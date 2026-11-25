using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using IdeviceIndex.Resources;

namespace IdeviceIndex;

internal sealed record DownloadProgressSnapshot(
    string EntryId,
    DownloadJobStatus Status,
    double Percent,
    string SpeedText,
    string? Note,
    string? TargetPath = null,
    string? SourceSummary = null,
    bool OpenFolderIfConfigured = false,
    long Generation = 0);

internal sealed record DownloadCompletedInfo(
    string EntryId,
    string BundleId,
    string Title,
    string Version,
    string FilePath,
    string? DownloadDir);

/// <summary>
/// aria2 RPC 下载调度引擎：并发与同 host 上限、镜像故障转移、死链记忆、退避重试、卡死检测、后验与原子落盘。
/// 后台全程不碰 UI 控件、不调用 Dispatcher.Invoke，统一通过 <see cref="IProgress{T}"/> 节流投递快照。
/// </summary>
internal sealed partial class DownloadEngine : IAsyncDisposable
{
    public const int MaxPerHostConcurrency = 2;
    private static readonly JsonSerializerOptions JsonWriteOptions = new() { WriteIndented = true };

    [GeneratedRegex(@"status\s*=\s*(\d{3})", RegexOptions.IgnoreCase)]
    private static partial Regex HttpStatusRegex();

    private readonly Func<string, IReadOnlyList<SourceRow>> _loadSources;
    private readonly IProgress<DownloadProgressSnapshot>? _progress;
    private readonly string _programDir;
    private readonly string _settingsPath;
    private readonly string _deadLinksPath;
    private readonly Aria2VerifyResult? _preVerifiedAria2;

    private readonly object _stateLock = new();
    private readonly object _deadLinksWriteLock = new();
    private readonly object _inFlightLock = new();
    private readonly List<PendingJob> _pending = [];
    private readonly Dictionary<string, PendingJob> _pendingReenqueue = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActiveJob> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _activeHostCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reservedTargetPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deadUrls = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Task> _inFlightTasks = [];
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _daemonLock = new(1, 1);
    private readonly SemaphoreSlim _suspendLock = new(1, 1);
    private readonly SemaphoreSlim _pumpWake = new(0);
    private readonly CancellationTokenSource _engineCts = new();
    private readonly Task _pumpTask;

    private Aria2VerifyResult? _verified;
    private Aria2Daemon? _daemon;
    private DownloadSettings? _lastDaemonSettings;
    private volatile string? _sessionDownloadDirOverride;
    private Task? _shutdownTask;
    private bool _isSuspended;
    private bool _shuttingDown;
    private bool _disposed;

    public Func<DownloadCompletedInfo, CancellationToken, Task<string?>>? OnDownloadCompletedAsync { get; set; }

    /// <summary>测试覆写卡死判定时长，避免单元测试等待 30 秒。</summary>
    internal TimeSpan? StallTimeoutOverride { get; set; }

    /// <summary>测试覆写重试退避基础间隔。</summary>
    internal TimeSpan? BackoffBaseOverride { get; set; }

    public DownloadEngine(
        Func<string, IReadOnlyList<SourceRow>> loadSources,
        IProgress<DownloadProgressSnapshot>? progress = null,
        string? programDir = null,
        string? settingsPath = null,
        Aria2VerifyResult? preVerifiedAria2 = null)
    {
        _loadSources = loadSources ?? throw new ArgumentNullException(nameof(loadSources));
        _progress = progress;
        _programDir = Path.GetFullPath(string.IsNullOrWhiteSpace(programDir) ? AppContext.BaseDirectory : programDir);
        _settingsPath = string.IsNullOrWhiteSpace(settingsPath)
            ? Path.Combine(_programDir, "data", "indexer.json")
            : Path.GetFullPath(settingsPath);
        _deadLinksPath = Path.Combine(_programDir, "data", "dead_links.json");
        _preVerifiedAria2 = preVerifiedAria2;

        LoadDeadLinks();
        _pumpTask = Task.Run(PumpLoopAsync);
    }

    public bool HasActiveDownloads
    {
        get
        {
            lock (_stateLock)
                return _active.Count > 0 || _pending.Count > 0;
        }
    }

    public string DeadLinksFilePath => _deadLinksPath;

    public void SetSessionDownloadDir(string? directory)
    {
        _sessionDownloadDirOverride = string.IsNullOrWhiteSpace(directory)
            ? null
            : DownloadSettings.NormalizeDir(directory, DownloadSettings.DefaultDir(_programDir));
    }

    public bool IsDeadUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        lock (_stateLock)
            return _deadUrls.Contains(url.Trim());
    }

    public Aria2VerifyResult VerifyToolchain()
    {
        if (_preVerifiedAria2 is not null)
        {
            _verified = _preVerifiedAria2;
            return _preVerifiedAria2;
        }
        _verified = Aria2Verifier.Verify(_programDir);
        return _verified;
    }

    public void EnqueueJob(DownloadJobRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_shuttingDown || _disposed) return;

        lock (_stateLock)
        {
            if (_shuttingDown || _disposed) return;

            var pending = new PendingJob(
                row.Id,
                row.BundleId,
                row.Title,
                row.Version,
                row.TargetPath,
                row.Generation);

            if (_active.TryGetValue(row.Id, out var active))
            {
                // If user cancelled and immediately re-enqueued before RunActiveJobAsync finished cleanup,
                // record a pending re-enqueue instead of dropping it (P1-14).
                if (active.WasCancelledByUser)
                    _pendingReenqueue[row.Id] = pending;
                return;
            }

            if (_pending.Any(p => string.Equals(p.EntryId, row.Id, StringComparison.Ordinal)))
                return;

            _pending.Add(pending);
        }

        SignalPump();
    }

    public async Task CancelJobAsync(string entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId)) return;

        PendingJob? pendingRemoved = null;
        PendingJob? reenqueueRemoved = null;
        ActiveJob? active = null;
        lock (_stateLock)
        {
            var idx = _pending.FindIndex(p => string.Equals(p.EntryId, entryId, StringComparison.Ordinal));
            if (idx >= 0)
            {
                pendingRemoved = _pending[idx];
                _pending.RemoveAt(idx);
            }
            _pendingReenqueue.Remove(entryId, out reenqueueRemoved);
            if (_active.TryGetValue(entryId, out active))
                active.MarkCancelled();
        }

        if (pendingRemoved is not null && active is null)
        {
            ReportProgress(new DownloadProgressSnapshot(
                entryId, DownloadJobStatus.Cancelled, 0, "", null, Generation: pendingRemoved.Generation));
            SignalPump();
            return;
        }

        if (active is null)
        {
            if (reenqueueRemoved is not null)
            {
                ReportProgress(new DownloadProgressSnapshot(
                    entryId, DownloadJobStatus.Cancelled, 0, "", null, Generation: reenqueueRemoved.Generation));
            }
            return;
        }

        var gid = active.CurrentGid;
        if (!string.IsNullOrWhiteSpace(gid) && _daemon is { HasExited: false } daemon)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await daemon.Rpc.ForceRemoveAsync(gid, cts.Token).ConfigureAwait(false);
                await daemon.Rpc.RemoveDownloadResultAsync(gid, cts.Token).ConfigureAwait(false);
            }
            catch
            {
                // 进程已退出或 GID 已结束时忽略
            }
        }

        try
        {
            active.CancelSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 任务已收尾释放
        }

        try
        {
            await active.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
            // 等待取消清理完成
        }
    }

    public async Task SetSuspendedAsync(bool suspended)
    {
        if (_shuttingDown || _disposed) return;

        lock (_stateLock)
        {
            _isSuspended = suspended;
        }

        try
        {
            await _suspendLock.WaitAsync(_engineCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        bool effectiveSuspended;
        try
        {
            List<(ActiveJob Job, string? Gid)> activeSnapshot;
            lock (_stateLock)
            {
                effectiveSuspended = _isSuspended;
                activeSnapshot = _active.Values.Select(a => (a, a.CurrentGid)).ToList();
            }

            if (_daemon is { HasExited: false } daemon)
            {
                foreach (var (job, gid) in activeSnapshot)
                {
                    if (string.IsNullOrWhiteSpace(gid)) continue;
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        if (effectiveSuspended)
                        {
                            await daemon.Rpc.PauseAsync(gid, cts.Token).ConfigureAwait(false);
                            job.IsPaused = true;
                            ReportProgress(new DownloadProgressSnapshot(
                                job.EntryId, DownloadJobStatus.Paused, job.LastPercent, "", Strings.Queue_Note_Suspended,
                                job.TargetPath, job.SourceSummary, Generation: job.Generation));
                        }
                        else
                        {
                            await daemon.Rpc.UnpauseAsync(gid, cts.Token).ConfigureAwait(false);
                            job.IsPaused = false;
                            ReportProgress(new DownloadProgressSnapshot(
                                job.EntryId, DownloadJobStatus.Downloading, job.LastPercent, "", null,
                                job.TargetPath, job.SourceSummary, Generation: job.Generation));
                        }
                    }
                    catch
                    {
                        // 单条 GID 状态已变化时忽略
                    }
                }
            }
        }
        finally
        {
            try { _suspendLock.Release(); } catch (ObjectDisposedException) { }
        }

        if (!effectiveSuspended) SignalPump();
    }

    public Task ShutdownAsync(bool keepPartFiles)
    {
        Task? existing;
        lock (_stateLock)
        {
            if (_shutdownTask is not null)
            {
                existing = _shutdownTask;
            }
            else
            {
                _shuttingDown = true;
                _shutdownTask = ShutdownCoreAsync(keepPartFiles);
                existing = _shutdownTask;
            }
        }
        return existing;
    }

    private async Task ShutdownCoreAsync(bool keepPartFiles)
    {
        List<ActiveJob> activeJobs;
        List<string> partPathsToDelete = [];

        lock (_stateLock)
        {
            _pending.Clear();
            _pendingReenqueue.Clear();
            activeJobs = _active.Values.ToList();
            foreach (var job in activeJobs)
            {
                job.IsShuttingDown = true;
                job.KeepPartsOnShutdown = keepPartFiles;
                if (!keepPartFiles && !string.IsNullOrWhiteSpace(job.PartPath))
                    partPathsToDelete.Add(job.PartPath);
                try
                {
                    job.CancelSource.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Ignore already disposed CTS
                }
            }
        }

        SignalPump();
        try { _engineCts.Cancel(); } catch (ObjectDisposedException) { }

        if (_daemon is { HasExited: false } daemon)
        {
            foreach (var job in activeJobs)
            {
                var gid = job.CurrentGid;
                if (string.IsNullOrWhiteSpace(gid)) continue;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
                    await daemon.Rpc.ForceRemoveAsync(gid, cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    // 忽略关闭前取消通知异常
                }
            }
        }

        await _daemonLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_daemon is not null)
            {
                await _daemon.DisposeAsync().ConfigureAwait(false);
                _daemon = null;
                _lastDaemonSettings = null;
            }
        }
        finally
        {
            _daemonLock.Release();
        }

        try
        {
            await _pumpTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch
        {
            // 等待调度泵收尾
        }

        // Wait for all tracked in-flight RunActiveJobAsync / background tasks before deleting .part files or disposing primitives.
        Task[] inFlightSnapshot;
        lock (_inFlightLock)
            inFlightSnapshot = _inFlightTasks.ToArray();

        if (inFlightSnapshot.Length > 0)
        {
            try
            {
                await Task.WhenAll(inFlightSnapshot).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch
            {
                // Bounded timeout / cancellation during shutdown
            }
        }

        if (!keepPartFiles)
        {
            foreach (var part in partPathsToDelete)
            {
                try { DownloadPathPolicy.TryDeleteSingleFile(part); } catch { }
                try { DownloadPathPolicy.TryDeleteSingleFile(part + ".aria2"); } catch { }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var settings = IndexerSettings.LoadDownloadSettings(_settingsPath, _programDir);
        await ShutdownAsync(settings.KeepPartFiles).ConfigureAwait(false);
        _engineCts.Dispose();
        _pumpWake.Dispose();
        _daemonLock.Dispose();
        _suspendLock.Dispose();
        foreach (var gate in _hostGates.Values)
            gate.Dispose();
    }

    private async Task PumpLoopAsync()
    {
        try
        {
            while (!_engineCts.IsCancellationRequested)
            {
                PreparePendingJobs();
                DispatchReadyJobs();
                await _pumpWake.WaitAsync(_engineCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }
    }

    private void PreparePendingJobs()
    {
        List<PendingJob> unprepared;
        lock (_stateLock)
        {
            if (_shuttingDown || _pending.Count == 0) return;
            unprepared = _pending.Where(p => !p.SourcesPrepared).ToList();
        }

        if (unprepared.Count == 0) return;

        var anyDeadLinksChanged = false;
        foreach (var job in unprepared)
        {
            if (_engineCts.IsCancellationRequested || _shuttingDown) return;

            IReadOnlyList<SourceRow> sources;
            try
            {
                sources = _loadSources(job.EntryId);
            }
            catch
            {
                sources = [];
            }

            lock (_stateLock)
            {
                if (_shuttingDown) return;
                if (!_pending.Any(p => ReferenceEquals(p, job))) continue;

                // 若该条目所有有效镜像都已被记为死链，用户手动重新入队时清除这些 URL 以允许显式重试（D22）
                var validUrls = sources
                    .Select(s => s.Url?.Trim() ?? "")
                    .Where(u => u.Length > 0)
                    .ToList();
                if (validUrls.Count > 0 && validUrls.All(u => _deadUrls.Contains(u)))
                {
                    foreach (var url in validUrls)
                    {
                        if (_deadUrls.Remove(url)) anyDeadLinksChanged = true;
                    }
                }

                job.InitialHost = PickInitialHostLocked(sources);
                job.SourcesPrepared = true;
            }
        }

        if (anyDeadLinksChanged)
            SaveDeadLinksCore();
    }

    private void DispatchReadyJobs()
    {
        var settings = IndexerSettings.LoadDownloadSettings(_settingsPath, _programDir);
        var maxConcurrent = settings.Concurrency;

        while (true)
        {
            PendingJob? next = null;
            lock (_stateLock)
            {
                if (_shuttingDown || _isSuspended) break;
                if (_active.Count >= maxConcurrent || _pending.Count == 0) break;

                // 优先选择同 host 当前活动数 < 2 的任务，避免单 host 占满时饿死其他 host
                var pickedIndex = -1;
                for (var i = 0; i < _pending.Count; i++)
                {
                    if (!_pending[i].SourcesPrepared) continue;
                    var host = _pending[i].InitialHost;
                    if (string.IsNullOrEmpty(host) || GetActiveHostCountLocked(host) < MaxPerHostConcurrency)
                    {
                        pickedIndex = i;
                        break;
                    }
                }
                if (pickedIndex < 0) break;

                next = _pending[pickedIndex];
                _pending.RemoveAt(pickedIndex);

                var active = new ActiveJob(
                    next.EntryId,
                    next.BundleId,
                    next.Title,
                    next.Version,
                    next.ExistingTargetPath,
                    next.Generation,
                    _engineCts.Token);

                if (!string.IsNullOrEmpty(next.InitialHost))
                {
                    IncrementHostCountLocked(next.InitialHost);
                    active.TrackedHost = next.InitialHost;
                }

                _active[next.EntryId] = active;
                var task = Task.Run(() => RunActiveJobAsync(active));
                TrackTask(task);
            }

            if (next is null) break;
        }
    }

    private async Task RunActiveJobAsync(ActiveJob active)
    {
        var reportCancelled = false;
        try
        {
            await ExecuteJobCoreAsync(active).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (active.WasCancelledByUser)
            {
                CleanupPartOnCancelIfNeeded(active);
                reportCancelled = true;
            }
        }
        catch (Exception ex)
        {
            if (!active.WasCancelledByUser && !_shuttingDown)
            {
                ReportProgress(new DownloadProgressSnapshot(
                    active.EntryId,
                    DownloadJobStatus.Failed,
                    0,
                    "",
                    Strings.Format(nameof(Strings.Download_Error_FailedFormat), ex.Message),
                    active.TargetPath,
                    active.SourceSummary,
                    Generation: active.Generation));
            }
        }
        finally
        {
            var requeued = false;
            lock (_stateLock)
            {
                _active.Remove(active.EntryId);
                if (!string.IsNullOrWhiteSpace(active.TargetPath))
                    _reservedTargetPaths.Remove(active.TargetPath);
                if (!string.IsNullOrEmpty(active.TrackedHost))
                {
                    DecrementHostCountLocked(active.TrackedHost);
                    active.TrackedHost = null;
                }
                if (!_shuttingDown && _pendingReenqueue.Remove(active.EntryId, out var pending))
                {
                    _pending.Add(pending);
                    requeued = true;
                }
            }

            if (reportCancelled && !requeued && !_shuttingDown)
            {
                ReportProgress(new DownloadProgressSnapshot(
                    active.EntryId, DownloadJobStatus.Cancelled, 0, "", null,
                    active.TargetPath, active.SourceSummary, Generation: active.Generation));
            }

            active.Completion.TrySetResult();
            active.CancelSource.Dispose();
            SignalPump();
        }
    }

    private async Task ExecuteJobCoreAsync(ActiveJob active)
    {
        var ct = active.CancelSource.Token;
        ct.ThrowIfCancellationRequested();

        var verify = _verified ?? VerifyToolchain();
        if (!verify.Ok)
        {
            ReportProgress(new DownloadProgressSnapshot(
                active.EntryId, DownloadJobStatus.Failed, 0, "",
                verify.Message ?? Strings.Format(nameof(Strings.Download_Error_DaemonFailedFormat), "aria2 disabled"),
                Generation: active.Generation));
            return;
        }

        var settings = IndexerSettings.LoadDownloadSettings(_settingsPath, _programDir);
        var overrideDir = _sessionDownloadDirOverride;
        var targetDir = DownloadSettings.NormalizeDir(
            string.IsNullOrWhiteSpace(overrideDir) ? settings.Dir : overrideDir,
            DownloadSettings.DefaultDir(_programDir));
        settings = settings with { Dir = targetDir };
        var dirCheck = DownloadPathPolicy.EnsureWritableDirectory(targetDir, _programDir, _settingsPath);
        if (!dirCheck.Ok)
        {
            ReportProgress(new DownloadProgressSnapshot(
                active.EntryId, DownloadJobStatus.Failed, 0, "", dirCheck.ErrorMessage, Generation: active.Generation));
            return;
        }
        targetDir = dirCheck.NormalizedPath;

        var rawSources = _loadSources(active.EntryId);
        var validSources = rawSources
            .Where(s => Uri.TryCreate(s.Url?.Trim(), UriKind.Absolute, out var u) &&
                        (string.Equals(u.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(u.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (validSources.Count == 0)
        {
            ReportProgress(new DownloadProgressSnapshot(
                active.EntryId, DownloadJobStatus.Failed, 0, "", Strings.Download_Note_NoSources, Generation: active.Generation));
            return;
        }

        // 过滤已标记死链（若全部都已死链则直接按死链失败，等待用户手动重试清除）
        var liveSources = validSources.Where(s => !IsDeadUrl(s.Url)).ToList();
        if (liveSources.Count == 0)
        {
            ReportProgress(new DownloadProgressSnapshot(
                active.EntryId, DownloadJobStatus.Failed, 0, "", Strings.Download_Error_DeadLink, Generation: active.Generation));
            return;
        }

        var maxExpectedSize = liveSources.Max(s => s.Size);
        var preCheck = DownloadPathPolicy.CheckPreDownload(targetDir, maxExpectedSize, settings, _programDir, _settingsPath);
        if (!preCheck.Ok)
        {
            ReportProgress(new DownloadProgressSnapshot(
                active.EntryId, DownloadJobStatus.Failed, 0, "", preCheck.ErrorMessage, Generation: active.Generation));
            return;
        }

        string targetPath;
        lock (_stateLock)
        {
            var fullExisting = string.IsNullOrWhiteSpace(active.ExistingTargetPath)
                ? null
                : Path.GetFullPath(active.ExistingTargetPath);
            if (fullExisting is not null &&
                string.Equals(Path.GetDirectoryName(fullExisting), targetDir, StringComparison.OrdinalIgnoreCase) &&
                !_reservedTargetPaths.Contains(fullExisting))
            {
                targetPath = fullExisting;
            }
            else
            {
                targetPath = DownloadPathPolicy.AllocateUniqueTargetPath(
                    targetDir, active.Title, active.Version, active.BundleId, _reservedTargetPaths);
            }
            _reservedTargetPaths.Add(targetPath);
        }

        var partPath = targetPath + ".part";
        active.TargetPath = targetPath;
        active.PartPath = partPath;

        var daemon = await EnsureDaemonAsync(verify, settings, ct).ConfigureAwait(false);

        string? lastFailureMessage = null;
        var sawDeadLink = false;
        var sawUnavailable = false;
        long observedTotalBytes = 0;
        SourceRow? succeededSource = null;

        for (var mirrorIdx = 0; mirrorIdx < liveSources.Count; mirrorIdx++)
        {
            ct.ThrowIfCancellationRequested();
            var source = liveSources[mirrorIdx];
            var mirrorLabel = string.IsNullOrWhiteSpace(source.SourceId) ? $"#{mirrorIdx + 1}" : source.SourceId;
            active.SourceSummary = mirrorLabel;

            var host = ExtractHost(source.Url);
            var hostGate = _hostGates.GetOrAdd(host, _ => new SemaphoreSlim(MaxPerHostConcurrency, MaxPerHostConcurrency));
            var stallRetried = false;

            if (!string.Equals(active.TrackedHost, host, StringComparison.OrdinalIgnoreCase))
            {
                SwitchTrackedHost(active, null, signalPump: true);
                await hostGate.WaitAsync(ct).ConfigureAwait(false);
                SwitchTrackedHost(active, host, signalPump: false);
            }
            else
            {
                await hostGate.WaitAsync(ct).ConfigureAwait(false);
            }

            try
            {
                for (var attempt = 0; attempt <= settings.RetryTimes; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    while (IsPausedForSuspend())
                    {
                        ct.ThrowIfCancellationRequested();
                        await Task.Delay(150, ct).ConfigureAwait(false);
                    }

                    // 每次写入前重新检查写盘边界（D31）及预期空间
                    var attemptExpected = source.Size > 0 ? source.Size : 0;
                    var attemptPreCheck = DownloadPathPolicy.CheckPreDownload(
                        targetDir, attemptExpected, settings, _programDir, _settingsPath);
                    if (!attemptPreCheck.Ok)
                    {
                        ReportProgress(new DownloadProgressSnapshot(
                            active.EntryId, DownloadJobStatus.Failed, 0, "", attemptPreCheck.ErrorMessage,
                            targetPath, mirrorLabel, Generation: active.Generation));
                        return;
                    }

                    var note = attempt == 0
                        ? (liveSources.Count > 1
                            ? Strings.Format(nameof(Strings.Download_Note_MirrorAttemptFormat), mirrorIdx + 1, liveSources.Count)
                            : null)
                        : Strings.Format(nameof(Strings.Download_Note_RetryingFormat), attempt, settings.RetryTimes);

                    ReportProgress(new DownloadProgressSnapshot(
                        active.EntryId, DownloadJobStatus.Downloading, active.LastPercent, "", note,
                        targetPath, mirrorLabel, Generation: active.Generation));

                    var addOptions = BuildAria2AddOptions(targetDir, Path.GetFileName(partPath), source.Url, settings);
                    var gid = await daemon.Rpc.AddUriAsync(source.Url, addOptions, ct).ConfigureAwait(false);
                    active.CurrentGid = gid;

                    // Re-check suspend immediately after AddUriAsync returns a GID so rapid suspend toggles don't race past it (P2-21).
                    if (IsPausedForSuspend())
                    {
                        try
                        {
                            await daemon.Rpc.PauseAsync(gid, ct).ConfigureAwait(false);
                            active.IsPaused = true;
                            ReportProgress(new DownloadProgressSnapshot(
                                active.EntryId, DownloadJobStatus.Paused, active.LastPercent, "", Strings.Queue_Note_Suspended,
                                targetPath, mirrorLabel, Generation: active.Generation));
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch
                        {
                            // GID may have already completed or removed
                        }

                        while (IsPausedForSuspend())
                        {
                            ct.ThrowIfCancellationRequested();
                            await Task.Delay(150, ct).ConfigureAwait(false);
                        }

                        if (active.IsPaused)
                        {
                            try
                            {
                                await daemon.Rpc.UnpauseAsync(gid, ct).ConfigureAwait(false);
                                active.IsPaused = false;
                                ReportProgress(new DownloadProgressSnapshot(
                                    active.EntryId, DownloadJobStatus.Downloading, active.LastPercent, "", note,
                                    targetPath, mirrorLabel, Generation: active.Generation));
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch
                            {
                                // Ignore if SetSuspendedAsync(false) already unpaused or completed
                            }
                        }
                    }

                    var outcome = await MonitorDownloadGidAsync(daemon.Rpc, active, gid, targetDir, settings, note, ct).ConfigureAwait(false);
                    active.CurrentGid = null;
                    ct.ThrowIfCancellationRequested();
                    if (active.WasCancelledByUser) throw new OperationCanceledException(ct);

                    try
                    {
                        using var rmCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await daemon.Rpc.RemoveDownloadResultAsync(gid, rmCts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // 忽略结果清理错误
                    }

                    if (outcome.Kind == AttemptOutcomeKind.Completed)
                    {
                        observedTotalBytes = outcome.TotalLength;
                        succeededSource = source;
                        break;
                    }

                    if (outcome.Kind == AttemptOutcomeKind.SizeOrSpaceExceeded)
                    {
                        CleanupPartAndControl(partPath);
                        ReportProgress(new DownloadProgressSnapshot(
                            active.EntryId, DownloadJobStatus.Failed, 0, "", outcome.ErrorMessage,
                            targetPath, mirrorLabel, Generation: active.Generation));
                        return;
                    }

                    if (outcome.Kind == AttemptOutcomeKind.DiskFull)
                    {
                        CleanupPartAndControl(partPath);
                        var spaceReCheck = DownloadPathPolicy.CheckPreDownload(
                            targetDir, Math.Max(source.Size, outcome.TotalLength), settings, _programDir, _settingsPath);
                        var msg = !spaceReCheck.Ok && !string.IsNullOrWhiteSpace(spaceReCheck.ErrorMessage)
                            ? spaceReCheck.ErrorMessage
                            : Strings.Format(nameof(Strings.Download_Error_DiskFullFormat), outcome.ErrorMessage ?? "");
                        ReportProgress(new DownloadProgressSnapshot(
                            active.EntryId, DownloadJobStatus.Failed, 0, "", msg,
                            targetPath, mirrorLabel, Generation: active.Generation));
                        return;
                    }

                    if (outcome.Kind == AttemptOutcomeKind.DeadLink)
                    {
                        sawDeadLink = true;
                        MarkUrlDead(source.Url);
                        lastFailureMessage = Strings.Download_Error_DeadLink;
                        // 不重试 404/410，切换下一个镜像
                        break;
                    }

                    if (outcome.Kind == AttemptOutcomeKind.Unavailable)
                    {
                        sawUnavailable = true;
                        lastFailureMessage = Strings.Download_Error_Unavailable;
                        // 403 不重试，切换下一个镜像
                        break;
                    }

                    if (outcome.Kind == AttemptOutcomeKind.Stalled)
                    {
                        lastFailureMessage = outcome.ErrorMessage ?? Strings.Download_Note_StallRetrying;
                        if (!stallRetried)
                        {
                            stallRetried = true;
                            ReportProgress(new DownloadProgressSnapshot(
                                active.EntryId, DownloadJobStatus.Downloading, active.LastPercent, "",
                                Strings.Download_Note_StallRetrying, targetPath, mirrorLabel, Generation: active.Generation));
                            // Guarantee the 1 free stall-reconnect even when RetryTimes == 0 (P2-19)
                            attempt--;
                            continue;
                        }
                        break;
                    }

                    // 429 / 5xx / 网络错误 → 指数退避重试，上限 30s
                    lastFailureMessage = Strings.Format(nameof(Strings.Download_Error_FailedFormat), outcome.ErrorMessage ?? "unknown error");
                    if (attempt < settings.RetryTimes)
                    {
                        var delay = ComputeBackoffDelay(attempt, settings.RetryWait);
                        ReportProgress(new DownloadProgressSnapshot(
                            active.EntryId, DownloadJobStatus.Downloading, active.LastPercent, "",
                            Strings.Format(nameof(Strings.Download_Note_RetryingFormat), attempt + 1, settings.RetryTimes),
                            targetPath, mirrorLabel, Generation: active.Generation));
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                SwitchTrackedHost(active, null, signalPump: true);
                try { hostGate.Release(); } catch (ObjectDisposedException) { }
            }

            if (succeededSource is not null) break;
        }

        if (succeededSource is null)
        {
            var reason = lastFailureMessage
                ?? (sawDeadLink ? Strings.Download_Error_DeadLink
                    : sawUnavailable ? Strings.Download_Error_Unavailable
                    : Strings.Format(nameof(Strings.Download_Error_FailedFormat), "unknown error"));
            ReportProgress(new DownloadProgressSnapshot(
                active.EntryId, DownloadJobStatus.Failed, 0, "", reason,
                targetPath, active.SourceSummary, Generation: active.Generation));
            return;
        }

        // 后验阶段（D15）
        ReportProgress(new DownloadProgressSnapshot(
            active.EntryId, DownloadJobStatus.Verifying, 100, "", Strings.Download_Note_Verifying,
            targetPath, active.SourceSummary, Generation: active.Generation));

        var expectedBytes = ResolveExpectedPartBytes(succeededSource, observedTotalBytes);
        var verifyPart = DownloadPathPolicy.VerifyPartFile(partPath, expectedBytes, succeededSource.Sha256);
        if (!verifyPart.Ok)
        {
            CleanupPartAndControl(partPath);
            ReportProgress(new DownloadProgressSnapshot(
                active.EntryId, DownloadJobStatus.Failed, 0, "", verifyPart.ErrorMessage,
                targetPath, active.SourceSummary, Generation: active.Generation));
            return;
        }

        // 写盘落盘前再次验证写盘边界（D31）
        var moveBoundary = DownloadPathPolicy.ValidateWriteDirectory(targetDir, _programDir, _settingsPath);
        if (!moveBoundary.Ok)
        {
            ReportProgress(new DownloadProgressSnapshot(
                active.EntryId, DownloadJobStatus.Failed, 0, "", moveBoundary.ErrorMessage,
                targetPath, active.SourceSummary, Generation: active.Generation));
            return;
        }

        if (File.Exists(targetPath))
        {
            lock (_stateLock)
            {
                var updatedPath = DownloadPathPolicy.AllocateUniqueTargetPath(
                    targetDir, active.Title, active.Version, active.BundleId, _reservedTargetPaths);
                _reservedTargetPaths.Remove(targetPath);
                _reservedTargetPaths.Add(updatedPath);
                targetPath = updatedPath;
                active.TargetPath = updatedPath;
            }
        }

        File.Move(partPath, targetPath, overwrite: false);
        try { DownloadPathPolicy.TryDeleteSingleFile(partPath + ".aria2"); } catch { }
        active.TargetPath = targetPath;

        string? finalNote = Strings.Format(nameof(Strings.Download_Note_SavedToFormat), targetPath);
        if (OnDownloadCompletedAsync is not null)
        {
            ReportProgress(new DownloadProgressSnapshot(
                active.EntryId, DownloadJobStatus.Importing, 100, "", null,
                targetPath, active.SourceSummary, Generation: active.Generation));
            try
            {
                var info = new DownloadCompletedInfo(
                    active.EntryId, active.BundleId, active.Title, active.Version, targetPath, targetDir);
                var hookNote = await OnDownloadCompletedAsync(info, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(hookNote)) finalNote = hookNote;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                finalNote = ex.Message;
            }
        }

        ReportProgress(new DownloadProgressSnapshot(
            active.EntryId,
            DownloadJobStatus.Succeeded,
            100,
            "",
            finalNote,
            targetPath,
            active.SourceSummary,
            OpenFolderIfConfigured: settings.OpenFolderWhenDone,
            Generation: active.Generation));
    }

    internal async Task<AttemptOutcome> MonitorDownloadGidAsync(
        Aria2RpcClient rpc,
        ActiveJob active,
        string gid,
        string targetDir,
        DownloadSettings settings,
        string? baseNote,
        CancellationToken ct)
    {
        var stallLimitMs = (long)(StallTimeoutOverride?.TotalMilliseconds
            ?? Math.Max(1, settings.Timeout) * 1000.0);

        long lastCompletedBytes = -1;
        var lastProgressTick = Environment.TickCount64;
        var lastReportTick = 0L;
        var checkedDynamicSizeAndSpace = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            Aria2StatusSnapshot status;
            try
            {
                status = await rpc.TellStatusAsync(gid, ct).ConfigureAwait(false);
            }
            catch (Aria2RpcException rpcEx) when (rpcEx.Code == 1)
            {
                return new AttemptOutcome(AttemptOutcomeKind.TransientError, 0, rpcEx.Message);
            }

            // As soon as aria2 reports TotalLength > 0, check MaxFileSizeMB and remaining free disk space (P2-18).
            if (!checkedDynamicSizeAndSpace && status.TotalLength > 0)
            {
                checkedDynamicSizeAndSpace = true;
                var remainingBytes = Math.Max(0L, status.TotalLength - Math.Max(0L, status.CompletedLength));
                var dynamicCheck = DownloadPathPolicy.CheckSizeAndSpace(
                    targetDir, remainingBytes, status.TotalLength, settings);
                if (!dynamicCheck.Ok)
                {
                    try
                    {
                        await rpc.ForceRemoveAsync(gid, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Ignore removal errors if already completed
                    }
                    return new AttemptOutcome(
                        AttemptOutcomeKind.SizeOrSpaceExceeded,
                        status.TotalLength,
                        dynamicCheck.ErrorMessage);
                }
            }

            if (string.Equals(status.Status, "complete", StringComparison.OrdinalIgnoreCase))
            {
                return new AttemptOutcome(AttemptOutcomeKind.Completed, status.TotalLength, null);
            }

            if (string.Equals(status.Status, "error", StringComparison.OrdinalIgnoreCase))
            {
                return ClassifyAria2Error(status);
            }

            if (string.Equals(status.Status, "removed", StringComparison.OrdinalIgnoreCase))
            {
                if (active.WasCancelledByUser)
                    throw new OperationCanceledException(ct);
                return new AttemptOutcome(AttemptOutcomeKind.TransientError, status.TotalLength, "removed");
            }

            if (string.Equals(status.Status, "paused", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status.Status, "waiting", StringComparison.OrdinalIgnoreCase))
            {
                lastProgressTick = Environment.TickCount64;
                await Task.Delay(150, ct).ConfigureAwait(false);
                continue;
            }

            var now = Environment.TickCount64;
            if (status.CompletedLength > lastCompletedBytes)
            {
                lastCompletedBytes = status.CompletedLength;
                lastProgressTick = now;
            }
            else if (string.Equals(status.Status, "active", StringComparison.OrdinalIgnoreCase) &&
                     status.DownloadSpeed == 0 &&
                     now - lastProgressTick >= stallLimitMs)
            {
                try
                {
                    await rpc.ForceRemoveAsync(gid, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // 忽略强移异常
                }
                return new AttemptOutcome(AttemptOutcomeKind.Stalled, status.TotalLength, Strings.Download_Note_StallRetrying);
            }

            var percent = status.TotalLength > 0
                ? Math.Clamp(status.CompletedLength * 100.0 / status.TotalLength, 0, 99.9)
                : 0;
            active.LastPercent = percent;

            if (now - lastReportTick >= 150)
            {
                lastReportTick = now;
                var speedText = status.DownloadSpeed > 0 ? FormatSpeed(status.DownloadSpeed) : "";
                ReportProgress(new DownloadProgressSnapshot(
                    active.EntryId, DownloadJobStatus.Downloading, percent, speedText, baseNote,
                    active.TargetPath, active.SourceSummary, Generation: active.Generation));
            }

            await Task.Delay(150, ct).ConfigureAwait(false);
        }
    }

    internal static long ResolveExpectedPartBytes(SourceRow source, long observedTotalBytes)
        => source.SizeExact && source.Size > 0 ? source.Size : observedTotalBytes;

    internal static AttemptOutcome ClassifyAria2Error(Aria2StatusSnapshot status)
    {
        var code = status.ErrorCode?.Trim() ?? "";
        var msg = status.ErrorMessage ?? "";

        if (code == "9" ||
            msg.Contains("not enough disk space", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("No space left", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("disk full", StringComparison.OrdinalIgnoreCase))
        {
            return new AttemptOutcome(AttemptOutcomeKind.DiskFull, status.TotalLength, msg);
        }

        var match = HttpStatusRegex().Match(msg);
        if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var httpStatus))
        {
            if (httpStatus is 404 or 410)
                return new AttemptOutcome(AttemptOutcomeKind.DeadLink, status.TotalLength, msg);
            if (httpStatus == 403)
                return new AttemptOutcome(AttemptOutcomeKind.Unavailable, status.TotalLength, msg);
        }

        if (code == "3")
            return new AttemptOutcome(AttemptOutcomeKind.DeadLink, status.TotalLength, msg);

        if (msg.Contains("SSL/TLS handshake failure", StringComparison.OrdinalIgnoreCase))
            return new AttemptOutcome(AttemptOutcomeKind.TransientError, status.TotalLength, $"{msg} ({Strings.Download_Hint_ProxyAria2Direct})");

        return new AttemptOutcome(AttemptOutcomeKind.TransientError, status.TotalLength, string.IsNullOrWhiteSpace(msg) ? $"aria2 error {code}" : msg);
    }

    private TimeSpan ComputeBackoffDelay(int attemptIndex, int retryWaitSeconds)
    {
        var baseMs = BackoffBaseOverride?.TotalMilliseconds ?? retryWaitSeconds * 1000.0;
        var scaled = baseMs * (1 << Math.Min(attemptIndex, 4));
        return TimeSpan.FromMilliseconds(Math.Min(30_000.0, Math.Max(10.0, scaled)));
    }

    private async Task<Aria2Daemon> EnsureDaemonAsync(Aria2VerifyResult verified, DownloadSettings settings, CancellationToken ct)
    {
        await _daemonLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_daemon is { HasExited: false })
            {
                if (_lastDaemonSettings is not null && !AreGlobalDaemonOptionsEqual(_lastDaemonSettings, settings))
                {
                    try
                    {
                        await _daemon.Rpc.ChangeGlobalOptionAsync(BuildGlobalAria2Options(settings), ct).ConfigureAwait(false);
                        _lastDaemonSettings = settings;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // Best-effort live global option update; per-task addUri options still apply
                    }
                }
                return _daemon;
            }

            if (_daemon is not null)
            {
                await _daemon.DisposeAsync().ConfigureAwait(false);
                _daemon = null;
                _lastDaemonSettings = null;
            }

            _daemon = await Aria2Daemon.StartAsync(verified, settings, _programDir, _settingsPath, ct).ConfigureAwait(false);
            _lastDaemonSettings = settings;
            return _daemon;
        }
        finally
        {
            _daemonLock.Release();
        }
    }

    internal async Task<bool> ApplySettingsToRunningDaemonAsync(DownloadSettings settings, CancellationToken ct = default)
    {
        await _daemonLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_daemon is not { HasExited: false }) return false;
            await _daemon.Rpc.ChangeGlobalOptionAsync(BuildGlobalAria2Options(settings), ct).ConfigureAwait(false);
            _lastDaemonSettings = settings;
            return true;
        }
        finally
        {
            _daemonLock.Release();
        }
    }

    internal static Dictionary<string, string> BuildGlobalAria2Options(DownloadSettings settings) => new(StringComparer.Ordinal)
    {
        ["max-concurrent-downloads"] = settings.Concurrency.ToString(CultureInfo.InvariantCulture),
        ["max-overall-download-limit"] = settings.SpeedLimit,
        ["max-download-limit"] = settings.PerTaskSpeedLimit,
        ["max-connection-per-server"] = settings.Connections.ToString(CultureInfo.InvariantCulture),
        ["split"] = settings.Splits.ToString(CultureInfo.InvariantCulture),
        ["min-split-size"] = settings.MinSplitSize,
        ["retry-wait"] = settings.RetryWait.ToString(CultureInfo.InvariantCulture),
        ["timeout"] = settings.Timeout.ToString(CultureInfo.InvariantCulture),
        ["connect-timeout"] = settings.Timeout.ToString(CultureInfo.InvariantCulture),
        ["continue"] = settings.Continue ? "true" : "false",
        ["user-agent"] = settings.UserAgent,
    };

    private static bool AreGlobalDaemonOptionsEqual(DownloadSettings a, DownloadSettings b)
        => a.Concurrency == b.Concurrency &&
           string.Equals(a.SpeedLimit, b.SpeedLimit, StringComparison.Ordinal) &&
           string.Equals(a.PerTaskSpeedLimit, b.PerTaskSpeedLimit, StringComparison.Ordinal) &&
           a.Connections == b.Connections &&
           a.Splits == b.Splits &&
           string.Equals(a.MinSplitSize, b.MinSplitSize, StringComparison.Ordinal) &&
           a.RetryWait == b.RetryWait &&
           a.Timeout == b.Timeout &&
           a.Continue == b.Continue &&
           string.Equals(a.UserAgent, b.UserAgent, StringComparison.Ordinal);

    private static Dictionary<string, string> BuildAria2AddOptions(
        string targetDir,
        string partFileName,
        string url,
        DownloadSettings settings)
    {
        var options = Aria2RpcClient.ResolveProxyOptions(settings, url);
        options["dir"] = targetDir;
        options["out"] = partFileName;
        options["max-tries"] = "1";
        options["max-connection-per-server"] = settings.Connections.ToString(CultureInfo.InvariantCulture);
        options["split"] = settings.Splits.ToString(CultureInfo.InvariantCulture);
        options["min-split-size"] = settings.MinSplitSize;
        options["max-download-limit"] = settings.PerTaskSpeedLimit;
        options["continue"] = settings.Continue ? "true" : "false";
        options["timeout"] = settings.Timeout.ToString(CultureInfo.InvariantCulture);
        options["connect-timeout"] = settings.Timeout.ToString(CultureInfo.InvariantCulture);
        options["user-agent"] = settings.UserAgent;
        options["allow-overwrite"] = "true";
        options["auto-file-renaming"] = "false";
        return options;
    }

    private void CleanupPartOnCancelIfNeeded(ActiveJob active)
    {
        var settings = IndexerSettings.LoadDownloadSettings(_settingsPath, _programDir);
        if (settings.KeepPartFiles || string.IsNullOrWhiteSpace(active.PartPath)) return;
        CleanupPartAndControl(active.PartPath);
    }

    private static void CleanupPartAndControl(string partPath)
    {
        for (var i = 0; i < 20; i++)
        {
            try
            {
                DownloadPathPolicy.TryDeleteSingleFile(partPath);
                DownloadPathPolicy.TryDeleteSingleFile(partPath + ".aria2");
                return;
            }
            catch (IOException)
            {
                if (i < 19) Thread.Sleep(50);
            }
            catch
            {
                return;
            }
        }
    }

    private void LoadDeadLinks()
    {
        try
        {
            if (!File.Exists(_deadLinksPath)) return;
            var node = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_deadLinksPath));
            if (node is null) return;
            lock (_stateLock)
            {
                foreach (var url in node)
                {
                    if (!string.IsNullOrWhiteSpace(url))
                        _deadUrls.Add(url.Trim());
                }
            }
        }
        catch
        {
            // 死链文件损坏时忽略
        }
    }

    private void MarkUrlDead(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        bool added;
        lock (_stateLock)
            added = _deadUrls.Add(url.Trim());
        if (added) SaveDeadLinksCore();
    }

    private void SaveDeadLinksCore()
    {
        lock (_deadLinksWriteLock)
        {
            var dir = Path.GetDirectoryName(_deadLinksPath);
            if (string.IsNullOrWhiteSpace(dir)) return;
            var check = DownloadPathPolicy.EnsureWritableDirectory(dir, _programDir, _settingsPath);
            if (!check.Ok) return;

            string[] snapshot;
            lock (_stateLock)
                snapshot = _deadUrls.OrderBy(u => u, StringComparer.OrdinalIgnoreCase).ToArray();

            string? tmp = null;
            try
            {
                tmp = Path.Combine(check.NormalizedPath, $"dead_links.{Guid.NewGuid():N}.tmp");
                File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonWriteOptions));
                File.Move(tmp, _deadLinksPath, overwrite: true);
                tmp = null;
            }
            catch
            {
                if (tmp is not null)
                {
                    try { DownloadPathPolicy.TryDeleteSingleFile(tmp); } catch { }
                }
            }
        }
    }

    private void TrackTask(Task task)
    {
        lock (_inFlightLock)
        {
            _inFlightTasks.Add(task);
        }

        _ = task.ContinueWith(static (t, state) =>
        {
            var engine = (DownloadEngine)state!;
            lock (engine._inFlightLock)
                engine._inFlightTasks.Remove(t);
        }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private string PickInitialHostLocked(IReadOnlyList<SourceRow> sources)
    {
        foreach (var source in sources)
        {
            var url = source.Url?.Trim() ?? "";
            if (url.Length == 0 || _deadUrls.Contains(url)) continue;
            var host = ExtractHost(url);
            if (!string.IsNullOrEmpty(host)) return host;
        }
        return "";
    }

    private bool IsPausedForSuspend()
    {
        lock (_stateLock) return _isSuspended;
    }

    private int GetActiveHostCountLocked(string host)
        => _activeHostCounts.TryGetValue(host, out var c) ? c : 0;

    private void IncrementHostCountLocked(string host)
    {
        if (string.IsNullOrEmpty(host)) return;
        _activeHostCounts[host] = GetActiveHostCountLocked(host) + 1;
    }

    private void DecrementHostCountLocked(string host)
    {
        if (string.IsNullOrEmpty(host)) return;
        var next = GetActiveHostCountLocked(host) - 1;
        if (next <= 0) _activeHostCounts.Remove(host);
        else _activeHostCounts[host] = next;
    }

    private void SwitchTrackedHost(ActiveJob active, string? newHost, bool signalPump)
    {
        var released = false;
        lock (_stateLock)
        {
            if (!string.IsNullOrEmpty(active.TrackedHost))
            {
                DecrementHostCountLocked(active.TrackedHost);
                active.TrackedHost = null;
                released = true;
            }

            if (!string.IsNullOrEmpty(newHost))
            {
                IncrementHostCountLocked(newHost);
                active.TrackedHost = newHost;
            }
        }

        if (signalPump && released)
            SignalPump();
    }

    private static string ExtractHost(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : "unknown";

    private void ReportProgress(DownloadProgressSnapshot snapshot)
        => _progress?.Report(snapshot);

    private void SignalPump()
    {
        try
        {
            if (!_disposed) _pumpWake.Release();
        }
        catch (SemaphoreFullException)
        {
            // 唤醒信号已挂起
        }
        catch (ObjectDisposedException)
        {
            // 已释放
        }
    }

    private static string FormatSpeed(long bytesPerSec)
    {
        if (bytesPerSec < 1024) return $"{bytesPerSec} B/s";
        if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024.0:0.0} KB/s";
        return $"{bytesPerSec / (1024.0 * 1024.0):0.0} MB/s";
    }

    internal enum AttemptOutcomeKind
    {
        Completed,
        DeadLink,
        Unavailable,
        DiskFull,
        SizeOrSpaceExceeded,
        Stalled,
        TransientError,
    }

    internal sealed record AttemptOutcome(AttemptOutcomeKind Kind, long TotalLength, string? ErrorMessage);

    private sealed class PendingJob(
        string entryId,
        string bundleId,
        string title,
        string version,
        string existingTargetPath,
        long generation)
    {
        public string EntryId { get; } = entryId;
        public string BundleId { get; } = bundleId;
        public string Title { get; } = title;
        public string Version { get; } = version;
        public string ExistingTargetPath { get; } = existingTargetPath;
        public long Generation { get; } = generation;
        public string InitialHost { get; set; } = "";
        public bool SourcesPrepared { get; set; }
    }

    internal sealed class ActiveJob(
        string entryId,
        string bundleId,
        string title,
        string version,
        string existingTargetPath,
        long generation,
        CancellationToken engineToken)
    {
        public string EntryId { get; } = entryId;
        public string BundleId { get; } = bundleId;
        public string Title { get; } = title;
        public string Version { get; } = version;
        public string ExistingTargetPath { get; } = existingTargetPath;
        public long Generation { get; } = generation;
        public CancellationTokenSource CancelSource { get; } = CancellationTokenSource.CreateLinkedTokenSource(engineToken);
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public volatile string? CurrentGid;
        public volatile string? TargetPath;
        public volatile string? PartPath;
        public volatile string? SourceSummary;
        public volatile string? TrackedHost;
        public volatile bool IsPaused;
        public volatile bool IsShuttingDown;
        public volatile bool KeepPartsOnShutdown;
        public volatile bool WasCancelledByUser;
        public double LastPercent;

        public void MarkCancelled() => WasCancelledByUser = true;
    }
}
