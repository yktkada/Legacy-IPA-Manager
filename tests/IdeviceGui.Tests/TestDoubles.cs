using System.IO;
using IdeviceGui.Services;
using IdeviceGui.Services.Diagnostics;

namespace IdeviceGui.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// 测试替身：可编程、可阻塞、可统计调用次数，行为对齐真实实现的关键语义
// （尤其：取消时抛 OperationCanceledException —— CliRunner / ReadbackService 的真实行为，
//   QueueService 依赖它把"用户暂停/关窗"识别成 Paused 而不是 Failed）。
// 所有替身都可在后台线程被调用，统计字段用 Interlocked。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>把 QueueService 的 IProgress 回报记录下来，供断言状态序列 / 日志（线程安全）。</summary>
internal sealed class RecordingProgress<T> : IProgress<T>
{
    private readonly object _gate = new();
    private readonly List<T> _items = new();

    public void Report(T value)
    {
        lock (_gate) _items.Add(value);
    }

    public IReadOnlyList<T> Snapshot()
    {
        lock (_gate) return _items.ToArray();
    }

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }
}

/// <summary>
/// ICliRunner 替身。
/// <para>
/// 可编程点：<see cref="ProbeFactory"/>（设备在线/离线）、<see cref="InstallResultFactory"/> /
/// <see cref="UninstallResultFactory"/>（成功 / 失败 / ERROR 行）、<see cref="QueryDiskUsageFactory"/>。
/// <see cref="BlockInstall"/> + <see cref="InstallGate"/> 让安装挂住，直到 <see cref="ReleaseInstall"/> 或被取消。
/// 阻塞用 <c>Task.WaitAsync(ct)</c>：取消时抛 <see cref="OperationCanceledException"/>，与真实 CliRunner 一致。
/// </para>
/// </summary>
internal sealed class FakeCliRunner : ICliRunner
{
    private readonly object _gate = new();
    private readonly List<(string Udid, string IpaPath)> _installInvocations = new();
    private readonly List<(string Udid, string BundleId)> _uninstallInvocations = new();

    private int _listUdidsCalls;
    private int _probeCalls;
    private int _installCalls;
    private int _uninstallCalls;
    private int _diskUsageCalls;
    private int _listInstalledXmlCalls;

    private TaskCompletionSource _installGate = NewGate();
    private TaskCompletionSource _installStarted = NewGate();

    private static TaskCompletionSource NewGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ── 可编程配置 ────────────────────────────────────────────────────────────

    /// <summary>默认探测结果：一台在线假设备（ProductType/Version 用于身份校验，测试里通常不登记身份）。</summary>
    public DeviceProbe DefaultProbe { get; set; } = new(true, "Fake Device", "iPhone9,9", "8.0", "armv7");

    /// <summary>ProbeAsync 的行为（默认返回 <see cref="DefaultProbe"/>）。测试 4 用它在离线/在线之间切换。</summary>
    public Func<string, DeviceProbe>? ProbeFactory { get; set; }

    /// <summary>安装结果（按 udid + ipaPath 编程；默认成功）。测试 3 / 6 用。</summary>
    public Func<string, string, InstallOutcome>? InstallResultFactory { get; set; }

    /// <summary>卸载结果（默认成功）。</summary>
    public Func<string, string, UninstallOutcome>? UninstallResultFactory { get; set; }

    /// <summary>容量查询结果（默认充足，避免空间检查拦截安装）。</summary>
    public Func<string, DiskUsage>? QueryDiskUsageFactory { get; set; }

    /// <summary>M4-b4：电池三条（电量 / 健康 / 生产商）的可编程点；给 null 走 Unknown。</summary>
    public Func<string, BatteryUsage>? BatteryUsageFactory { get; set; }

    public Func<string, BatteryHealth>? BatteryHealthFactory { get; set; }

    public Func<string, BatterySpec>? BatterySpecFactory { get; set; }

    /// <summary>true 时 InstallAsync 挂住等 <see cref="InstallGate"/>；用于"暂停时正在安装"。</summary>
    public bool BlockInstall { get; set; }

    /// <summary>
    /// 取消后、抛 <see cref="OperationCanceledException"/> 之前的减速。
    /// 模拟真实 CliRunner 取消时"杀进程树 + 异常展开"的耗时，用来**稳定复现**
    /// "暂停后立刻继续"的竞态窗口（0 = 不减速）。
    /// </summary>
    public TimeSpan CancelUnwindDelay { get; set; } = TimeSpan.Zero;

    /// <summary>ListUdidsAsync 返回（默认一台设备）。</summary>
    public IReadOnlyList<string> Udids { get; set; } = new[] { "fake-udid-0001" };

    // ── 统计 ──────────────────────────────────────────────────────────────────

    public int ListUdidsCalls => Volatile.Read(ref _listUdidsCalls);
    public int ProbeCalls => Volatile.Read(ref _probeCalls);
    public int InstallCalls => Volatile.Read(ref _installCalls);
    public int UninstallCalls => Volatile.Read(ref _uninstallCalls);
    public int QueryDiskUsageCalls => Volatile.Read(ref _diskUsageCalls);
    public int ListInstalledXmlCalls => Volatile.Read(ref _listInstalledXmlCalls);

    public IReadOnlyList<(string Udid, string IpaPath)> InstallInvocations
    {
        get { lock (_gate) return _installInvocations.ToArray(); }
    }

    public IReadOnlyList<(string Udid, string BundleId)> UninstallInvocations
    {
        get { lock (_gate) return _uninstallInvocations.ToArray(); }
    }

    // ── 阻塞控制（每轮测试重置一次）─────────────────────────────────────────

    /// <summary>安装开始（挂住之前）完成；测试可等待它确认"已经进到安装里"。</summary>
    public Task InstallStarted => _installStarted.Task;

    /// <summary>重置安装闸门（新一轮测试前调用），并清掉"已开始"信号。</summary>
    public void ResetInstallGate()
    {
        _installGate = NewGate();
        _installStarted = NewGate();
    }

    /// <summary>放行当前挂住的安装（不取消时用）。</summary>
    public void ReleaseInstall() => _installGate.TrySetResult();

    // ── ICliRunner ────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<string>> ListUdidsAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _listUdidsCalls);
        if (ct.IsCancellationRequested) return Task.FromCanceled<IReadOnlyList<string>>(ct);
        return Task.FromResult(Udids);
    }

    public Task<DeviceProbe> ProbeAsync(string udid, CancellationToken ct)
    {
        Interlocked.Increment(ref _probeCalls);
        if (ct.IsCancellationRequested) return Task.FromCanceled<DeviceProbe>(ct);
        var probe = ProbeFactory?.Invoke(udid) ?? DefaultProbe;
        return Task.FromResult(probe);
    }

    public async Task<InstallOutcome> InstallAsync(
        string udid,
        string ipaPath,
        IProgress<string>? log,
        IProgress<double>? percent,
        CancellationToken ct)
    {
        Interlocked.Increment(ref _installCalls);
        lock (_gate) _installInvocations.Add((udid, ipaPath));
        percent?.Report(0);
        _installStarted.TrySetResult();

        if (BlockInstall)
        {
            // 与真实 CliRunner 一致：取消时抛 OperationCanceledException（由 WaitAsync 抛出）
            try
            {
                await _installGate.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (CancelUnwindDelay > TimeSpan.Zero)
                {
                    await Task.Delay(CancelUnwindDelay, CancellationToken.None).ConfigureAwait(false);
                }

                throw;
            }
        }
        else
        {
            ct.ThrowIfCancellationRequested();
        }

        log?.Report($"[fake] 安装完成 {Path.GetFileName(ipaPath)}");
        percent?.Report(100);
        return InstallResultFactory?.Invoke(udid, ipaPath) ?? new InstallOutcome(0, false, null, false);
    }

    public Task<UninstallOutcome> UninstallAsync(
        string udid,
        string bundleId,
        IProgress<string>? log,
        CancellationToken ct)
    {
        Interlocked.Increment(ref _uninstallCalls);
        lock (_gate) _uninstallInvocations.Add((udid, bundleId));
        if (ct.IsCancellationRequested) return Task.FromCanceled<UninstallOutcome>(ct);
        log?.Report($"[fake] 卸载完成 {bundleId}");
        return Task.FromResult(
            UninstallResultFactory?.Invoke(udid, bundleId) ?? new UninstallOutcome(0, false, null, false));
    }

    public Task<DiskUsage> QueryDiskUsageAsync(string udid, CancellationToken ct)
    {
        Interlocked.Increment(ref _diskUsageCalls);
        if (ct.IsCancellationRequested) return Task.FromCanceled<DiskUsage>(ct);
        var usage = QueryDiskUsageFactory?.Invoke(udid)
                    ?? new DiskUsage(true, 20L * 1024 * 1024 * 1024, 10L * 1024 * 1024 * 1024);
        return Task.FromResult(usage);
    }

    // M4-b4：设备信息"慢信息"（默认全 Unknown，测试按需注入）

    public Task<BatteryUsage> ReadBatteryUsageAsync(string udid, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<BatteryUsage>(ct);
        return Task.FromResult(BatteryUsageFactory?.Invoke(udid) ?? BatteryUsage.Unknown);
    }

    public Task<BatteryHealth> ReadBatteryHealthAsync(string udid, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<BatteryHealth>(ct);
        return Task.FromResult(BatteryHealthFactory?.Invoke(udid) ?? BatteryHealth.Unknown);
    }

    public Task<BatterySpec> ReadBatterySpecAsync(string udid, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<BatterySpec>(ct);
        return Task.FromResult(BatterySpecFactory?.Invoke(udid) ?? BatterySpec.Unknown);
    }

    /// <summary>回读 XML 的编程入口（默认返回空 plist 数组）。用于缓存竞态 / 并发测试。</summary>
    public Func<string, CancellationToken, Task<string?>>? ListInstalledXmlFactory { get; set; }

    public Task<string?> ListInstalledXmlAsync(string udid, CancellationToken ct)
    {
        Interlocked.Increment(ref _listInstalledXmlCalls);
        if (ct.IsCancellationRequested) return Task.FromCanceled<string?>(ct);
        var factory = ListInstalledXmlFactory;
        return factory is not null
            ? factory(udid, ct)
            : Task.FromResult<string?>("<?xml version=\"1.0\"?><plist><array/></plist>");
    }
}

/// <summary>
/// IAfcHelper 替身：默认"清理成功 + 空间充足"，可配置 clean/space 结果与统计调用次数。
/// 取消时返回 canceled Task（OperationCanceledException 语义）。
/// </summary>
internal sealed class FakeAfcHelper : IAfcHelper
{
    private int _cleanCalls;
    private int _spaceCalls;

    public AfcCleanOutcome CleanResult { get; set; } = new(0, false, false, null);
    public AfcSpaceOutcome SpaceResult { get; set; } =
        new(false, false, 20L * 1024 * 1024 * 1024, 10L * 1024 * 1024 * 1024, null);

    public int CleanCalls => Volatile.Read(ref _cleanCalls);
    public int SpaceCalls => Volatile.Read(ref _spaceCalls);

    public Task<AfcCleanOutcome> CleanStagingAsync(string udid, IProgress<string>? log, CancellationToken ct)
    {
        Interlocked.Increment(ref _cleanCalls);
        if (ct.IsCancellationRequested) return Task.FromCanceled<AfcCleanOutcome>(ct);
        return Task.FromResult(CleanResult);
    }

    public Task<AfcSpaceOutcome> QuerySpaceAsync(string udid, CancellationToken ct)
    {
        Interlocked.Increment(ref _spaceCalls);
        if (ct.IsCancellationRequested) return Task.FromCanceled<AfcSpaceOutcome>(ct);
        return Task.FromResult(SpaceResult);
    }
}

/// <summary>
/// IReadbackService 替身：可随时设置"设备上已装哪些 App"、统计调用次数。
/// 取消时返回 canceled Task；Invalidate 只计数（测试不关心缓存语义）。
/// </summary>
internal sealed class FakeReadbackService : IReadbackService
{
    private readonly object _gate = new();
    private readonly List<InstalledApp> _apps = new();
    private int _listCalls;
    private int _invalidateCalls;

    public int ListCalls => Volatile.Read(ref _listCalls);
    public int InvalidateCalls => Volatile.Read(ref _invalidateCalls);

    /// <summary>设置设备上已安装集合（覆盖）。</summary>
    public void SetApps(params InstalledApp[] apps)
    {
        lock (_gate)
        {
            _apps.Clear();
            _apps.AddRange(apps);
        }
    }

    /// <summary>追加一个已装 App（版本未知时传空串 + versionKnown=false）。</summary>
    public void AddApp(string bundleId, string version, bool versionKnown = true) =>
        SetApps(CurrentApps().Append(new InstalledApp(bundleId, version, versionKnown)).ToArray());

    /// <summary>移除一个已装 App（卸载成功后用）。</summary>
    public void RemoveApp(string bundleId) =>
        SetApps(CurrentApps()
            .Where(a => !string.Equals(a.BundleId, bundleId, StringComparison.OrdinalIgnoreCase))
            .ToArray());

    public IReadOnlyList<InstalledApp> CurrentApps()
    {
        lock (_gate) return _apps.ToArray();
    }

    /// <summary>编程入口：让回读抛异常 / 延迟（测试 NeedsConfirm 与并发）。</summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<InstalledApp>>>? ListFactory { get; set; }

    public Task<IReadOnlyList<InstalledApp>> ListInstalledAsync(string udid, CancellationToken ct)
    {
        Interlocked.Increment(ref _listCalls);
        if (ct.IsCancellationRequested) return Task.FromCanceled<IReadOnlyList<InstalledApp>>(ct);
        var factory = ListFactory;
        return factory is not null ? factory(udid, ct) : Task.FromResult(CurrentApps());
    }

    public void Invalidate(string udid) => Interlocked.Increment(ref _invalidateCalls);
}
