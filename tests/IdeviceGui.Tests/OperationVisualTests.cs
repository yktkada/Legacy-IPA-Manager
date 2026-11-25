using IdeviceGui.Services;
using IdeviceGui.ViewModels;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>统一操作颜色状态（2026-11-06）：所有界面共用，不能再各写一套。</summary>
public sealed class OperationVisualTests
{
    [Theory]
    [InlineData((int)JobKind.Install, (int)QueueStatus.Pending, (int)OperationVisual.InstallWaiting)]
    [InlineData((int)JobKind.Install, (int)QueueStatus.Precheck, (int)OperationVisual.InstallWaiting)]
    [InlineData((int)JobKind.Install, (int)QueueStatus.Installing, (int)OperationVisual.InstallActive)]
    [InlineData((int)JobKind.Install, (int)QueueStatus.Readback, (int)OperationVisual.InstallActive)]
    [InlineData((int)JobKind.Uninstall, (int)QueueStatus.Pending, (int)OperationVisual.UninstallWaiting)]
    [InlineData((int)JobKind.Uninstall, (int)QueueStatus.Uninstalling, (int)OperationVisual.UninstallActive)]
    [InlineData((int)JobKind.Uninstall, (int)QueueStatus.Readback, (int)OperationVisual.UninstallActive)]
    [InlineData((int)JobKind.Install, (int)QueueStatus.Done, (int)OperationVisual.Succeeded)]
    [InlineData((int)JobKind.Uninstall, (int)QueueStatus.Done, (int)OperationVisual.Succeeded)]
    [InlineData((int)JobKind.Install, (int)QueueStatus.Failed, (int)OperationVisual.Failed)]
    [InlineData((int)JobKind.Uninstall, (int)QueueStatus.Failed, (int)OperationVisual.Failed)]
    [InlineData((int)JobKind.Install, (int)QueueStatus.Paused, (int)OperationVisual.Paused)]
    [InlineData((int)JobKind.Uninstall, (int)QueueStatus.NeedsConfirm, (int)OperationVisual.Paused)]
    [InlineData((int)JobKind.Install, (int)QueueStatus.Suspended, (int)OperationVisual.Paused)]
    [InlineData((int)JobKind.Install, (int)QueueStatus.Cancelled, (int)OperationVisual.None)]
    public void 队列状态映射到统一颜色(int kind, int status, int expected)
        => Assert.Equal((OperationVisual)expected, OperationVisuals.Resolve((JobKind)kind, (QueueStatus)status));

    [Fact]
    public void 聚合优先级_失败最高_卸载中优先安装中()
    {
        Assert.Equal(OperationVisual.Failed, OperationVisuals.Max(new[]
        {
            OperationVisual.InstallWaiting, OperationVisual.Failed, OperationVisual.UninstallActive,
        }));
        Assert.Equal(OperationVisual.UninstallActive, OperationVisuals.Max(new[]
        {
            OperationVisual.InstallActive, OperationVisual.UninstallActive, OperationVisual.Succeeded,
        }));
        Assert.Equal(OperationVisual.Paused, OperationVisuals.Max(new[]
        {
            OperationVisual.InstallWaiting, OperationVisual.Paused,
        }));
    }

    [Fact]
    public void 暂停和待确认是灰色且不是进行中()
    {
        Assert.Equal(OperationVisual.Paused, OperationVisuals.Resolve(JobKind.Install, QueueStatus.Paused));
        Assert.Equal(OperationVisual.Paused, OperationVisuals.Resolve(JobKind.Install, QueueStatus.NeedsConfirm));
        Assert.Equal(OperationVisual.Paused, OperationVisuals.Resolve(JobKind.Install, QueueStatus.Suspended));
        Assert.False(OperationVisuals.IsActive(OperationVisual.Paused));
    }
}
