using IdeviceGui.Resources;
using IdeviceGui.Services;
using IdeviceGui.ViewModels;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 设备离线/插回状态机（2026-11-02 用户报 bug）：
/// 队列判离线 → Online=false + RunState=Offline；插回（MarkOnline）→ Online=true 且 **Offline 必须复位成 Idle**，
/// 否则界面一直显示"设备离线"。
/// </summary>
public sealed class DeviceOnlineTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly QueueService _queue;

        public Fixture()
        {
            _queue = new QueueService(
                new FakeCliRunner(),
                new FakeAfcHelper(),
                readback: null,
                new RecordingProgress<QueueEvent>(),
                new RecordingProgress<LogLine>());
            Vm = new DeviceQueueViewModel(_queue, new DeviceInfo
            {
                Udid = "0000-test-online",
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
    public void 离线_标记后在线为false且状态是离线()
    {
        using var f = new Fixture();
        var vm = f.Vm;

        vm.MarkOffline();

        Assert.False(vm.Device.Online);
        Assert.Equal(QueueRunState.Offline, vm.RunState);
        Assert.Equal(Strings.Queue_StateNote_DeviceOffline, vm.StateNote);
    }

    [Fact]
    public void 插回_在线恢复且Offline复位成Idle()
    {
        using var f = new Fixture();
        var vm = f.Vm;

        vm.MarkOffline();
        vm.MarkOnline();

        Assert.True(vm.Device.Online);
        Assert.Equal(QueueRunState.Idle, vm.RunState);   // 关键：不能留着 Offline
        Assert.Null(vm.StateNote);
    }

    [Fact]
    public void 插回_不影响正在跑的状态()
    {
        using var f = new Fixture();
        var vm = f.Vm;

        vm.Apply(new DeviceRunStateEvent(vm.Device.Udid, QueueRunState.Running, null));
        vm.MarkOnline();

        Assert.True(vm.Device.Online);
        Assert.Equal(QueueRunState.Running, vm.RunState);
    }
}
