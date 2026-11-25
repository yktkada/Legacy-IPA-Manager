using IdeviceGui.Services;
using IdeviceGui.ViewModels;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// T6 接线测试（2026-11-03）：**只发事件**，不手动调用 `RecomputeProgress` / 不手动改状态，
/// 断言 ViewModel 自己完成了该做的事。
/// <para>
/// 背景：11-03「传输阶段页签进度不涨」没被抓住，就是因为测试里手动补了 `RecomputeProgress()`——
/// 测试替生产代码走了一步，接线断掉永远发现不了。
/// </para>
/// </summary>
public sealed class DeviceQueueWiringTests
{
    private const string Udid = "0000-test-fake-udid";

    private static DeviceQueueViewModel NewVm(out FakeDevice fixture)
    {
        fixture = new FakeDevice();
        return fixture.CreateQueueViewModel();
    }

    private static QueueItem Install(string bundleId) => new()
    {
        Udid = Udid,
        Kind = JobKind.Install,
        BundleId = bundleId,
        FilePath = $@"C:\tmp\{bundleId}.ipa",
        PackageKey = $@"C:\tmp\{bundleId}.ipa",
    };

    [Fact]
    public void 状态事件_Done_不手动重算也会变成终态满格()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.AddJob(Install("com.a"));

            // 只发事件：Apply 内部必须自己 Recompute（以前测试替它调了，接线条 bug 抓不到）
            vm.Apply(new JobStatusEvent(Udid, vm.Jobs[0].Id, QueueStatus.Done, null));

            Assert.True(vm.IsProgressTerminal);
            Assert.Equal(1d, vm.ProgressFillFraction, 3);
            Assert.Equal(QueueProgressVisual.AllDone, vm.ProgressVisual);
        }
    }

    [Fact]
    public void 进度事件_不手动重算也会更新条宽()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.AddJob(Install("com.a"));

            vm.Apply(new JobProgressEvent(Udid, vm.Jobs[0].Id, 40));

            Assert.Equal(40d, vm.Jobs[0].Percent, 3);
            Assert.Equal(40d, vm.CurrentPercent, 3);
            Assert.Equal(0.24d, vm.ProgressFraction, 3);   // 40% × 60%（传输段）
        }
    }

    [Fact]
    public void 安装事件_不手动重算也会进入安装段并更新条宽()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.AddJob(Install("com.a"));
            vm.Apply(new JobProgressEvent(Udid, vm.Jobs[0].Id, 100));   // 传输完成

            vm.Apply(new JobInstallProgressEvent(Udid, vm.Jobs[0].Id, 50));

            Assert.True(vm.Jobs[0].InstallStarted);
            Assert.Equal(0.8d, vm.ProgressFraction, 3);   // 0.6 + 0.4 × 50%
        }
    }

    [Fact]
    public void 状态事件_Pending_不手动重算也会清掉安装段与百分比()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.AddJob(Install("com.a"));
            vm.Apply(new JobProgressEvent(Udid, vm.Jobs[0].Id, 80));
            vm.Apply(new JobInstallProgressEvent(Udid, vm.Jobs[0].Id, 30));
            Assert.True(vm.Jobs[0].InstallStarted);

            vm.Apply(new JobStatusEvent(Udid, vm.Jobs[0].Id, QueueStatus.Pending, null));

            Assert.False(vm.Jobs[0].InstallStarted);
            Assert.Equal(0d, vm.Jobs[0].InstallPercent, 3);
            Assert.Equal(0d, vm.Jobs[0].Percent, 3);
        }
    }

    [Fact]
    public void 回读事件_不手动重算也会更新已安装集合并发通知()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            var notified = new List<string?>();
            vm.PropertyChanged += (_, args) => notified.Add(args.PropertyName);

            var apps = new List<InstalledApp> { new("com.a", "1.0", VersionKnown: true) };
            vm.Apply(new InstalledAppsEvent(Udid, apps));

            Assert.NotNull(vm.InstalledApps);
            Assert.Single(vm.InstalledApps!);
            Assert.Contains(nameof(DeviceQueueViewModel.InstalledApps), notified);
            Assert.Contains(nameof(DeviceQueueViewModel.InstalledAppsText), notified);
        }
    }

    [Fact]
    public void 未知任务的事件_不崩也不改其它行()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.AddJob(Install("com.a"));
            var before = vm.Jobs[0].Status;

            vm.Apply(new JobStatusEvent(Udid, Guid.NewGuid(), QueueStatus.Done, null));
            vm.Apply(new JobProgressEvent(Udid, Guid.NewGuid(), 99));

            Assert.Equal(before, vm.Jobs[0].Status);
            Assert.Equal(0d, vm.Jobs[0].Percent, 3);
        }
    }

    /// <summary>最小夹具：真 QueueService + 假 CLI/AFC + 一台假设备（与 DeviceQueueProgressTests 同款）。</summary>
    private sealed class FakeDevice : IDisposable
    {
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
