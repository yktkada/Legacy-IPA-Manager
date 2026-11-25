using IdeviceGui.ViewModels;
using IdeviceGui.ViewModels.Library;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 文件路径列显示（2026-11-02 用户定，docs/06 §6.16）：
/// 属于扫描根 → `RootN\子路径`；不属于任何根 → **完整路径**；动作（复制/打开）仍用完整路径。
/// </summary>
public sealed class LibraryRowPathTests
{
    private static LibraryRow Row(string filePath, long rootId = 0, string rootPath = "", string rootLabel = "")
        => new()
        {
            Id = 1,
            FilePath = filePath,
            FileName = System.IO.Path.GetFileName(filePath),
            ScanRootId = rootId,
            RootPath = rootPath,
            RootLabel = rootLabel,
        };

    [Fact]
    public void 没有根_显示完整路径()
    {
        var row = Row(@"E:\games\a.ipa");
        Assert.Equal(@"E:\games\a.ipa", row.FilePathText);
    }

    [Fact]
    public void 属于根_显示Root加子路径()
    {
        var row = Row(@"E:\games\sub\a.ipa", rootId: 7, rootPath: @"E:\games", rootLabel: "Root2");
        Assert.Equal(@"Root2\sub\a.ipa", row.FilePathText);
    }

    [Fact]
    public void 根下的直接子文件_也带Root前缀()
    {
        var row = Row(@"E:\games\a.ipa", rootId: 7, rootPath: @"E:\games", rootLabel: "Root1");
        Assert.Equal(@"Root1\a.ipa", row.FilePathText);
    }

    [Fact]
    public void 路径与根前缀不匹配_回退完整路径()
    {
        var row = Row(@"D:\other\a.ipa", rootId: 7, rootPath: @"E:\games", rootLabel: "Root2");
        Assert.Equal(@"D:\other\a.ipa", row.FilePathText);
    }

    [Fact]
    public void 大小写不敏感匹配_大小写按原路径保留()
    {
        var row = Row(@"E:\Games\sub\A.ipa", rootId: 7, rootPath: @"e:\games", rootLabel: "Root3");
        Assert.Equal(@"Root3\sub\A.ipa", row.FilePathText);
    }

    [Fact]
    public void 空路径_空文案()
    {
        var row = Row("", rootId: 7, rootPath: @"E:\games", rootLabel: "Root1");
        Assert.Equal("", row.FilePathText);
    }

    [Fact]
    public void 动作仍用完整路径_显示属性变不影响FilePath()
    {
        var row = Row(@"E:\games\sub\a.ipa", rootId: 7, rootPath: @"E:\games", rootLabel: "Root2");
        Assert.Equal(@"E:\games\sub\a.ipa", row.FilePath);
        Assert.NotEqual(row.FilePath, row.FilePathText);
    }
}
