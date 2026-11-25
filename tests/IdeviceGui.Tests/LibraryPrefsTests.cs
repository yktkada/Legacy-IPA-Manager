using IdeviceGui.ViewModels.Library;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 资料库偏好（2026-11-01）：排序持久化文本 / 行高夹紧 / 列默认方向。纯函数，不依赖 UI。
/// </summary>
public sealed class LibraryPrefsTests
{
    [Fact]
    public void 排序偏好_往返()
    {
        var sort = new LibrarySort(LibrarySortField.AppSize, Descending: true);
        Assert.Equal("AppSize:desc", LibraryPrefs.FormatSort(sort));
        Assert.Equal(sort, LibraryPrefs.ParseSort("AppSize:desc", new LibrarySort(LibrarySortField.Name, false)));

        var asc = new LibrarySort(LibrarySortField.Name, Descending: false);
        Assert.Equal("Name:asc", LibraryPrefs.FormatSort(asc));
        Assert.Equal(asc, LibraryPrefs.ParseSort("Name:asc", new LibrarySort(LibrarySortField.AppSize, true)));
    }

    [Fact]
    public void 排序偏好_坏值回默认()
    {
        var fallback = new LibrarySort(LibrarySortField.AppSize, Descending: true);

        Assert.Equal(fallback, LibraryPrefs.ParseSort(null, fallback));
        Assert.Equal(fallback, LibraryPrefs.ParseSort("", fallback));
        Assert.Equal(fallback, LibraryPrefs.ParseSort("NotAField:desc", fallback));
        Assert.Equal(fallback, LibraryPrefs.ParseSort("Name:sideways", fallback));
        Assert.Equal(fallback, LibraryPrefs.ParseSort("Name", fallback));
    }

    [Fact]
    public void 行高夹紧()
    {
        Assert.Equal(16, LibraryPrefs.ClampRowHeight(10));
        Assert.Equal(48, LibraryPrefs.ClampRowHeight(60));
        Assert.Equal(24, LibraryPrefs.ClampRowHeight(24));
        Assert.Equal(32, LibraryPrefs.ClampRowHeight(double.NaN));
        Assert.Equal(32, LibraryPrefs.ParseRowHeight("abc"));
        Assert.Equal(32, LibraryPrefs.ParseRowHeight("32"));
    }

    [Fact]
    public void 下一次排序方向_同列反向_换列用默认()
    {
        var current = new LibrarySort(LibrarySortField.AppSize, Descending: true);

        // 同列再点 → 反向
        Assert.False(LibraryPrefs.NextDescending(current, LibrarySortField.AppSize));

        // 换列 → 该列默认方向：数值列降序、文本列升序
        Assert.True(LibraryPrefs.NextDescending(current, LibrarySortField.FileSize));
        Assert.False(LibraryPrefs.NextDescending(current, LibrarySortField.Name));
    }

        [Fact]
    public void 数值列默认降序_文本列默认升序()
    {
        Assert.True(LibraryPrefs.DefaultDescending(LibrarySortField.AppSize));
        Assert.True(LibraryPrefs.DefaultDescending(LibrarySortField.FileSize));
        Assert.False(LibraryPrefs.DefaultDescending(LibrarySortField.Name));
        Assert.False(LibraryPrefs.DefaultDescending(LibrarySortField.BundleId));
    }

    [Fact]
    public void 搜索防抖偏好_默认与夹紧()
    {
        Assert.Equal(LibraryPrefs.DefaultSearchDebounceRows, LibraryPrefs.ParseSearchDebounceRows(null));
        Assert.Equal(0, LibraryPrefs.ParseSearchDebounceRows("0"));
        Assert.Equal(300, LibraryPrefs.ParseSearchDebounceRows("300"));
        Assert.Equal(LibraryPrefs.MaxSearchDebounceRows, LibraryPrefs.ParseSearchDebounceRows("99999999"));
        Assert.Equal(LibraryPrefs.DefaultSearchDebounceRows, LibraryPrefs.ParseSearchDebounceRows("abc"));

        Assert.Equal(LibraryPrefs.DefaultSearchDebounceMs, LibraryPrefs.ParseSearchDebounceMs(null));
        Assert.Equal(0, LibraryPrefs.ParseSearchDebounceMs("0"));
        Assert.Equal(50, LibraryPrefs.ParseSearchDebounceMs("50"));
        Assert.Equal(LibraryPrefs.MaxSearchDebounceMs, LibraryPrefs.ParseSearchDebounceMs("99999999"));
        Assert.Equal(LibraryPrefs.DefaultSearchDebounceMs, LibraryPrefs.ParseSearchDebounceMs("abc"));
    }

}
