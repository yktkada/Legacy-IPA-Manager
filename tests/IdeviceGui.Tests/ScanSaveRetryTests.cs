using System.IO;
using System.Threading;
using IdeviceGui.Services.Scanning;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 审核报告 P2-2 / P2-7：落库重试的纯逻辑（只对瞬时错误重试、有总预算、可取消）。
/// 单测直接打 <see cref="IpaScanner.SaveWithRetry"/>，不需要制造真实的锁库现场。
/// </summary>
public sealed class ScanSaveRetryTests
{
    private static readonly long NoDeadline = long.MaxValue;

    [Fact]
    public void 瞬时错误两次后成功_共尝试三次且结果是Saved()
    {
        var calls = 0;
        var attempt = IpaScanner.SaveWithRetry(
            () =>
            {
                if (Interlocked.Increment(ref calls) < 3) throw new IOException("库被占用");
            },
            "某批 30 行", null, CancellationToken.None, NoDeadline, maxAttempts: 3, delayMs: 1);

        Assert.Equal(SaveAttemptStatus.Saved, attempt.Status);
        Assert.Equal(3, attempt.Attempts);
        Assert.Equal(3, calls);
        Assert.Null(attempt.Error);
    }

    [Fact]
    public void 一直失败_达到上限判Failed并带上最后一次异常()
    {
        var calls = 0;
        var attempt = IpaScanner.SaveWithRetry(
            () =>
            {
                Interlocked.Increment(ref calls);
                throw new IOException("还是占用");
            },
            "某批 30 行", null, CancellationToken.None, NoDeadline, maxAttempts: 3, delayMs: 1);

        Assert.Equal(SaveAttemptStatus.Failed, attempt.Status);
        Assert.Equal(3, attempt.Attempts);
        Assert.Equal(3, calls);
        Assert.IsType<IOException>(attempt.Error);
    }

    [Fact]
    public void 不可重试的错误_只尝试一次()
    {
        var calls = 0;
        var attempt = IpaScanner.SaveWithRetry(
            () =>
            {
                Interlocked.Increment(ref calls);
                throw new ObjectDisposedException("library.db");
            },
            "某批 30 行", null, CancellationToken.None, NoDeadline, maxAttempts: 3, delayMs: 1);

        Assert.Equal(SaveAttemptStatus.Failed, attempt.Status);
        Assert.Equal(1, attempt.Attempts);   // 库都释放了，重试没意义（审核 P2-2）
        Assert.Equal(1, calls);
    }

    [Fact]
    public void 超出重试总预算_不再重试()
    {
        var calls = 0;
        var attempt = IpaScanner.SaveWithRetry(
            () =>
            {
                Interlocked.Increment(ref calls);
                throw new IOException("库被占用");
            },
            "某批 30 行", null, CancellationToken.None, deadlineTick: 0, maxAttempts: 3, delayMs: 1);

        Assert.Equal(SaveAttemptStatus.Failed, attempt.Status);
        Assert.Equal(1, attempt.Attempts);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void 取消后立刻返回Cancelled_不空等重试间隔()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var attempt = IpaScanner.SaveWithRetry(
            () =>
            {
                Interlocked.Increment(ref calls);
                cts.Cancel();                       // 第一次失败时用户关窗
                throw new IOException("库被占用");
            },
            "某批 30 行", null, cts.Token, NoDeadline, maxAttempts: 3, delayMs: 5000);

        sw.Stop();
        Assert.Equal(SaveAttemptStatus.Cancelled, attempt.Status);
        Assert.Equal(1, calls);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"取消应立刻返回（实际 {sw.ElapsedMilliseconds} ms）");
    }
}
