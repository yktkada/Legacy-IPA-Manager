using IdeviceGui.Services;
using IdeviceGui.ViewModels;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>Tab 分块与固定当前状态块（2026-11-06）。</summary>
public sealed class TabProgressTests
{
    private static (DeviceQueueViewModel Vm, QueueService Queue) NewVm()
    {
        var queue = new QueueService(
            new FakeCliRunner(), new FakeAfcHelper(), readback: null,
            new RecordingProgress<QueueEvent>(), new RecordingProgress<LogLine>());
        var vm = new DeviceQueueViewModel(queue, new DeviceInfo
        {
            Udid = "tab-test",
            Name = "Tab iPhone",
            Product = "iPhone4,1",
            Version = "7.1.1",
            CpuArch = "armv7",
        });
        return (vm, queue);
    }

    private static JobViewModel Add(DeviceQueueViewModel vm, string bundle, JobKind kind,
        QueueStatus status, long peak = 0)
    {
        var item = new QueueItem
        {
            Udid = "tab-test",
            Kind = kind,
            BundleId = bundle,
            FilePath = kind == JobKind.Install ? @"C:\tmp\" + bundle + ".ipa" : null,
            PackageKey = kind == JobKind.Install ? @"C:\tmp\" + bundle + ".ipa" : null,
            PeakBytes = peak,
            Status = status,
        };
        var job = vm.AddJob(item);
        job.Status = status;
        return job;
    }

    [Fact]
    public void 进度块按队列显示顺序_包含安装和卸载_取消不计()
    {
        var (vm, queue) = NewVm();
        Add(vm, "install-a", JobKind.Install, QueueStatus.Pending, 100);
        Add(vm, "uninstall-b", JobKind.Uninstall, QueueStatus.Pending);
        Add(vm, "install-c", JobKind.Install, QueueStatus.Cancelled, 900);
        Add(vm, "install-d", JobKind.Install, QueueStatus.Installing, 50);
        vm.ReorderJobs();
        vm.RecomputeProgress();

        Assert.Equal(2, vm.TabBlocks.Count);
        Assert.DoesNotContain(vm.TabBlocks, b => b.Visual == OperationVisual.None);
        Assert.Equal(OperationVisual.InstallActive, vm.CurrentVisual);
        var currentId = vm.Jobs.Single(j => j.Status == QueueStatus.Installing).Id;
        Assert.DoesNotContain(vm.TabBlocks, b => b.Id == currentId);
    }

    [Fact]
    public void 无当前任务但有暂停_固定状态块显示灰色()
    {
        var (vm, queue) = NewVm();
        Add(vm, "install-a", JobKind.Install, QueueStatus.Paused);
        vm.ReorderJobs();
        vm.RecomputeProgress();

        Assert.Equal(OperationVisual.Paused, vm.CurrentVisual);
        Assert.True(vm.CurrentStatusVisible);
        Assert.Empty(vm.TabBlocks);   // 固定灰块已表示暂停，进度条不再重复
    }

    [Fact]
    public void 无任务时没有固定状态块()
    {
        var (vm, queue) = NewVm();
        vm.RecomputeProgress();
        Assert.False(vm.CurrentStatusVisible);
        Assert.Empty(vm.TabBlocks);
    }
}
