using IdeviceGui.Services;
using IdeviceGui.ViewModels;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 设备页签进度叠加（2026-11-02 用户定，docs/07 §3.10）：
/// 只算安装任务；分母 = 已完成 + 未完成 + 失败；**已取消不计**；
/// 进行中左→右填充；有失败转红；终态整格（全绿/全红）；0 任务不画；点页签 800 ms 可隐藏、队列变化重新出现。
/// </summary>
public sealed class DeviceQueueProgressTests
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

    private static void SetStatus(DeviceQueueViewModel vm, QueueStatus status, string bundleId)
    {
        var job = vm.Jobs.First(j => j.BundleId == bundleId);
        job.Status = status;
    }

    [Fact]
    public void 空间预报_显示所有任务完成后的已用总容量_低于500MB标红()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.Device.SetSpace(new DiskUsage(true, 10L * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024));
            Assert.Equal("2/10GB", vm.SpaceAfterText);
            Assert.False(vm.IsSpaceAfterLow);

            vm.AddJob(new QueueItem
            {
                Udid = Udid,
                Kind = JobKind.Install,
                BundleId = "com.space",
                FilePath = @"C:\tmp\space.ipa",
                PackageKey = @"C:\tmp\space.ipa",
                PeakBytes = 4L * 1024 * 1024 * 1024,
                ExpectedUncompressedBytes = 3L * 1024 * 1024 * 1024,
            });

            Assert.Equal("5/10GB", vm.SpaceAfterText);
            Assert.False(vm.IsSpaceAfterLow);

            vm.Device.SetSpace(new DiskUsage(true, 10L * 1024 * 1024 * 1024, 3L * 1024 * 1024 * 1024));
            Assert.Equal("10/10GB", vm.SpaceAfterText);
            Assert.True(vm.IsSpaceAfterLow);
        }
    }

    [Fact]
    public void 空间预报_空间未知显示占位()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            Assert.Equal("—", vm.SpaceAfterText);
            Assert.False(vm.IsSpaceAfterLow);
        }
    }

    [Fact]
    public void 无任务_不画()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            Assert.Equal(QueueProgressVisual.None, vm.ProgressVisual);
            Assert.False(vm.ProgressVisible);
            Assert.Equal(0, vm.ProgressTotal);
        }
    }

    [Fact]
    public void 有排队任务_进行中_最少百分之一宽()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.AddJob(Install("com.a"));

            Assert.Equal(QueueProgressVisual.InProgress, vm.ProgressVisual);
            Assert.True(vm.ProgressVisible);
            Assert.Equal(0.01d, vm.ProgressFraction, 3);   // 2026-11-03：有任务在跑就留一条细边
            Assert.Equal(1, vm.ProgressTotal);
        }
    }

    [Fact]
    public void 完成一半_进行中_比例05()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.AddJob(Install("com.a"));
            vm.AddJob(Install("com.b"));
            SetStatus(vm, QueueStatus.Done, "com.a");
            vm.RecomputeProgress();

            Assert.Equal(QueueProgressVisual.InProgress, vm.ProgressVisual);
            Assert.Equal(0.5d, vm.ProgressFraction, 3);
            Assert.Equal(1, vm.ProgressDone);
            Assert.Equal(1, vm.ProgressUnfinished);
        }
    }

    // ── 2026-11-03 用户报"进行中没有进度条显示"：分子 = 已完成 + 当前运行任务自身百分比 ──

    [Fact]
    public void 单任务传输40percent_按前60percent折算_条宽24percent()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.AddJob(Install("com.a"));
            SetStatus(vm, QueueStatus.Installing, "com.a");
            vm.Jobs[0].Percent = 40;          // 传输 40%
            vm.RecomputeProgress();

            Assert.Equal(QueueProgressVisual.InProgress, vm.ProgressVisual);
            Assert.Equal(0.24d, vm.ProgressFraction, 3);   // 0.6 × 40%
            Assert.True(vm.ProgressVisible);
        }
    }

    [Fact]
    public void 传输进度事件_会驱动条宽_不需要等状态变化()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.AddJob(Install("com.a"));
            SetStatus(vm, QueueStatus.Installing, "com.a");
            var job = vm.Jobs[0];

            // 只有传输事件（没有状态变化、没有安装事件）→ 条宽也要跟着涨
            vm.Apply(new JobProgressEvent(Udid, job.Id, 50));

            Assert.Equal(0.3d, vm.ProgressFraction, 3);   // 0.6 × 50%
        }
    }

    [Fact]
    public void 单任务安装50percent_条宽80percent()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.AddJob(Install("com.a"));
            SetStatus(vm, QueueStatus.Installing, "com.a");
            vm.Jobs[0].Percent = 100;           // 传输完成
            vm.Apply(new JobInstallProgressEvent(Udid, vm.Jobs[0].Id, 50));   // 安装到 50%

            Assert.Equal(0.8d, vm.ProgressFraction, 3);    // 0.6 + 0.4×50%
        }
    }

    [Fact]
    public void 单任务安装90percent_条宽96percent()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.AddJob(Install("com.a"));
            SetStatus(vm, QueueStatus.Installing, "com.a");
            vm.Jobs[0].Percent = 100;
            vm.Apply(new JobInstallProgressEvent(Udid, vm.Jobs[0].Id, 90));

            Assert.Equal(0.96d, vm.ProgressFraction, 3);
        }
    }

    [Fact]
    public void 传输刚完成_未收到安装行_条宽60percent()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.AddJob(Install("com.a"));
            SetStatus(vm, QueueStatus.Installing, "com.a");
            vm.Jobs[0].Percent = 100;
            vm.Jobs[0].InstallPercent = 0;
            vm.RecomputeProgress();

            Assert.Equal(0.6d, vm.ProgressFraction, 3);
        }
    }

    [Fact]
    public void 传输完成_只到60percent_仍是进行中()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.AddJob(Install("com.a"));
            SetStatus(vm, QueueStatus.Installing, "com.a");
            vm.Jobs[0].Percent = 100;
            vm.RecomputeProgress();

            Assert.Equal(0.6d, vm.ProgressFraction, 3);
            Assert.Equal(QueueProgressVisual.InProgress, vm.ProgressVisual);
        }
    }

    [Fact]
    public void 刚开始还没有百分比_至少百分之一宽()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.AddJob(Install("com.a"));   // Pending，Percent=0

            Assert.Equal(QueueProgressVisual.InProgress, vm.ProgressVisual);
            Assert.Equal(0.01d, vm.ProgressFraction, 3);
        }
    }

    [Fact]
    public void 已暂停_保留中断时的百分比()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.AddJob(Install("com.a"));
            SetStatus(vm, QueueStatus.Paused, "com.a");
            vm.Jobs[0].Percent = 30;
            vm.RecomputeProgress();

            Assert.Equal(0.18d, vm.ProgressFraction, 3);   // 0.6 × 30%
            Assert.Equal(QueueProgressVisual.InProgress, vm.ProgressVisual);
        }
    }

    [Fact]
    public void 多任务_已完成一半加运行中一半_四分之三()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.AddJob(Install("com.a"));
            vm.AddJob(Install("com.b"));
            SetStatus(vm, QueueStatus.Done, "com.a");
            SetStatus(vm, QueueStatus.Installing, "com.b");
            vm.Jobs[1].Percent = 50;
            vm.RecomputeProgress();

            Assert.Equal(0.65d, vm.ProgressFraction, 3);   // (1 + 0.6×50%) / 2
        }
    }

    [Fact]
    public void 多任务_一个装完一个安装一半_条宽90percent()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.AddJob(Install("com.a"));
            vm.AddJob(Install("com.b"));
            SetStatus(vm, QueueStatus.Done, "com.a");
            SetStatus(vm, QueueStatus.Installing, "com.b");
            vm.Jobs[1].Percent = 100;
            vm.Apply(new JobInstallProgressEvent(Udid, vm.Jobs[1].Id, 50));

            Assert.Equal(0.9d, vm.ProgressFraction, 3);    // (1 + 0.8) / 2
        }
    }

    [Fact]
    public void 安装段0percent_也算进入安装段_条宽60percent()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.AddJob(Install("com.a"));
            SetStatus(vm, QueueStatus.Installing, "com.a");
            vm.Jobs[0].Percent = 100;

            // 审查 N9：老设备可能先来一条 `Install: … (0%)`——必须立即算"已进入安装段"
            vm.Apply(new JobInstallProgressEvent(Udid, vm.Jobs[0].Id, 0));

            Assert.True(vm.Jobs[0].InstallStarted);
            Assert.Equal(0.6d, vm.ProgressFraction, 3);
        }
    }

    [Fact]
    public void 全部完成_终态_满格填绿()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.AddJob(Install("com.a"));
            vm.AddJob(Install("com.b"));
            SetStatus(vm, QueueStatus.Done, "com.a");
            SetStatus(vm, QueueStatus.Done, "com.b");
            vm.RecomputeProgress();

            Assert.Equal(QueueProgressVisual.AllDone, vm.ProgressVisual);
            Assert.True(vm.IsProgressTerminal);
            Assert.Equal(1d, vm.ProgressFillFraction);
            Assert.Equal(1d, vm.ProgressFraction, 3);
        }
    }

    [Fact]
    public void 有失败且无未完成_终态红_即使没到100百分比也满格()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.AddJob(Install("com.a"));
            vm.AddJob(Install("com.b"));
            SetStatus(vm, QueueStatus.Done, "com.a");
            SetStatus(vm, QueueStatus.Failed, "com.b");
            vm.RecomputeProgress();

            Assert.Equal(QueueProgressVisual.AllFailed, vm.ProgressVisual);
            Assert.Equal(0.5d, vm.ProgressFraction, 3);     // 失败不算"已完成"
            Assert.Equal(1d, vm.ProgressFillFraction);      // 但终态整格填充
        }
    }

    [Fact]
    public void 还在进行但已失败_进度条转红_仍是进度条形态()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.AddJob(Install("com.a"));
            vm.AddJob(Install("com.b"));
            vm.AddJob(Install("com.c"));
            SetStatus(vm, QueueStatus.Failed, "com.a");
            SetStatus(vm, QueueStatus.Done, "com.b");
            vm.RecomputeProgress();

            Assert.Equal(QueueProgressVisual.FailedInProgress, vm.ProgressVisual);
            Assert.False(vm.IsProgressTerminal);
            Assert.Equal(1d / 3d, vm.ProgressFraction, 3);
        }
    }

    [Fact]
    public void 已取消不计入分母()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.AddJob(Install("com.a"));
            vm.AddJob(Install("com.b"));
            SetStatus(vm, QueueStatus.Done, "com.a");
            SetStatus(vm, QueueStatus.Cancelled, "com.b");
            vm.RecomputeProgress();

            Assert.Equal(1, vm.ProgressTotal);
            Assert.Equal(QueueProgressVisual.AllDone, vm.ProgressVisual);
        }
    }

    [Fact]
    public void 卸载任务不计入进度()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.AddJob(Install("com.a"));
            vm.Jobs.Add(new JobViewModel(new QueueItem
            {
                Udid = Udid,
                Kind = JobKind.Uninstall,
                BundleId = "com.b",
                FilePath = @"C:\tmp\b.ipa",
                PackageKey = @"C:\tmp\b.ipa",
            }, "Fake"));
            vm.RecomputeProgress();

            Assert.Equal(1, vm.ProgressTotal);
        }
    }

    [Fact]
    public void 点页签隐藏终态_队列再变化重新出现()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.AddJob(Install("com.a"));
            SetStatus(vm, QueueStatus.Done, "com.a");
            vm.RecomputeProgress();
            Assert.True(vm.ProgressVisible);

            vm.SuppressProgress();
            Assert.False(vm.ProgressVisible);

            // 队列再变化（新任务入队）→ 重新显示
            vm.AddJob(Install("com.b"));
            Assert.True(vm.ProgressVisible);
        }
    }

    [Fact]
    public void 非终态时点页签不隐藏()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.AddJob(Install("com.a"));
            vm.SuppressProgress();

            Assert.True(vm.ProgressVisible);   // 只有终态才允许隐藏
        }
    }

    [Fact]
    public void 空间预报_在飞任务不重复计入()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.AddJob(new QueueItem
            {
                Udid = Udid,
                Kind = JobKind.Install,
                BundleId = "com.running",
                FilePath = @"C:\tmp\running.ipa",
                PackageKey = @"C:\tmp\running.ipa",
                PeakBytes = 4L * 1024 * 1024 * 1024,
                ExpectedUncompressedBytes = 3L * 1024 * 1024 * 1024,
            });
            SetStatus(vm, QueueStatus.Installing, "com.running");
            vm.Device.SetSpace(new DiskUsage(true, 10L * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024));

            // 当前已用 2 GB 已包含在飞任务的占用；不能再把 3 GB 加一次。
            Assert.Equal("2/10GB", vm.SpaceAfterText);
            Assert.False(vm.IsSpaceAfterLow);
        }
    }

    [Fact]
    public void 空间预报_缺InstalledBytes_回退Peak并标估算()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.Device.SetSpace(new DiskUsage(true, 10L * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024));
            vm.AddJob(new QueueItem
            {
                Udid = Udid,
                Kind = JobKind.Install,
                BundleId = "com.unknown-size",
                FilePath = @"C:\tmp\unknown.ipa",
                PackageKey = @"C:\tmp\unknown.ipa",
                PeakBytes = 4L * 1024 * 1024 * 1024,
                ExpectedUncompressedBytes = null,
            });

            Assert.Equal("≥6/10GB", vm.SpaceAfterText);
            Assert.False(vm.IsSpaceAfterLow);
        }
    }

    [Fact]
    public void 空间预报_超出总容量_显示大于号并标红()
    {
        var vm = NewVm(out var fixture);
        using (fixture)
        {
            vm.Device.SetSpace(new DiskUsage(true, 10L * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024));
            vm.AddJob(new QueueItem
            {
                Udid = Udid,
                Kind = JobKind.Install,
                BundleId = "com.too-big",
                FilePath = @"C:\tmp\big.ipa",
                PackageKey = @"C:\tmp\big.ipa",
                PeakBytes = 12L * 1024 * 1024 * 1024,
                ExpectedUncompressedBytes = 9L * 1024 * 1024 * 1024,
            });

            Assert.Equal(">10/10GB", vm.SpaceAfterText);
            Assert.True(vm.IsSpaceAfterLow);
        }
    }

    /// <summary>最小夹具：真 QueueService + 假 CLI/AFC + 一台假设备（与 ClearFinishedTests 同款）。</summary>
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
