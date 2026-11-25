namespace IdeviceGui.Services;

/// <summary>
/// 回读完成后的队列空间预演（2026-11-07 用户定）：
/// **只提示、不改队列**——按引擎执行顺序逐个模拟安装任务，返回当前空间下装不下的任务。
/// <para>
/// 口径：峰值 = 压缩包 + 解压后；解压体积未知时按压缩包 × 2（用户定）。
/// 装不下的任务不扣减可用空间（模拟它不会执行），继续评估后面的任务。
/// 卸载任务不占空间，直接跳过；运行中的任务不参与（它已经开始）。
/// </para>
/// </summary>
internal sealed record QueueSpaceProjection(
    long FreeBytes,
    IReadOnlyList<QueueItem> CannotFit);

internal static class QueueSpaceProjectionCalculator
{
    /// <summary>与 QueueService 的单任务空间检查同一余量。</summary>
    public const long MarginBytes = 10L * 1024 * 1024;

    /// <summary>
    /// 峰值占用的唯一公式：压缩包 + 解压后；解压未知按压缩包 ×2（两处都为 0 时给 0）。
    /// 入队闸门、QueueService 入队快照、回读预演共用，避免三套口径。
    /// </summary>
    public static long PeakBytes(long fileBytes, long? expectedUncompressedBytes)
    {
        if (fileBytes < 0) fileBytes = 0;
        return expectedUncompressedBytes is > 0
            ? fileBytes + expectedUncompressedBytes.Value
            : fileBytes * 2;
    }

    /// <summary>
    /// 入队前的快速空间判断：前序安装任务的**最终占用** + 新包峰值 + 10 MB 余量。
    /// 队列串行执行，前序任务的压缩包 staging 不会与新包峰值同时存在。
    /// 只在已知设备容量时调用；峰值未知（0）时不阻止入队，交给安装前检查。
    /// </summary>
    public static bool CanEnqueue(long freeBytes, long queuedInstallBytes, long newPeakBytes) =>
        TryReserve(freeBytes, queuedInstallBytes, newPeakBytes, out _);

    /// <summary>同上，同时返回拒绝时用于提示的 required（调用方不再自己拼公式）。</summary>
    public static bool TryReserve(
        long freeBytes,
        long queuedInstallBytes,
        long newPeakBytes,
        out long required)
    {
        required = queuedInstallBytes + newPeakBytes + MarginBytes;
        if (freeBytes <= 0 || newPeakBytes <= 0) return true;
        return freeBytes >= required;
    }

    public static QueueSpaceProjection Check(long freeBytes, IReadOnlyList<QueueItem> snapshot)
    {
        var canNotFit = new List<QueueItem>();
        var free = freeBytes;

        foreach (var item in snapshot)
        {
            if (item.Kind != JobKind.Install) continue;
            if (item.Status.IsFinished() || item.Status.IsInFlight()) continue;

            var peak = PeakBytes(item);
            if (peak <= 0)
            {
                // 体积未知到连压缩包都读不到：无法证明放得下，按装不下提示。
                canNotFit.Add(item);
                continue;
            }

            var required = peak + MarginBytes;
            if (free < required)
            {
                canNotFit.Add(item);
                continue;   // 不执行，不扣 free
            }

            // 串行执行：当前包需要 peak 才装得下；装完后只留下最终占用。
            free -= FinalBytes(item);
        }

        return new QueueSpaceProjection(freeBytes, canNotFit);
    }

    /// <summary>
    /// 峰值统一由入队时算进 <see cref="QueueItem.PeakBytes"/>（解压未知按压缩 ×2）。
    /// 最终占用优先 <see cref="QueueItem.FinalBytes"/>；旧对象没有该值时退回峰值（保守）。
    /// 投影在 UI 线程只读内存，不再做 FileInfo 同步 IO。
    /// </summary>
    private static long PeakBytes(QueueItem item) => item.PeakBytes > 0 ? item.PeakBytes : 0;

    private static long FinalBytes(QueueItem item) =>
        item.FinalBytes > 0 ? Math.Min(item.FinalBytes, PeakBytes(item)) : PeakBytes(item);
}
