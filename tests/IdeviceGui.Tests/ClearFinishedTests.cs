using IdeviceGui.Services;
using IdeviceGui.ViewModels;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 「清空已结束」（2026-11-01 用户定）：真实操作 <see cref="DeviceQueueViewModel.ClearFinished"/>，
/// 断言**哪些行被删、哪些留下**（审查报告 T2：原先只测分类函数，清了 Done 却漏 Failed/Cancelled 也不会红）。
/// </summary>
public sealed class ClearFinishedTests
{
    private static readonly QueueStatus[] AllStatuses =
    {
        QueueStatus.Pending, QueueStatus.Installing, QueueStatus.Paused, QueueStatus.NeedsConfirm,
        QueueStatus.Done, QueueStatus.Failed, QueueStatus.Cancelled,
    };

    [Fact]
    public void ClearFinished_只删已结束_保留排队运行暂停待确认()
    {
        using var fixedDevice = new FakeDevice();
        var vm = fixedDevice.CreateQueueViewModel();

        foreach (var status in AllStatuses)
        {
            var item = new QueueItem
            {
                Udid = FakeDevice.Udid,
                Kind = JobKind.Install,
                BundleId = "com.test." + status,
                FilePath = @"C:\tmp\" + status + ".ipa",
                PackageKey = @"C:\tmp\" + status + ".ipa",
            };
            vm.AddJob(item);
            vm.Jobs[^1].Status = status;   // AddJob 新建的 JobViewModel 默认 Pending，这里显式设成目标状态
        }

        Assert.Equal(AllStatuses.Length, vm.Jobs.Count);

        vm.ClearFinished();

        var remaining = vm.Jobs.Select(j => j.Status).ToArray();
        var expected = new[] { QueueStatus.Pending, QueueStatus.Installing, QueueStatus.Paused, QueueStatus.NeedsConfirm };
        Assert.True(expected.ToHashSet().SetEquals(remaining),
            $"清空后应剩 [{string.Join(", ", expected)}]，实际 [{string.Join(", ", remaining)}]");
        Assert.DoesNotContain(vm.Jobs, j => j.Status is QueueStatus.Done or QueueStatus.Failed or QueueStatus.Cancelled);
    }

    [Fact]
    public void ClearFinished_没有已结束项时不动()
    {
        using var fixedDevice = new FakeDevice();
        var vm = fixedDevice.CreateQueueViewModel();
        var item = new QueueItem
        {
            Udid = FakeDevice.Udid,
            Kind = JobKind.Install,
            BundleId = "com.test.pending",
            FilePath = @"C:\tmp\pending.ipa",
            PackageKey = @"C:\tmp\pending.ipa",
        };
        vm.AddJob(item);

        vm.ClearFinished();

        Assert.Single(vm.Jobs);
    }

    /// <summary>最小夹具：一套真 QueueService + 假 CLI/AFC，一台假设备。</summary>
    private sealed class FakeDevice : IDisposable
    {
        public const string Udid = "0000-test-fake-udid";

        private readonly QueueService _queue;

        public FakeDevice()
        {
            _queue = new QueueService(
                new FakeCliRunner(),
                new FakeAfcHelper(),
                readback: null,
                new RecordingProgress<QueueEvent>(),
                new RecordingProgress<LogLine>());
        }

        public DeviceQueueViewModel CreateQueueViewModel()
            => new(_queue, new DeviceInfo
            {
                Udid = Udid,
                Name = "Fake iPhone",
                Product = "iPhone4,1",
                Version = "7.1.1",
                CpuArch = "armv7",
            });

        public void Dispose() => _queue.ShutdownAsync(TimeSpan.FromMilliseconds(1)).GetAwaiter().GetResult();
    }
}
