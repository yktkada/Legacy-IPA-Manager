namespace IdeviceIndex;

/// <summary>
/// 双击判定（直接复制自 <c>IdeviceGui\ViewModels\Library\DoubleClickState.cs</c>）：
/// 统一 400 ms，不用 WPF MouseDoubleClick——它跟系统双击时间。
/// </summary>
internal static class DoubleClickState
{
    public const int WindowMs = 400;

    /// <summary>上一次按下（同毫秒时钟）到本次按下的间隔是否在双击窗口内。</summary>
    public static bool IsSecondClick(long? lastClickMs, long nowMs)
        => lastClickMs is { } last && nowMs - last >= 0 && nowMs - last <= WindowMs;
}
