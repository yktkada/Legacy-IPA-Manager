using IdeviceGui.Services;
using IdeviceGui.ViewModels;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 队列显示顺序：正在执行最上 → 等待中 Pending（按服务端 OrderIndex）→ 挂起 → 暂停/需确认 → 已结束。
/// </summary>
public sealed class QueueOrderTests
{
    private static JobViewModel Job(string bundle, QueueStatus status, long peak, JobKind kind = JobKind.Install, int order = 0)
    {
        var item = new QueueItem
        {
            Udid = "0000-test",
            Kind = kind,
            BundleId = bundle,
            FilePath = @"C:\tmp\" + bundle + ".ipa",
            PackageKey = @"C:\tmp\" + bundle + ".ipa",
            PeakBytes = peak,
            OrderIndex = order,
        };
        return new JobViewModel(item, "Fake iPhone") { Status = status };
    }

    [Fact]
    public void 队列按PeakBytes降序_不按状态分段()
    {
        var jobs = new List<JobViewModel>
        {
            Job("small", QueueStatus.Pending, 10, order: 1),
            Job("done", QueueStatus.Done, 1_000, order: 2),
            Job("big", QueueStatus.Pending, 2_000, order: 0),
            Job("running", QueueStatus.Installing, 500),
            Job("paused", QueueStatus.Paused, 300),
            Job("failed", QueueStatus.Failed, 0),
        };

        var order = DeviceQueueViewModel.ComputeJobOrder(jobs);

        Assert.Equal(new[] { "big", "done", "running", "paused", "small", "failed" },
            order.Select(i => jobs[i].BundleId));
    }

    [Fact]
    public void 完成不改变队列位置()
    {
        var jobs = new List<JobViewModel>
        {
            Job("large", QueueStatus.Pending, 2_000, order: 0),
            Job("small", QueueStatus.Pending, 100, order: 1),
        };

        var before = DeviceQueueViewModel.ComputeJobOrder(jobs).Select(i => jobs[i].BundleId).ToArray();
        jobs[0].Status = QueueStatus.Done;
        var after = DeviceQueueViewModel.ComputeJobOrder(jobs).Select(i => jobs[i].BundleId).ToArray();

        Assert.Equal(new[] { "large", "small" }, before);
        Assert.Equal(before, after);
    }

    [Fact]
    public void 同一个包在队列只显示一行_Kind切换复用同一行()
    {
        using var f = new DeviceFixture();
        var item = new QueueItem
        {
            Udid = "0000-test",
            Kind = JobKind.Install,
            BundleId = "com.test.same",
            FilePath = @"C:\tmp\same.ipa",
            PackageKey = @"C:\tmp\same.ipa",
            PeakBytes = 123,
        };
        var job = f.Vm.AddJob(item);
        Assert.Single(f.Vm.Jobs);

        // 模拟"卸载复用同一行"：QueueItem 的 Kind/路径/状态被就地改写，再 AddJob 一次
        item.Kind = JobKind.Uninstall;
        item.FilePath = null;
        item.PackageKey = null;
        item.Status = QueueStatus.Pending;

        var same = f.Vm.AddJob(item);
        Assert.Same(job, same);
        Assert.Single(f.Vm.Jobs);
        Assert.Equal(JobKind.Uninstall, same.Kind);
        Assert.Equal("com.test.same", same.FileName);   // 卸载没有路径 → 显示 BundleId
    }

    private sealed class DeviceFixture : IDisposable
    {
        private readonly QueueService _queue;

        public DeviceFixture()
        {
            _queue = new QueueService(
                new FakeCliRunner(),
                new FakeAfcHelper(),
                readback: null,
                new RecordingProgress<QueueEvent>(),
                new RecordingProgress<LogLine>());
            Vm = new DeviceQueueViewModel(_queue, new DeviceInfo
            {
                Udid = "0000-test",
                Name = "Fake iPhone",
                Product = "iPhone4,1",
                Version = "7.1.1",
                CpuArch = "armv7",
            });
        }

        public DeviceQueueViewModel Vm { get; }

        public void Dispose() => _queue.ShutdownAsync(TimeSpan.FromMilliseconds(1)).GetAwaiter().GetResult();
    }

        [Fact]
    public void 全部Pending时_按OrderIndex排序()
    {
        var jobs = new List<JobViewModel>
        {
            Job("a", QueueStatus.Pending, 100, order: 2),
            Job("b", QueueStatus.Pending, 900, order: 0),
            Job("c", QueueStatus.Pending, 500, order: 1),
        };

        var order = DeviceQueueViewModel.ComputeJobOrder(jobs);
        Assert.Equal(new[] { "b", "c", "a" }, order.Select(i => jobs[i].BundleId));
    }

    [Fact]
    public void 待执行_按服务端OrderIndex_卸载优先顺序()
    {
        var jobs = new List<JobViewModel>
        {
            Job("install-big", QueueStatus.Pending, 9_000_000, order: 2),
            Job("uninstall-small", QueueStatus.Pending, 0, JobKind.Uninstall, order: 1),
            Job("install-small", QueueStatus.Pending, 10, order: 3),
            Job("uninstall-large-placeholder", QueueStatus.Pending, 123, JobKind.Uninstall, order: 0),
        };

        var order = DeviceQueueViewModel.ComputeJobOrder(jobs);
        Assert.Equal(
            new[] { "uninstall-large-placeholder", "uninstall-small", "install-big", "install-small" },
            order.Select(i => jobs[i].BundleId));
    }
}
