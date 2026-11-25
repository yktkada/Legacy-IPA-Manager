using IdeviceGui.ViewModels;
using IdeviceGui.ViewModels.Library;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>2026-11-04 审查 P1：资料库行重建/展开收起后的选中恢复规则。</summary>
public sealed class MainViewModelSelectionTests
{
    private static LibraryRow FileRow(long id) => new() { Id = id, BundleId = "com.x", FileName = "x.ipa" };

    private static LibraryRow GroupRow(string bundleId) =>
        new() { Id = -1, IsGroup = true, GroupBundleId = bundleId };

    [Fact]
    public void 文件行还在_按Id找回()
    {
        var keep = FileRow(7);
        var rows = new[] { GroupRow("com.g"), FileRow(7), FileRow(8) };

        Assert.Same(rows[1], MainViewModel.RestoreLibrarySelection(keep, rows, fallbackGroupBundleId: null));
    }

    [Fact]
    public void 组行_按BundleId找回()
    {
        var keep = GroupRow("com.g");
        var rows = new[] { GroupRow("com.g") };

        Assert.Same(rows[0], MainViewModel.RestoreLibrarySelection(keep, rows, fallbackGroupBundleId: null));
    }

    [Fact]
    public void 选中成员被收起_落到组行()
    {
        var keep = FileRow(7);
        var rows = new[] { GroupRow("com.g") };

        Assert.Same(rows[0], MainViewModel.RestoreLibrarySelection(keep, rows, fallbackGroupBundleId: "com.g"));
    }

    [Fact]
    public void 成员还在_优先回文件行()
    {
        var keep = FileRow(7);
        var rows = new[] { GroupRow("com.g"), FileRow(7) };

        Assert.Same(rows[1], MainViewModel.RestoreLibrarySelection(keep, rows, fallbackGroupBundleId: "com.g"));
    }

    [Fact]
    public void 无选中_返回null()
    {
        Assert.Null(MainViewModel.RestoreLibrarySelection(null, new[] { GroupRow("com.g") }, fallbackGroupBundleId: null));
    }
}
