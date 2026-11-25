namespace IdeviceIndex;

/// <summary>
/// 多选行契约（直接复制自 <c>IdeviceGui\ViewModels\MultiSelectRange.cs</c>）。
/// </summary>
internal interface IMultiSelectRow
{
    bool IsMultiSelected { get; set; }
}

/// <summary>
/// 拖动范围选择的唯一实现（直接复制自 <c>IdeviceGui\ViewModels\MultiSelectRange.cs</c>）：
/// 从 anchor 到 current 的行相对拖动前状态切换（Explorer 风格）。
/// </summary>
internal static class MultiSelectRange
{
    public static bool Apply<T>(
        IReadOnlyList<T> rows,
        T anchor,
        T current,
        IReadOnlySet<T> baseSelection,
        Func<T, bool> skip) where T : class, IMultiSelectRow
    {
        var first = -1;
        var last = -1;
        for (var i = 0; i < rows.Count; i++)
        {
            if (ReferenceEquals(rows[i], anchor)) first = i;
            if (ReferenceEquals(rows[i], current)) last = i;
        }
        if (first < 0 || last < 0) return false;
        if (first > last) (first, last) = (last, first);

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (skip(row)) continue;
            var inRange = i >= first && i <= last;
            row.IsMultiSelected = baseSelection.Contains(row) ^ inRange;
        }
        return true;
    }
}
