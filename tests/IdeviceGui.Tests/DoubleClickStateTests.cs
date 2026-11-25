using IdeviceGui.ViewModels.Library;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>双击判定（2026-11-01）：固定 400 ms 窗口，不用系统双击时间。</summary>
public sealed class DoubleClickStateTests
{
    [Fact]
    public void 四百毫秒内第二次按下_算双击()
    {
        Assert.True(DoubleClickState.IsSecondClick(1_000, 1_000));
        Assert.True(DoubleClickState.IsSecondClick(1_000, 1_200));
        Assert.True(DoubleClickState.IsSecondClick(1_000, 1_400));   // 恰好 400 ms 算
    }

    [Fact]
    public void 超过四百毫秒_不算双击()
    {
        Assert.False(DoubleClickState.IsSecondClick(1_000, 1_401));
        Assert.False(DoubleClickState.IsSecondClick(1_000, 5_000));
    }

    [Fact]
    public void 没有上一次按下_不算双击()
    {
        Assert.False(DoubleClickState.IsSecondClick(null, 1_000));
    }

    [Fact]
    public void 时钟倒退_不算双击()
    {
        Assert.False(DoubleClickState.IsSecondClick(1_000, 900));
    }
}
