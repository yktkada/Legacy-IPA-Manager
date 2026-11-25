using IdeviceGui.Services;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>回读后空间预演（2026-11-07）：只算提示，不改队列。</summary>
public sealed class QueueSpaceProjectionTests
{
    private static QueueItem Install(string bundle, long peak, long? expected = 1, QueueStatus status = QueueStatus.Pending)
        => new()
        {
            Udid = "space-test",
            Kind = JobKind.Install,
            BundleId = bundle,
            FilePath = @"C:\tmp\" + bundle + ".ipa",
            PeakBytes = peak,
            FinalBytes = expected is > 0 ? expected.Value : peak,
            ExpectedUncompressedBytes = expected,
            Status = status,
        };

    [Fact]
    public void 按顺序模拟_装不下的任务不扣空间_后面的任务仍可评估()
    {
        var items = new[]
        {
            Install("a", 7 * 1024 * 1024, 1024 * 1024),
            Install("b", 2 * 1024 * 1024, 1024 * 1024),
            Install("c", 9 * 1024 * 1024, 1024 * 1024),
        };

        var result = QueueSpaceProjectionCalculator.Check(20L * 1024 * 1024, items);

        // a: 7+10=17 <=20 → free 13；b: 2+10=12 <=13 → free 11；c: 9+10=19 >11
        Assert.Equal("c", Assert.Single(result.CannotFit).BundleId);
    }

    [Fact]
    public void 卸载任务不占空间_运行中与已结束不参与()
    {
        var items = new[]
        {
            new QueueItem
            {
                Udid = "space-test", Kind = JobKind.Uninstall, BundleId = "u",
                Status = QueueStatus.Pending,
            },
            Install("done", 999, 1, QueueStatus.Done),
            Install("running", 999, 1, QueueStatus.Installing),
            Install("small", 1, 1),
        };

        var result = QueueSpaceProjectionCalculator.Check(12L * 1024 * 1024, items);
        Assert.Empty(result.CannotFit);   // small: 1+10 <= 10? 注意 1MB+10MB > 10MB
    }

    [Fact]
    public void 入队前空间判断_包含已排队峰值与新包峰值和余量()
    {
        const long mb = 1024 * 1024;
        Assert.True(QueueSpaceProjectionCalculator.CanEnqueue(20 * mb, 3 * mb, 2 * mb));
        Assert.False(QueueSpaceProjectionCalculator.CanEnqueue(14 * mb, 3 * mb, 2 * mb)); // 3+2+10 > 14
        Assert.True(QueueSpaceProjectionCalculator.CanEnqueue(0, 3 * mb, 2 * mb));       // 空间未知不拦
        Assert.True(QueueSpaceProjectionCalculator.CanEnqueue(10 * mb, 3 * mb, 0));     // 包峰值未知不拦
    }

    [Fact]
    public void 峰值公式_已知解压相加_未知按压缩乘二()
    {
        const long mb = 1024 * 1024;
        Assert.Equal(7 * mb, QueueSpaceProjectionCalculator.PeakBytes(3 * mb, 4 * mb));
        Assert.Equal(6 * mb, QueueSpaceProjectionCalculator.PeakBytes(3 * mb, null));
        Assert.Equal(6 * mb, QueueSpaceProjectionCalculator.PeakBytes(3 * mb, 0));
        Assert.Equal(0, QueueSpaceProjectionCalculator.PeakBytes(0, null));
    }

    [Fact]
    public void TryReserve_返回拒绝提示用的required()
    {
        const long mb = 1024 * 1024;
        Assert.False(QueueSpaceProjectionCalculator.TryReserve(14 * mb, 3 * mb, 2 * mb, out var required));
        Assert.Equal(15 * mb, required);
        Assert.True(QueueSpaceProjectionCalculator.TryReserve(0, 3 * mb, 2 * mb, out _));
    }

    [Fact]
    public void 前序任务按最终体积累加_不把压缩包峰值重复算()
    {
        const long mb = 1024 * 1024;
        QueueItem App(string bundle, long peak, long final) => new()
        {
            Udid = "space-test",
            Kind = JobKind.Install,
            BundleId = bundle,
            FilePath = @"C:\tmp\" + bundle + ".ipa",
            PeakBytes = peak,
            FinalBytes = final,
            ExpectedUncompressedBytes = final,
            Status = QueueStatus.Pending,
        };

        var items = new[] { App("a", 1000 * mb, 500 * mb), App("b", 1000 * mb, 500 * mb), App("c", 1000 * mb, 500 * mb) };

        // 2.5 GB：旧算法扣三次 peak（3 GB）会误判 c 装不下；正确算法每个包装完只留 500 MB。
        var result = QueueSpaceProjectionCalculator.Check(2500 * mb, items);
        Assert.Empty(result.CannotFit);

        // 入队闸门：4 个前序包各 peak 1 GB、最终 500 MB + 新包 peak 1 GB = 3 GB，而不是 5 GB。
        Assert.True(QueueSpaceProjectionCalculator.TryReserve(3010 * mb, 2000 * mb, 1000 * mb, out var required));
        Assert.Equal(3010 * mb, required);
        Assert.False(QueueSpaceProjectionCalculator.TryReserve(3009 * mb, 2000 * mb, 1000 * mb, out _));
    }

    [Fact]
    public void 解压体积未知_按压缩大小乘二()
    {
        // 解压未知的 2× 已在入队时算进 PeakBytes；投影只读内存，不再 FileInfo。
        var item = new QueueItem
        {
            Udid = "space-test",
            Kind = JobKind.Install,
            BundleId = "unknown",
            FilePath = @"C:\does-not-exist\unknown.ipa",
            PeakBytes = 1024 * 2,
            ExpectedUncompressedBytes = null,
            Status = QueueStatus.Pending,
        };
        var free = 10L * 1024 * 1024 + 1024 * 2;   // 2x 刚好 + 余量
        Assert.Empty(QueueSpaceProjectionCalculator.Check(free, new[] { item }).CannotFit);
        Assert.Single(QueueSpaceProjectionCalculator.Check(free - 1, new[] { item }).CannotFit);
    }
}
