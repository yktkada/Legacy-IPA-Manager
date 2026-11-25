using System.Diagnostics;
using System.IO;
using IdeviceGui.Resources;
using IdeviceGui.Services;
using IdeviceGui.ViewModels;
using Xunit;
using Xunit.Sdk;

namespace IdeviceGui.Tests;

/// <summary>
/// QueueService（队列引擎）的离线单元测试：用可编程替身驱动真实状态机，不碰真机。
/// <para>
/// 每条用例都对应真机上验过的行为（B1/B3 联调），断言失败时把最终 Status/Note 和调用计数一起带出来，
/// 竞态类失败才定位得动。测试必须快：只用 Task.Delay 轮询，不用 Thread.Sleep，单用例 &lt; 2 s。
/// </para>
/// </summary>
public sealed class QueueServiceTests : IDisposable
{
    private const string BundleId = "com.fake.app";

    private readonly List<string> _tempFiles = new();

    // ── 测试夹具 ─────────────────────────────────────────────────────────────

    /// <summary>一套 fake + 一个真实 QueueService；DisposeAsync 会 Shutdown，避免失败用例泄漏后台泵。</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public const string Udid = "fake-udid-0001";

        public FakeCliRunner Cli { get; }
        public FakeAfcHelper Afc { get; }
        public FakeReadbackService Readback { get; }
        public RecordingProgress<QueueEvent> Events { get; } = new();
        public RecordingProgress<LogLine> Log { get; } = new();
        public QueueService Service { get; }

        public Harness(bool useReadback = true)
        {
            Cli = new FakeCliRunner { Udids = new[] { Udid } };
            Afc = new FakeAfcHelper();
            Readback = new FakeReadbackService();
            Service = new QueueService(Cli, Afc, useReadback ? Readback : null, Events, Log)
            {
                ReadbackRetryDelay = TimeSpan.Zero,
            };
        }

        public QueueItem EnqueueInstall(string ipaPath, string bundleId = BundleId, string? version = "1.0", long? appSize = null, bool? hadInstalledBefore = null)
            => EnqueueInstallResult(ipaPath, bundleId, version, appSize, hadInstalledBefore: hadInstalledBefore).Item;

        public EnqueueResult EnqueueInstallResult(string ipaPath, string bundleId = BundleId, string? version = "1.0", long? appSize = null, string? minOs = null, bool? hadInstalledBefore = null)
            => Service.Enqueue(Udid, JobKind.Install, bundleId, ipaPath, "pkg-" + bundleId, version, appSize, minOs, hadInstalledBefore);

        /// <summary>断言/超时信息里统一拼上最终状态，竞态失败可定位。</summary>
        public string Describe(QueueItem? item = null)
        {
            var text = item is null
                ? "条目=<未入队>"
                : $"Status={item.Status} Note={item.Note ?? "<null>"} Percent={item.Percent:0} " +
                  $"WasInterrupted={item.WasInterrupted}";
            return $"{text} | IsRunning={Service.IsRunning(Udid)} " +
                   $"InstallCalls={Cli.InstallCalls} UninstallCalls={Cli.UninstallCalls} ProbeCalls={Cli.ProbeCalls} " +
                   $"ReadbackCalls={Readback.ListCalls}";
        }

        public QueueItem? Find(Guid jobId) => Service.Snapshot(Udid).FirstOrDefault(x => x.Id == jobId);

        public IReadOnlyList<QueueStatus> StatusSequence(Guid jobId) =>
            Events.Snapshot().OfType<JobStatusEvent>()
                .Where(e => e.JobId == jobId)
                .Select(e => e.Status)
                .ToList();

        /// <summary>队列是否上报过"队列异常"（单设备异常不该逃逸，断言它没有出现）。</summary>
        public bool HasPumpException() =>
            Events.Snapshot().OfType<DeviceRunStateEvent>().Any(e => e.Note?.Contains("队列异常") == true);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Service.ShutdownAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // 清理路径不抛
            }
        }
    }

    private string NewIpaFile()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "qtest-" + Guid.NewGuid().ToString("N") + ".ipa");
        File.WriteAllBytes(path, new byte[1024]);   // 只为通过"文件存在"预检
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>轮询等待条件成立；统一走 <see cref="TestWait"/>（有上限，超时报错带 describe()）。</summary>
    private static Task WaitUntilAsync(Func<bool> condition, Func<string> describe, int timeoutMs = 3000) =>
        TestWait.UntilAsync(condition, describe, timeoutMs);

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch { /* 测试清理不抛 */ }
        }
    }

    // ── 1. 暂停后立刻继续不卡死（20 轮，泵句柄竞态）─────────────────────────

    // ── 取消（M4-e1 第 2 步）：上传中杀进程 / 安装段装完自动卸载 ────────────────
    [Fact]
    public async Task 上传中取消_状态为已取消且设备不判离线()
    {
        await using var h = new Harness();
        var ipa = NewIpaFile();
        h.Cli.BlockInstall = true;
        h.Cli.ResetInstallGate();
        var item = h.EnqueueInstall(ipa);

        h.Service.StartPump(Harness.Udid);
        await h.Cli.InstallStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var outcome = h.Service.Cancel(Harness.Udid, item.Id);
        Assert.Equal(QueueService.CancelOutcome.CancelledRunning, outcome);

        await WaitUntilAsync(
            () => h.Service.Snapshot(Harness.Udid).First(x => x.Id == item.Id).Status == QueueStatus.Cancelled,
            () => "上传中取消后没变成 Cancelled：" + h.Describe(item));

        Assert.Equal(QueueStatus.Cancelled, h.Service.Snapshot(Harness.Udid).First(x => x.Id == item.Id).Status);
        // 取消 ≠ 失败：条目停在 Cancelled，不是 Failed/Paused
        Assert.NotEqual(QueueStatus.Failed, h.Service.Snapshot(Harness.Udid).First(x => x.Id == item.Id).Status);
    }    // ── 队列排序（M4-e2）：按峰值占用（压缩 + 解压）降序，未知排最后 ────────────
    [Fact]
    public async Task 入队_待执行区按峰值占用降序()
    {
        await using var h = new Harness();
        var small = h.EnqueueInstall(NewIpaFile(), bundleId: "com.test.small", appSize: 1_000);
        var big = h.EnqueueInstall(NewIpaFile(), bundleId: "com.test.big", appSize: 5_000);
        var mid = h.EnqueueInstall(NewIpaFile(), bundleId: "com.test.mid", appSize: 3_000);

        var order = h.Service.Snapshot(Harness.Udid).Select(x => x.Id).ToList();
        Assert.Equal(new[] { big.Id, mid.Id, small.Id }, order);
    }

    [Fact]
    public async Task 入队_解压体积未知排最后()
    {
        await using var h = new Harness();
        var unknown = h.EnqueueInstall(NewIpaFile(), bundleId: "com.test.unknown", appSize: null);
        var known = h.EnqueueInstall(NewIpaFile(), bundleId: "com.test.known", appSize: 100_000);

        var order = h.Service.Snapshot(Harness.Udid).Select(x => x.Id).ToList();
        Assert.Equal(new[] { known.Id, unknown.Id }, order);
    }    // ── 取消（M4-e1 第 1 步）：只有 Pending 能立刻取消 ──────────────────────
    [Fact]
    public async Task 取消待执行条目_状态变为已取消且不影响其它排队项()
    {
        await using var h = new Harness();
        var first = h.EnqueueInstall(NewIpaFile(), bundleId: "com.test.cancel.a");
        var second = h.EnqueueInstall(NewIpaFile(), bundleId: "com.test.cancel.b");

        Assert.True(h.Service.CancelPending(Harness.Udid, first.Id));

        var snapshot = h.Service.Snapshot(Harness.Udid);
        Assert.Equal(QueueStatus.Cancelled, snapshot.First(x => x.Id == first.Id).Status);
        Assert.Equal(QueueStatus.Pending, snapshot.First(x => x.Id == second.Id).Status);
    }

    // ── 审查修复回归（2026-11-01）：取消字段重置 / 覆盖安装不自动卸载 ──────────
    [Fact]
    public async Task Reset之后_Uploading回到true且CancelAfterInstall清零()
    {
        await using var h = new Harness();
        var item = h.EnqueueInstall(NewIpaFile());

        // 模拟"装过一次"：安装段把 Uploading 置 false，并登记过自动卸载
        item.Uploading = false;
        item.CancelAfterInstall = true;

        var again = h.Service.Enqueue(Harness.Udid, JobKind.Install, item.BundleId, item.FilePath,
            packageKey: item.PackageKey, packageVersion: "1.0");

        Assert.Equal(EnqueueDisposition.Reset, again.Disposition);
        Assert.True(again.Item.Uploading);
        Assert.False(again.Item.CancelAfterInstall);
    }

    [Fact]
    public async Task 覆盖安装取消_装完不自动卸载()
    {
        await using var h = new Harness();
        var ipa = NewIpaFile();
        h.Cli.InstallResultFactory = (_, _) =>
        {
            h.Readback.AddApp(BundleId, "1.0");
            return new InstallOutcome(0, false, null, false);
        };
        h.Cli.BlockInstall = true;
        h.Cli.ResetInstallGate();

        var item = h.EnqueueInstall(ipa, hadInstalledBefore: true);
        h.Service.StartPump(Harness.Udid);

        await h.Cli.InstallStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        item.Uploading = false;                       // 模拟已进入安装段
        var outcome = h.Service.Cancel(Harness.Udid, item.Id);
        Assert.Equal(QueueService.CancelOutcome.ScheduledAfterInstall, outcome);

        h.Cli.ReleaseInstall();
        await WaitUntilAsync(
            () => item.Status == QueueStatus.Cancelled,
            () => "覆盖安装取消后应收敛到 Cancelled：" + h.Describe(item));

        Assert.Contains("未自动卸载", item.Note);
        Assert.Equal(0, h.Cli.UninstallCalls);
    }

    [Fact]
    public async Task 普通安装取消_装完自动卸载()
    {
        await using var h = new Harness();
        var ipa = NewIpaFile();
        h.Cli.InstallResultFactory = (_, _) =>
        {
            h.Readback.AddApp(BundleId, "1.0");
            return new InstallOutcome(0, false, null, false);
        };
        h.Cli.UninstallResultFactory = (_, _) =>
        {
            h.Readback.RemoveApp(BundleId);
            return new UninstallOutcome(0, false, null, false);
        };
        h.Cli.BlockInstall = true;
        h.Cli.ResetInstallGate();

        var item = h.EnqueueInstall(ipa);
        h.Service.StartPump(Harness.Udid);

        await h.Cli.InstallStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        item.Uploading = false;
        var outcome = h.Service.Cancel(Harness.Udid, item.Id);
        Assert.Equal(QueueService.CancelOutcome.ScheduledAfterInstall, outcome);

        h.Cli.ReleaseInstall();
        await WaitUntilAsync(
            () => item.Status == QueueStatus.Cancelled && item.Note is not null && item.Note.Contains("已卸载"),
            () => "取消后应装完自动卸载：" + h.Describe(item));

        Assert.Equal(1, h.Cli.UninstallCalls);
    }

    [Fact]
    public async Task 同一个包只保留一行_但未结束的安装不能被卸载覆盖()
    {
        await using var h = new Harness();
        var item = h.EnqueueInstall(NewIpaFile());

        // 同 bundle、不同路径再入队：不新增行，复用同一 QueueItem（Reset）
        var again = h.Service.Enqueue(Harness.Udid, JobKind.Install, item.BundleId,
            @"C:\tmp\another-copy.ipa", @"C:\tmp\another-copy.ipa", packageVersion: "2.0");
        Assert.Equal(EnqueueDisposition.Reset, again.Disposition);
        Assert.Equal(item.Id, again.Item.Id);
        Assert.Single(h.Service.Snapshot(Harness.Udid));

        // 审查 B2（2026-11-03 修）：安装还没结束 → 卸载**不许覆盖**，返回 KindConflict 且原行不动
        var uninstall = h.Service.Enqueue(Harness.Udid, JobKind.Uninstall, item.BundleId,
            filePath: null, packageKey: null);
        Assert.Equal(EnqueueDisposition.KindConflict, uninstall.Disposition);
        Assert.Equal(item.Id, uninstall.Item.Id);
        Assert.Equal(JobKind.Install, uninstall.Item.Kind);
        Assert.Equal(item.FilePath, uninstall.Item.FilePath);   // 路径/版本没被清空

        var rows = h.Service.Snapshot(Harness.Udid);
        Assert.Single(rows);
        Assert.Equal(JobKind.Install, rows[0].Kind);
    }

    [Fact]
    public async Task 安装Done之后_卸载复用同一行并切Kind()
    {
        await using var h = new Harness();
        var item = h.EnqueueInstall(NewIpaFile());
        item.Status = QueueStatus.Done;   // 模拟已装完（界面上的"已装"行）

        var uninstall = h.Service.Enqueue(Harness.Udid, JobKind.Uninstall, item.BundleId,
            filePath: null, packageKey: null);

        Assert.Equal(EnqueueDisposition.Reset, uninstall.Disposition);
        Assert.Equal(item.Id, uninstall.Item.Id);
        Assert.Equal(JobKind.Uninstall, uninstall.Item.Kind);
        Assert.Null(uninstall.Item.FilePath);
        Assert.Single(h.Service.Snapshot(Harness.Udid));
    }

        [Fact]
    public async Task 取消后重新入队_是Reset而不是AlreadyRunning()
    {
        await using var h = new Harness();
        var item = h.EnqueueInstall(NewIpaFile());

        Assert.True(h.Service.CancelPending(Harness.Udid, item.Id));

        var again = h.Service.Enqueue(Harness.Udid, JobKind.Install, item.BundleId, item.FilePath,
            packageKey: item.PackageKey, packageVersion: "1.0");
        Assert.Equal(EnqueueDisposition.Reset, again.Disposition);
        Assert.Equal(QueueStatus.Pending, again.Item.Status);
    }

    [Fact]
    public async Task 取消同一个条目两次_第二次返回false()
    {
        await using var h = new Harness();
        var item = h.EnqueueInstall(NewIpaFile());

        Assert.True(h.Service.CancelPending(Harness.Udid, item.Id));
        Assert.False(h.Service.CancelPending(Harness.Udid, item.Id));
    }

    [Fact]
    public async Task 取消未知条目_返回false()
    {
        await using var h = new Harness();
        Assert.False(h.Service.CancelPending(Harness.Udid, Guid.NewGuid()));
    }

    [Fact]
    public void 队列状态文本_已取消()
        => Assert.Equal(Strings.Queue_Status_Cancelled, QueueStatus.Cancelled.ToText());    [Fact]
    public async Task PauseThenImmediateResume_TwentyRounds_AllEndDone()
    {
        await using var h = new Harness();
        var ipa = NewIpaFile();

        // 安装成功后把它写进回读结果，否则最后一步"回读确认"会判失败
        h.Cli.InstallResultFactory = (_, _) =>
        {
            h.Readback.AddApp(BundleId, "1.0");
            return new InstallOutcome(0, false, null, false);
        };

        // 真实 CliRunner 取消时要"杀进程树"再抛 OCE（几十~几百 ms），这里用替身的减速把这个窗口放大，
        // 让"Pause 之后立刻 Resume"的竞态**稳定复现**，不靠进程调度的运气。
        // 20 ms 已远大于测试线程 Pause→Resume 的间隔，同时保证 20 轮总耗时 < 2 s。
        h.Cli.CancelUnwindDelay = TimeSpan.FromMilliseconds(20);

        for (var round = 1; round <= 20; round++)
        {
            h.Readback.SetApps();                 // 每轮从"未安装"开始，保证继续后真的重装
            h.Cli.ResetInstallGate();
            h.Cli.BlockInstall = true;

            var item = h.EnqueueInstall(ipa);
            h.Service.StartPump(Harness.Udid);

            await WaitUntilAsync(
                () => item.Status == QueueStatus.Installing,
                () => $"第 {round} 轮没进入 Installing：{h.Describe(item)}");

            // 关键：Pause 之后不等旧泵收尾，立刻 Resume（复现"重启泵时旧句柄还没清掉"的竞态）
            h.Service.Pause(Harness.Udid);
            h.Service.Resume(Harness.Udid);

            h.Cli.BlockInstall = false;
            h.Cli.ReleaseInstall();               // 放行可能已经挂在闸门上的第二次安装

            await WaitUntilAsync(
                () => item.Status is QueueStatus.Done or QueueStatus.Failed or QueueStatus.Paused,
                () => $"第 {round} 轮卡住没收敛：{h.Describe(item)}");

            Assert.True(item.Status == QueueStatus.Done,
                $"第 {round} 轮最终不是 Done：{h.Describe(item)}");
            Assert.False(h.HasPumpException(), $"第 {round} 轮泵异常：{h.Describe(item)}");
        }
    }

    // ── 1b. 连续 Pause/Resume 3 次：重置标志被多次覆盖也不丢泵 ────────────────
    [Fact]
    public async Task RepeatedPauseResumeThreeTimes_WhileInstalling_EndsDone()
    {
        await using var h = new Harness();
        var ipa = NewIpaFile();
        h.Cli.InstallResultFactory = (_, _) =>
        {
            h.Readback.AddApp(BundleId, "1.0");
            return new InstallOutcome(0, false, null, false);
        };
        h.Cli.CancelUnwindDelay = TimeSpan.FromMilliseconds(20);

        h.Cli.ResetInstallGate();
        h.Cli.BlockInstall = true;

        var item = h.EnqueueInstall(ipa);
        h.Service.StartPump(Harness.Udid);

        await WaitUntilAsync(
            () => item.Status == QueueStatus.Installing,
            () => $"没进入 Installing：{h.Describe(item)}");

        // 连续 3 次「Pause + 立刻 Resume」：每次都在旧泵取消展开的窗口内，验证重启标志被反复覆盖也不会丢泵
        for (var i = 1; i <= 3; i++)
        {
            h.Service.Pause(Harness.Udid);
            h.Service.Resume(Harness.Udid);
            await Task.Delay(5, TestContext.Current.CancellationToken);   // 让取消/重启发生，但**不等**旧泵收尾
            Assert.False(h.HasPumpException(), $"第 {i} 次 Pause/Resume 后泵异常：{h.Describe(item)}");
        }

        h.Cli.BlockInstall = false;
        h.Cli.ReleaseInstall();

        await WaitUntilAsync(
            () => item.Status is QueueStatus.Done or QueueStatus.Failed or QueueStatus.Paused,
            () => $"连续 Pause/Resume 后卡住：{h.Describe(item)}",
            timeoutMs: 5000);

        Assert.True(item.Status == QueueStatus.Done,
            $"连续 Pause/Resume 后应为 Done：{h.Describe(item)}");
        Assert.False(h.HasPumpException(), $"泵不应抛异常：{h.Describe(item)}");
    }

    // ── 2. 用户暂停 → 继续先回读 → 已装同版本则跳过重装 ──────────────────────
    [Fact]
    public async Task ResumeAfterUserPause_ReadbackShowsInstalled_SkipsReinstall()
    {
        await using var h = new Harness();
        h.Readback.AddApp(BundleId, "1.0");       // 设备上已装同版本
        var ipa = NewIpaFile();

        h.Cli.ResetInstallGate();
        h.Cli.BlockInstall = true;

        var item = h.EnqueueInstall(ipa, BundleId, "1.0");
        h.Service.StartPump(Harness.Udid);

        await WaitUntilAsync(
            () => item.Status == QueueStatus.Installing,
            () => $"没进入 Installing：{h.Describe(item)}");

        h.Service.Pause(Harness.Udid);
        await WaitUntilAsync(
            () => item.Status == QueueStatus.Paused,
            () => $"暂停没生效：{h.Describe(item)}");

        var installCallsAtPause = h.Cli.InstallCalls;
        h.Service.Resume(Harness.Udid);

        await WaitUntilAsync(
            () => item.Status == QueueStatus.Done,
            () => $"继续后没完成：{h.Describe(item)}");

        var statuses = h.StatusSequence(item.Id);
        Assert.True(statuses.Contains(QueueStatus.Readback),
            $"继续后应先走 Readback；实际序列={string.Join("→", statuses)}；{h.Describe(item)}");
        Assert.Null(item.Note);   // 成功后不保留备注（2026-11-08 用户定）
        Assert.Equal(installCallsAtPause, h.Cli.InstallCalls);   // 回读已装 → 不再调安装
    }

    // ── 3. 坏包失败后继续下一个（不是整队停）────────────────────────────────
    [Fact]
    public async Task BadPackageFails_ThenNextPackageStillRuns()
    {
        await using var h = new Harness();
        const string goodBundle = "com.fake.good";
        h.Readback.AddApp(goodBundle, "1.0");

        var badIpa = NewIpaFile();
        var goodIpa = NewIpaFile();
        h.Cli.InstallResultFactory = (_, path) => path == badIpa
            ? new InstallOutcome(1, true, "ERROR: zip_open: fake bad package", false)
            : new InstallOutcome(0, false, null, false);

        var bad = h.EnqueueInstall(badIpa, "com.fake.bad", "0");
        var good = h.EnqueueInstall(goodIpa, goodBundle, "1.0");
        h.Service.StartPump(Harness.Udid);

        await WaitUntilAsync(
            () => bad.Status is QueueStatus.Failed or QueueStatus.Done,
            () => $"坏包没收敛：{h.Describe(bad)}");
        Assert.True(bad.Status == QueueStatus.Failed, $"坏包应为 Failed：{h.Describe(bad)}");
        Assert.True(bad.Note?.Contains("安装未完成") == true, $"坏包 Note 应说明未完成：{h.Describe(bad)}");

        await WaitUntilAsync(
            () => good.Status is QueueStatus.Done or QueueStatus.Failed,
            () => $"坏包失败后好包没跑：{h.Describe(good)}");
        Assert.True(good.Status == QueueStatus.Done, $"好包应为 Done：{h.Describe(good)}");
        Assert.Equal(3, h.Cli.InstallCalls);   // 坏包首次 + 自动再入队一次 + 好包
    }

    // ── 4. 设备离线 → Paused 且泵停住；恢复后 Resume 能继续 ───────────────────
    [Fact]
    public async Task DeviceOffline_PausesAndStopsPump_ResumeAfterRecoveryContinues()
    {
        await using var h = new Harness();
        var ipa = NewIpaFile();

        h.Cli.ProbeFactory = _ => DeviceProbe.Missing;      // 离线
        var item = h.EnqueueInstall(ipa);
        h.Service.StartPump(Harness.Udid);

        await WaitUntilAsync(
            () => item.Status == QueueStatus.Paused,
            () => $"离线没变成 Paused：{h.Describe(item)}");
        await WaitUntilAsync(
            () => !h.Service.IsRunning(Harness.Udid),
            () => $"离线后泵应停住：{h.Describe(item)}");
        Assert.False(h.HasPumpException(), $"离线不应冒泡异常：{h.Describe(item)}");

        // 设备恢复：先回读为空（走重装），安装成功后把 App 写进回读，最终回读确认才能过
        h.Cli.ProbeFactory = null;                          // 恢复默认在线
        h.Readback.SetApps();
        h.Cli.InstallResultFactory = (_, _) =>
        {
            h.Readback.AddApp(BundleId, "1.0");
            return new InstallOutcome(0, false, null, false);
        };

        h.Service.Resume(Harness.Udid);
        await WaitUntilAsync(
            () => item.Status == QueueStatus.Done,
            () => $"设备恢复后 Resume 没继续：{h.Describe(item)}");
        Assert.Equal(1, h.Cli.InstallCalls);
        Assert.False(h.HasPumpException(), $"恢复路径不应有泵异常：{h.Describe(item)}");
    }

    // ── 5. 取消能力与 QueueStatusKinds.IsCancellable 一致（T1）─────────────────
    [Fact]
    public async Task Cancel_暂停与待确认不可取消_状态不变()
    {
        await using var h = new Harness();
        var item = h.EnqueueInstall(NewIpaFile());

        foreach (var status in new[] { QueueStatus.Paused, QueueStatus.NeedsConfirm, QueueStatus.Done })
        {
            item.Status = status;
            var outcome = h.Service.Cancel(Harness.Udid, item.Id);
            Assert.Equal(QueueService.CancelOutcome.NotCancellable, outcome);
            Assert.Equal(status, item.Status);
        }

        item.Status = QueueStatus.Cancelled;
        Assert.Equal(QueueService.CancelOutcome.AlreadyCancelled, h.Service.Cancel(Harness.Udid, item.Id));
    }

    [Fact]
    public async Task Cancel_卸载任务运行段不可取消_安装任务可排自动卸载()
    {
        await using var h = new Harness();
        var install = h.EnqueueInstall(NewIpaFile());

        // 安装任务：运行段 → 不能中止，排「装完自动卸载」
        install.Status = QueueStatus.Readback;
        Assert.Equal(QueueService.CancelOutcome.ScheduledAfterInstall, h.Service.Cancel(Harness.Udid, install.Id));
        Assert.True(install.CancelAfterInstall);

        // 卸载任务：Uninstalling 段 → 不可取消（旧手抄列表会误显示为可点）
        var uninstall = h.Service.Enqueue(Harness.Udid, JobKind.Uninstall, "com.fake.uninstall", null, null).Item;
        uninstall.Status = QueueStatus.Uninstalling;
        Assert.Equal(QueueService.CancelOutcome.NotCancellable, h.Service.Cancel(Harness.Udid, uninstall.Id));
        Assert.False(uninstall.CancelAfterInstall);
    }

    // ── 6. 卸载不存在的包 → 预检就拦下（Failed，且不真执行卸载）───────────────
    [Fact]
    public async Task UninstallMissingBundle_FailsAtPrecheck_WithoutCallingCli()
    {
        await using var h = new Harness();
        h.Readback.SetApps();                               // 设备上没有任何 App

        const string missing = "com.fake.not.installed";
        var item = h.Service.Enqueue(Harness.Udid, JobKind.Uninstall, missing, null, null, null).Item;
        h.Service.StartPump(Harness.Udid);

        await WaitUntilAsync(
            () => item.Status is QueueStatus.Failed or QueueStatus.Done,
            () => $"卸载不存在的包没收敛：{h.Describe(item)}");

        Assert.True(item.Status == QueueStatus.Failed, $"应判 Failed：{h.Describe(item)}");
        Assert.True(item.Note?.Contains("无需卸载") == true, $"Note 应含“无需卸载”：{h.Describe(item)}");
        Assert.Equal(0, h.Cli.UninstallCalls);              // 预检拦下，不该调用真实卸载
    }

    // ── 6. 安装报错但回读确认已装 → Done（不是 Failed）──────────────────────
    [Fact]
    public async Task InstallReportsError_ButReadbackConfirmsInstalled_EndsDone()
    {
        await using var h = new Harness();
        h.Readback.AddApp(BundleId, "1.0");
        var ipa = NewIpaFile();

        h.Cli.InstallResultFactory = (_, _) =>
            new InstallOutcome(1, true, "ERROR: fake install error", false);

        var item = h.EnqueueInstall(ipa, BundleId, "1.0");
        h.Service.StartPump(Harness.Udid);

        await WaitUntilAsync(
            () => item.Status is QueueStatus.Done or QueueStatus.Failed,
            () => $"报错但已装的情况没收敛：{h.Describe(item)}");

        Assert.True(item.Status == QueueStatus.Done, $"回读已装应判 Done：{h.Describe(item)}");
        Assert.Null(item.Note);   // 成功只显示“完成”，不保留回读备注
        Assert.Equal(1, h.Cli.InstallCalls);
    }

    // ── 7. 单 UDID 单泵：连调两次 StartPump 只起一个泵 ───────────────────────
    [Fact]
    public async Task StartPumpTwice_OnlyOnePumpExecutesQueue()
    {
        await using var h = new Harness();
        const string bundleA = "com.fake.a";
        const string bundleB = "com.fake.b";
        h.Readback.SetApps(
            new InstalledApp(bundleA, "1.0", true),
            new InstalledApp(bundleB, "1.0", true));

        var a = h.EnqueueInstall(NewIpaFile(), bundleA, "1.0");
        var b = h.EnqueueInstall(NewIpaFile(), bundleB, "1.0");

        h.Service.StartPump(Harness.Udid);
        h.Service.StartPump(Harness.Udid);          // 立刻再调一次：应复用已有泵

        await WaitUntilAsync(
            () => a.Status == QueueStatus.Done && b.Status == QueueStatus.Done,
            () => $"两个条目没都完成：A={h.Describe(a)} B={h.Describe(b)}");

        Assert.Equal(2, h.Cli.InstallCalls);        // 每包只装一次，没有被第二个泵重复执行
        await WaitUntilAsync(
            () => !h.Service.IsRunning(Harness.Udid),
            () => $"队列跑完泵应退出：{h.Describe(b)}");
    }

    // ── 回读结果未知：不能当"没装"，也不能自动重装（体检报告 P0-2）────────────────
    [Fact]
    public async Task ReadbackThrows_DeviceOnline_ThreeAttemptsThenFailed()
    {
        var h = new Harness();
        h.Readback.ListFactory = (_, _) => throw new IOException("回读超时（测试）");
        var item = h.EnqueueInstall(NewIpaFile());

        h.Service.StartPump(Harness.Udid);

        await WaitUntilAsync(
            () => item.Status == QueueStatus.Failed,
            () => $"回读异常重试后应失败：{h.Describe(item)}");

        Assert.Contains("未能验证结果", item.Note);

        Assert.Equal(2, h.Cli.InstallCalls);   // 首次安装 + 自动再入队一次
    }

    [Fact]
    public async Task ReadbackThrows_DeviceOffline_PausesInsteadOfFailing()
    {
        var h = new Harness();
        var probes = 0;
        h.Cli.ProbeFactory = _ =>
        {
            // 第 1 次是任务前校验；回读异常后的 SafeProbe（第 2 次）返回离线
            var n = Interlocked.Increment(ref probes);
            return n <= 1
                ? h.Cli.DefaultProbe
                : new DeviceProbe(false, "Fake Device", "iPhone9,9", "8.0", "armv7");
        };
        h.Readback.ListFactory = (_, _) => throw new IOException("回读超时（测试）");
        var item = h.EnqueueInstall(NewIpaFile());

        h.Service.StartPump(Harness.Udid);

        await WaitUntilAsync(
            () => item.Status == QueueStatus.Paused,
            () => $"回读异常 + 设备离线应暂停：{h.Describe(item)}");

        Assert.True(item.WasInterrupted);
        Assert.Equal(1, h.Cli.InstallCalls);
    }

    // ── P1-3：在飞条目不被入队重置（建议稿 §4.1）────────────────────────────────
    [Fact]
    public async Task 安装中再次入队_不重置且只装一次()
    {
        var h = new Harness();
        h.Readback.AddApp(BundleId, "1.0");   // 回读要能看到它，否则装完会被判"设备上没有"
        h.Cli.BlockInstall = true;
        var path = NewIpaFile();
        var item = h.EnqueueInstall(path);

        h.Service.StartPump(Harness.Udid);
        await WaitUntilAsync(
            () => item.Status == QueueStatus.Installing,
            () => $"应进入安装中：{h.Describe(item)}");

        var again = h.EnqueueInstallResult(path);

        Assert.Equal(EnqueueDisposition.AlreadyRunning, again.Disposition);
        Assert.Equal(QueueStatus.Installing, again.CurrentStatus);
        Assert.Equal(QueueStatus.Installing, item.Status);      // 没有被重置回 Pending
        Assert.Same(item, again.Item);                          // 还是同一行
        Assert.Single(h.Service.Snapshot(Harness.Udid));

        h.Cli.ReleaseInstall();
        await WaitUntilAsync(() => item.Status == QueueStatus.Done, () => $"应正常完成：{h.Describe(item)}");
        Assert.Equal(1, h.Cli.InstallCalls);
    }

    [Fact]
    public async Task Failed_自动再入队后_可重置回待执行()
    {
        var h = new Harness();
        h.Cli.InstallResultFactory = (_, _) => new InstallOutcome(1, true, "ERROR: 坏包", false);
        var path = NewIpaFile();
        h.EnqueueInstall(path);

        h.Service.StartPump(Harness.Udid);
        await WaitUntilAsync(
            () => h.Service.Snapshot(Harness.Udid).Any(x => x.AutoRetryCount == 2 && x.Status == QueueStatus.Failed),
            () => "自动再入队一次后应再次失败：" + h.Describe());

        var retry = h.Service.Snapshot(Harness.Udid).Single(x => x.AutoRetryCount == 2);
        var again = h.EnqueueInstallResult(path);
        Assert.Equal(EnqueueDisposition.Reset, again.Disposition);
        Assert.Equal(QueueStatus.Pending, retry.Status);
    }

    [Fact]
    public void 待执行再入队_不新增行_只排到队尾()
    {
        var h = new Harness();
        var a = h.EnqueueInstall(NewIpaFile(), bundleId: "com.fake.a");
        var b = h.EnqueueInstall(NewIpaFile(), bundleId: "com.fake.b");
        Assert.True(a.OrderIndex < b.OrderIndex, "先入队的应在前面");

        var again = h.EnqueueInstallResult(a.FilePath!, bundleId: "com.fake.a");

        Assert.Equal(EnqueueDisposition.Reset, again.Disposition);
        Assert.Single(h.Service.Snapshot(Harness.Udid), x => x.BundleId == "com.fake.a");
        Assert.True(again.Item.OrderIndex > b.OrderIndex, "重排后应排到队尾（在 b 之后）");
    }

    // ── P0-1：恢复条目回读失败，不得继续安装（会覆盖清数据）──────────────────
    [Fact]
    public async Task 恢复条目_回读失败_判失败后自动再入队一次()
    {
        var h = new Harness();
        h.Readback.ListFactory = (_, _) => throw new IOException("回读超时（测试）");
        var path = NewIpaFile();
        var item = h.EnqueueInstall(path);

        // 模拟"曾被中断"：直接改状态，走恢复分支
        item.WasInterrupted = true;
        item.Status = QueueStatus.Pending;

        h.Service.StartPump(Harness.Udid);

        await WaitUntilAsync(
            () => item.Status is QueueStatus.Paused or QueueStatus.Failed,
            () => $"恢复回读失败应收敛到需确认/暂停：{h.Describe(item)}");

        Assert.Equal(QueueStatus.Failed, item.Status);                // 设备在线、3 次回读异常 → 失败
        Assert.Equal(1, h.Cli.InstallCalls);                          // 首次恢复失败不装；自动再入队后尝试一次
    }

    [Fact]
    public async Task 恢复条目_回读失败且离线_暂停()
    {
        var h = new Harness();
        var probes = 0;
        h.Cli.ProbeFactory = _ =>
        {
            var n = Interlocked.Increment(ref probes);
            return n <= 1
                ? h.Cli.DefaultProbe
                : new DeviceProbe(false, "Fake Device", "iPhone9,9", "8.0", "armv7");
        };
        h.Readback.ListFactory = (_, _) => throw new IOException("回读超时（测试）");
        var path = NewIpaFile();
        var item = h.EnqueueInstall(path);
        item.WasInterrupted = true;
        item.Status = QueueStatus.Pending;

        h.Service.StartPump(Harness.Udid);

        await WaitUntilAsync(
            () => item.Status == QueueStatus.Paused,
            () => $"恢复回读失败 + 离线应暂停：{h.Describe(item)}");

        Assert.True(item.WasInterrupted);
        Assert.Equal(0, h.Cli.InstallCalls);
    }

    // ── 执行前检查：源文件已被删 → 秒判失败，不拉起 ideviceinstaller ──────────
    [Fact]
    public async Task 执行前检查_源文件已删_秒判失败且不调用CLI()
    {
        var h = new Harness();
        var path = NewIpaFile();
        var item = h.EnqueueInstall(path);

        File.Delete(path);                       // 入队后文件被删（模拟"扫描后没有的包"）
        var sw = System.Diagnostics.Stopwatch.StartNew();

        h.Service.StartPump(Harness.Udid);
        await WaitUntilAsync(() => item.Status == QueueStatus.Failed,
            () => $"应判失败：{h.Describe(item)}");
        sw.Stop();

        Assert.Contains("源文件已不存在", item.Note);
        Assert.Equal(0, h.Cli.InstallCalls);      // 没白跑一次进程
        Assert.True(sw.ElapsedMilliseconds < 2000, $"应立刻失败（实际 {sw.ElapsedMilliseconds} ms）");
    }

    // ── 执行前检查：源文件被别的进程独占 → 200 ms 重试一次后判失败，不调 CLI ──
    [Fact]
    public async Task 执行前检查_源文件被占用_重试一次后判失败且不调用CLI()
    {
        var h = new Harness();
        var path = NewIpaFile();
        var item = h.EnqueueInstall(path);

        using var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        h.Service.StartPump(Harness.Udid);
        await WaitUntilAsync(() => item.Status == QueueStatus.Failed,
            () => $"应判失败：{h.Describe(item)}");
        sw.Stop();

        Assert.Contains("源文件不可用", item.Note);
        Assert.Equal(0, h.Cli.InstallCalls);
        Assert.True(sw.ElapsedMilliseconds >= 150,
            $"应先重试一次再判失败（实际 {sw.ElapsedMilliseconds} ms，说明没有等待窗口）");
    }

    // ── 空间口径（docs/07 §3.4）：需要 = 压缩包 + 预计解压体积，不预留固定余量 ──
    [Fact]
    public async Task 空间检查_压缩加解压不足_挂起队列且不调用CLI()
    {
        var h = new Harness();
        const long appSize = 5000;
        const long compressed = 1024;                       // NewIpaFile 的固定大小
        h.Cli.QueryDiskUsageFactory = _ => new DiskUsage(true, 10_000_000, compressed + appSize - 1);
        var path = NewIpaFile();
        var item = h.EnqueueInstall(path, appSize: appSize);

        h.Service.StartPump(Harness.Udid);
        await WaitUntilAsync(() => item.Status == QueueStatus.Suspended,
            () => $"空间不足应挂起队列：{h.Describe(item)}");

        Assert.Contains("空间不足", item.Note);
        Assert.Contains("请释放空间后重试", item.Note);
        Assert.Contains("解压", item.Note);                  // 提示里要说明算了解压体积
        Assert.Equal(0, h.Cli.InstallCalls);                 // 没白跑一次进程
    }

    [Fact]
    public async Task 空间检查_压缩加解压够_继续安装并完成()
    {
        var h = new Harness();
        const long appSize = 5000;
        h.Cli.QueryDiskUsageFactory = _ => new DiskUsage(true, 10_000_000, 1024 + appSize + 10 * 1024 * 1024 + 100);   // 峰值 + 10 MB 余量
        h.Cli.InstallResultFactory = (_, _) =>
        {
            h.Readback.AddApp(BundleId, "1.0");
            return new InstallOutcome(0, false, null, false);
        };
        var path = NewIpaFile();
        var item = h.EnqueueInstall(path, appSize: appSize);

        h.Service.StartPump(Harness.Udid);
        await WaitUntilAsync(() => item.Status is QueueStatus.Done or QueueStatus.Failed,
            () => $"应装完：{h.Describe(item)}");

        Assert.Equal(QueueStatus.Done, item.Status);
        Assert.Equal(1, h.Cli.InstallCalls);
        Assert.Contains(h.Log.Snapshot(), line => line.Text.Contains("空间检查") && line.Text.Contains("解压"));
    }

    [Fact]
    public async Task 空间检查_解压体积未知_按压缩乘二估算并挂起()
    {
        var h = new Harness();
        // 可用只够 1× 压缩 + 余量：按 2× 估算应判空间不足并挂起（2026-11-07 用户定）。
        h.Cli.QueryDiskUsageFactory = _ => new DiskUsage(true, 10_000_000, 1024 + 10 * 1024 * 1024 + 500);
        h.Cli.InstallResultFactory = (_, _) =>
        {
            h.Readback.AddApp(BundleId, "1.0");
            return new InstallOutcome(0, false, null, false);
        };
        var path = NewIpaFile();
        var item = h.EnqueueInstall(path);                    // appSize 省略 = 未知

        h.Service.StartPump(Harness.Udid);
        await WaitUntilAsync(() => item.Status == QueueStatus.Suspended,
            () => $"应按压缩 ×2 判空间不足并挂起：{h.Describe(item)}");

        Assert.Contains("× 2", item.Note);
        Assert.Equal(0, h.Cli.InstallCalls);
        Assert.Contains(h.Log.Snapshot(), line => line.Text.Contains("解压体积未知"));
    }

    [Fact]
    public async Task 版本要求高于设备_任务开始前直接判失败_且不调用安装()
    {
        var h = new Harness();                       // 假设备版本 8.0（FakeCliRunner.DefaultProbe）
        var path = NewIpaFile();

        // IPA 要求 iOS 9.0 > 设备 8.0：应在上传前失败
        var result = h.Service.Enqueue(Harness.Udid, JobKind.Install, BundleId, path,
            "pkg-" + BundleId, packageVersion: "1.0", expectedUncompressedBytes: null, packageMinOs: "9.0");
        var item = result.Item;

        h.Service.StartPump(Harness.Udid);
        await WaitUntilAsync(() => item.Status is QueueStatus.Failed, () => h.Describe(item));

        Assert.Equal(QueueStatus.Failed, item.Status);
        Assert.Equal(0, h.Cli.InstallCalls);          // 关键：没有进入上传
        Assert.Contains("需要 iOS 9.0", item.Note ?? "", StringComparison.Ordinal);
        Assert.Contains("未上传", item.Note ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task 版本要求不高于设备_照常安装完成()
    {
        var h = new Harness();
        h.Cli.InstallResultFactory = (_, _) =>
        {
            h.Readback.AddApp(BundleId, "1.0");
            return new InstallOutcome(0, false, null, false);
        };
        var path = NewIpaFile();

        var result = h.Service.Enqueue(Harness.Udid, JobKind.Install, BundleId, path,
            "pkg-" + BundleId, packageVersion: "1.0", expectedUncompressedBytes: null, packageMinOs: "7.0");
        var item = result.Item;

        h.Service.StartPump(Harness.Udid);
        await WaitUntilAsync(() => item.Status is QueueStatus.Done or QueueStatus.Failed, () => h.Describe(item));

        Assert.Equal(QueueStatus.Done, item.Status);
        Assert.Equal(1, h.Cli.InstallCalls);
    }

    // ── 复审 B1：MinOs 闸必须在"恢复回读"之后（已装好的中断条目不能被版本判失败）──
    [Fact]
    public async Task 恢复条目_回读确认已装_比MinOs更优先_记完成()
    {
        var h = new Harness();
        h.Readback.AddApp(BundleId, "1.0");            // 设备上已有同版本（上次其实装好了）
        var path = NewIpaFile();

        var result = h.Service.Enqueue(Harness.Udid, JobKind.Install, BundleId, path,
            "pkg-" + BundleId, packageVersion: "1.0", expectedUncompressedBytes: null, packageMinOs: "9.0");
        var item = result.Item;
        item.WasInterrupted = true;
        item.Status = QueueStatus.Pending;

        h.Service.StartPump(Harness.Udid);
        await WaitUntilAsync(() => item.Status is QueueStatus.Done or QueueStatus.Failed, () => h.Describe(item));

        Assert.Equal(QueueStatus.Done, item.Status);   // 复审 B1：先回读确认已装 → 完成，不走 MinOs 失败
        Assert.Equal(0, h.Cli.InstallCalls);
    }

    // ── 复审 B2：同路径重入队必须刷新 PackageVersion / PackageMinOs ──
    [Fact]
    public void 同路径重入队_刷新版本与MinOs()
    {
        var h = new Harness();
        var path = NewIpaFile();

        var first = h.Service.Enqueue(Harness.Udid, JobKind.Install, BundleId, path,
            "pkg-" + BundleId, packageVersion: "1.0", expectedUncompressedBytes: null, packageMinOs: "7.0");
        Assert.Equal(EnqueueDisposition.Added, first.Disposition);

        var again = h.Service.Enqueue(Harness.Udid, JobKind.Install, BundleId, path,
            "pkg-" + BundleId, packageVersion: "2.0", expectedUncompressedBytes: null, packageMinOs: "9.0");

        Assert.Equal(EnqueueDisposition.Reset, again.Disposition);
        Assert.Equal("2.0", again.Item.PackageVersion);
        Assert.Equal("9.0", again.Item.PackageMinOs);
    }

    [Fact]
    public async Task 待执行_卸载优先_安装按临时占用降序()
    {
        await using var h = new Harness();
        var big = NewIpaFile();
        var small = NewIpaFile();

        h.EnqueueInstall(big, bundleId: "com.test.install.big", appSize: 10_000_000);
        h.EnqueueInstall(small, bundleId: "com.test.install.small", appSize: 1_000);
        h.Service.Enqueue(Harness.Udid, JobKind.Uninstall, "com.test.uninstall", null, null);

        var order = h.Service.Snapshot(Harness.Udid).Select(x => x.BundleId).ToArray();
        Assert.Equal(new[]
        {
            "com.test.uninstall",
            "com.test.install.big",
            "com.test.install.small",
        }, order);
    }

    // ── 队列挂起（2026-11-07）：等待任务灰、当前任务跑完停、挂起中入队继续灰、继续还原 ──
    [Fact]
    public async Task 挂起后队列清空_解除挂起_新任务回Pending()
    {
        await using var h = new Harness();
        var first = h.EnqueueInstall(NewIpaFile(), bundleId: "com.test.suspend.clear.a");
        var second = h.EnqueueInstall(NewIpaFile(), bundleId: "com.test.suspend.clear.b");

        h.Service.Suspend(Harness.Udid);
        Assert.True(h.Service.CancelPending(Harness.Udid, first.Id));
        Assert.True(h.Service.CancelPending(Harness.Udid, second.Id));
        Assert.Equal(2, h.Service.RemoveFinished(Harness.Udid));

        var third = h.EnqueueInstall(NewIpaFile(), bundleId: "com.test.suspend.clear.c");
        Assert.Equal(QueueStatus.Pending, third.Status);
    }

    [Fact]
    public async Task 挂起_等待任务变Suspended_新入队也挂起_继续后回Pending()
    {
        await using var h = new Harness();
        var first = h.EnqueueInstall(NewIpaFile(), bundleId: "com.test.suspend.a");
        var second = h.EnqueueInstall(NewIpaFile(), bundleId: "com.test.suspend.b");

        h.Service.Suspend(Harness.Udid);
        Assert.Equal(QueueStatus.Suspended, h.Service.Snapshot(Harness.Udid).Single(x => x.Id == first.Id).Status);
        Assert.Equal(QueueStatus.Suspended, h.Service.Snapshot(Harness.Udid).Single(x => x.Id == second.Id).Status);

        var third = h.EnqueueInstall(NewIpaFile(), bundleId: "com.test.suspend.c");
        Assert.Equal(QueueStatus.Suspended, third.Status);
        Assert.Equal(0, h.Cli.InstallCalls);

        h.Service.Resume(Harness.Udid);
        // Resume 会立刻起泵，任务可能已进入执行；关键是不能还停留在 Suspended。
        Assert.DoesNotContain(h.Service.Snapshot(Harness.Udid), x => x.Status == QueueStatus.Suspended);
    }

    [Fact]
    public async Task 挂起_当前任务继续跑完_不再取下一个()
    {
        await using var h = new Harness();
        h.Cli.BlockInstall = true;
        h.Cli.ResetInstallGate();
        h.Cli.InstallResultFactory = (_, _) =>
        {
            h.Readback.AddApp(BundleId, "1.0");
            return new InstallOutcome(0, false, null, false);
        };

        var first = h.EnqueueInstall(NewIpaFile(), bundleId: BundleId);
        var second = h.EnqueueInstall(NewIpaFile(), bundleId: "com.test.suspend.next");

        h.Service.StartPump(Harness.Udid);
        await h.Cli.InstallStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        h.Service.Suspend(Harness.Udid);
        Assert.Equal(QueueStatus.Suspended, h.Service.Snapshot(Harness.Udid).Single(x => x.Id == second.Id).Status);

        h.Cli.ReleaseInstall();
        await WaitUntilAsync(() => first.Status == QueueStatus.Done,
            () => "当前任务应继续跑完：" + h.Describe(first));

        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(QueueStatus.Suspended, h.Service.Snapshot(Harness.Udid).Single(x => x.Id == second.Id).Status);
        Assert.Equal(1, h.Cli.InstallCalls);
    }

    // ── 2026-11-07：失败自动再入队次数 / 断开挂起标记 ─────────────────────────
    [Fact]
    public async Task 失败自动再入队_按设置次数_达到次数后终判()
    {
        await using var h = new Harness();
        h.Service.AutoRetryAttempts = 3;
        h.Cli.InstallResultFactory = (_, _) => new InstallOutcome(1, true, "ERROR: 坏包", false);
        h.EnqueueInstall(NewIpaFile());

        h.Service.StartPump(Harness.Udid);
        await WaitUntilAsync(
            () => h.Service.Snapshot(Harness.Udid).Any(x => x.AutoRetryCount == 3 && x.Status == QueueStatus.Failed),
            () => "第 3 次尝试应终判失败：" + h.Describe());

        Assert.Equal(3, h.Cli.InstallCalls);
        Assert.Single(h.Service.Snapshot(Harness.Udid));   // 只留最后一次尝试
    }

    [Fact]
    public async Task 断开时等待任务转挂起_联机恢复后清标记()
    {
        await using var h = new Harness();
        h.Cli.ProbeFactory = _ => DeviceProbe.Missing;      // 离线
        var first = h.EnqueueInstall(NewIpaFile(), bundleId: "com.test.offline.a");
        var second = h.EnqueueInstall(NewIpaFile(), bundleId: "com.test.offline.b");

        h.Service.StartPump(Harness.Udid);
        await WaitUntilAsync(
            () => second.Status == QueueStatus.Suspended && second.SuspendedByDisconnect,
            () => $"等待任务应因断开挂起：{h.Describe(second)}");
        Assert.Equal(QueueStatus.Paused, first.Status);

        h.Cli.ProbeFactory = null;
        h.Service.ResumeAfterReconnect(Harness.Udid);
        Assert.DoesNotContain(h.Service.Snapshot(Harness.Udid), x => x.SuspendedByDisconnect);
    }

    // ── 2026-11-07 审查 P1：已装但版本不符，不得自动重装 ─────────────────────
    [Fact]
    public async Task 版本不一致_已确认App存在_不自动再入队()
    {
        await using var h = new Harness();
        h.Readback.AddApp(BundleId, "1.2");
        h.Cli.InstallResultFactory = (_, _) => new InstallOutcome(0, false, null, false);
        var item = h.EnqueueInstall(NewIpaFile(), version: "1.2.0");

        h.Service.StartPump(Harness.Udid);
        await WaitUntilAsync(() => item.Status == QueueStatus.Failed,
            () => $"版本不一致应终判失败：{h.Describe(item)}");

        Assert.Contains("设备已装版本", item.Note);
        Assert.Equal(1, h.Cli.InstallCalls);          // 不再自动重装
        Assert.Single(h.Service.Snapshot(Harness.Udid));
    }

    [Fact]
    public async Task 版本不符后回读异常_仍不自动再入队()
    {
        await using var h = new Harness();
        var calls = 0;
        h.Readback.ListFactory = (_, _) =>
        {
            calls++;
            if (calls == 1)
                return Task.FromResult<IReadOnlyList<InstalledApp>>(
                    new[] { new InstalledApp(BundleId, "1.2", VersionKnown: true) });
            throw new IOException("回读超时（测试）");
        };
        h.Cli.InstallResultFactory = (_, _) => new InstallOutcome(0, false, null, false);
        var item = h.EnqueueInstall(NewIpaFile(), version: "1.2.0", hadInstalledBefore: true);

        h.Service.StartPump(Harness.Udid);
        await WaitUntilAsync(() => item.Status == QueueStatus.Failed,
            () => $"应终判失败：{h.Describe(item)}");

        Assert.Equal(1, h.Cli.InstallCalls);   // 首轮 mismatch 已抑制，后续异常也不能再入队
        Assert.Single(h.Service.Snapshot(Harness.Udid));
    }

    [Fact]
    public async Task 联机恢复返回实际恢复条数()
    {
        await using var h = new Harness();
        h.Cli.ProbeFactory = _ => DeviceProbe.Missing;
        h.EnqueueInstall(NewIpaFile(), bundleId: "com.test.restore.a");
        h.EnqueueInstall(NewIpaFile(), bundleId: "com.test.restore.b");
        h.Service.StartPump(Harness.Udid);

        await WaitUntilAsync(
            () => h.Service.Snapshot(Harness.Udid).Count(x => x.Status == QueueStatus.Suspended) == 1,
            () => "应有一个等待任务因断开挂起：" + h.Describe());

        h.Cli.ProbeFactory = null;
        var restored = h.Service.ResumeAfterReconnect(Harness.Udid);
        Assert.Equal(2, restored);   // 当前 Paused + 一个断开挂起
    }

    // ── P2-3：自动卸载期间断线，不能被 Idle 覆盖 Paused ────────────────────────
    [Fact]
    public async Task 自动卸载期间设备离线_保留Paused运行态_不被Idle覆盖()
    {
        await using var h = new Harness();
        var ipa = NewIpaFile();
        h.Cli.InstallResultFactory = (_, _) =>
        {
            h.Readback.AddApp(BundleId, "1.0");
            return new InstallOutcome(0, false, null, false);
        };
        h.Cli.BlockInstall = true;
        h.Cli.ResetInstallGate();

        var item = h.EnqueueInstall(ipa);
        h.Service.StartPump(Harness.Udid);
        await h.Cli.InstallStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // 模拟进入安装段后取消 → 排「装完自动卸载」，并在安装结束后把设备切成离线
        item.Uploading = false;
        Assert.Equal(QueueService.CancelOutcome.ScheduledAfterInstall, h.Service.Cancel(Harness.Udid, item.Id));
        h.Cli.ProbeFactory = _ => DeviceProbe.Missing;
        h.Cli.ReleaseInstall();

        await WaitUntilAsync(
            () => item.Status == QueueStatus.Cancelled && !h.Service.IsRunning(Harness.Udid),
            () => "装完自动卸载离线应收敛并停泵：" + h.Describe(item));

        Assert.Contains("自动卸载未完成", item.Note);
        var runStates = h.Events.Snapshot().OfType<DeviceRunStateEvent>().Select(e => e.State).ToList();
        Assert.NotEmpty(runStates);
        Assert.Equal(QueueRunState.Paused, runStates[^1]);
    }

    // ── P2-4：Enqueue 重置清除 AutoRetrySuppressed/SuspendedByDisconnect，且 Suspend 清除 _resumeRequests ──
    [Fact]
    public void 重新入队_清除AutoRetrySuppressed与SuspendedByDisconnect()
    {
        var h = new Harness();
        var path = NewIpaFile();
        var item = h.EnqueueInstall(path);
        item.Status = QueueStatus.Failed;
        item.AutoRetrySuppressed = true;
        item.SuspendedByDisconnect = true;

        var again = h.EnqueueInstallResult(path);

        Assert.Equal(EnqueueDisposition.Reset, again.Disposition);
        Assert.False(again.Item.AutoRetrySuppressed);
        Assert.False(again.Item.SuspendedByDisconnect);
    }

    [Fact]
    public async Task 暂停后立刻Resume再立刻Suspend_不反解挂起也不覆盖Idle()
    {
        await using var h = new Harness();
        var ipa = NewIpaFile();
        h.Cli.CancelUnwindDelay = TimeSpan.FromMilliseconds(40);
        h.Cli.BlockInstall = true;
        h.Cli.ResetInstallGate();

        var item = h.EnqueueInstall(ipa);
        h.Service.StartPump(Harness.Udid);
        await WaitUntilAsync(() => item.Status == QueueStatus.Installing, () => h.Describe(item));

        // 触发旧泵取消展开，挂上 Resume 请求，紧接着在旧泵未收尾时用户点 Suspend
        h.Service.Pause(Harness.Udid);
        h.Service.Resume(Harness.Udid);
        h.Service.Suspend(Harness.Udid);

        await WaitUntilAsync(() => !h.Service.IsRunning(Harness.Udid), () => "旧泵应收尾退出：" + h.Describe(item));
        await Task.Delay(60, TestContext.Current.CancellationToken);

        // 队列仍处于用户挂起态：新入队应直接 Suspended，且不会启动安装
        var next = h.EnqueueInstall(NewIpaFile(), bundleId: "com.fake.after.suspend");
        Assert.Equal(QueueStatus.Suspended, next.Status);
        Assert.Equal(1, h.Cli.InstallCalls);
    }

    // ── P1-3：RemoveFinished 与 Enqueue 并发不致孤立 Suspended ────────────────
    [Fact]
    public void RemoveFinished清空后_立即Enqueue_不会遗留Suspended()
    {
        var h = new Harness();
        var a = h.EnqueueInstall(NewIpaFile(), bundleId: "com.test.p13.a");
        h.Service.Suspend(Harness.Udid);
        Assert.True(h.Service.CancelPending(Harness.Udid, a.Id));

        Assert.Equal(1, h.Service.RemoveFinished(Harness.Udid));
        var b = h.EnqueueInstall(NewIpaFile(), bundleId: "com.test.p13.b");
        Assert.Equal(QueueStatus.Pending, b.Status);
    }
}
