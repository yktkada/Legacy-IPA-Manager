using IdeviceGui.Services.Data;
using IdeviceGui.ViewModels.Library;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 资料库右键复制（2026-11-01 用户要求）：取值逻辑纯函数；组行用 GroupBundleId、文件行用各自字段。
/// </summary>
public sealed class LibraryCopyTests
{
    [Fact]
    public void 文件行_各字段按行取值()
    {
        var row = FileRow("MemoryInfo", "com.example.memoryinfo");

        Assert.Equal("com.example.memoryinfo", LibraryCopy.BundleId(row));
        Assert.Equal("MemoryInfo", LibraryCopy.Name(row));
        Assert.Equal("1.1", LibraryCopy.Version(row));
        Assert.Equal("2.0", LibraryCopy.MinOs(row));
        Assert.Equal("MemoryInfo 1.1.ipa", LibraryCopy.FileName(row));
        Assert.Equal(@"C:\fake\MemoryInfo 1.1.ipa", LibraryCopy.FilePath(row));
    }

    [Fact]
    public void 文件行_整行Tab分隔_含10段且首段是名称()
    {
        var row = FileRow("MemoryInfo", "com.example.memoryinfo");
        var parts = LibraryCopy.Row(row).Split('\t');

        Assert.Equal(10, parts.Length);
        Assert.Equal("MemoryInfo", parts[0]);
        Assert.Equal("com.example.memoryinfo", parts[1]);
        Assert.Equal(@"C:\fake\MemoryInfo 1.1.ipa", parts[^1]);
    }

    [Fact]
    public void 组行_BundleId取GroupBundleId_文件名与路径为空()
    {
        var row = new LibraryRow
        {
            Id = -1,
            IsGroup = true,
            GroupBundleId = "com.rockstargames.gta3sa",
            Name = "圣安地列斯汉化",
            GroupCount = 3,
        };

        Assert.Equal("com.rockstargames.gta3sa", LibraryCopy.BundleId(row));
        Assert.Contains("圣安地列斯汉化", LibraryCopy.Name(row)!);
        Assert.Null(LibraryCopy.FileName(row));
        Assert.Null(LibraryCopy.FilePath(row));
    }

    [Fact]
    public void 空值给null_不把空串写剪贴板()
    {
        var row = FileRow("无BundleId的包", bundleId: "");

        Assert.Null(LibraryCopy.BundleId(row));
        Assert.NotNull(LibraryCopy.FilePath(row));   // 路径本身非空，只是 BundleId 缺失
    }

    [Fact]
    public void null行_安全返回()
    {
        Assert.Null(LibraryCopy.BundleId(null));
        Assert.Null(LibraryCopy.Name(null));
        Assert.Equal("", LibraryCopy.Row(null));
    }

    private static LibraryRow FileRow(string name, string bundleId) => new()
    {
        Id = 7,
        FilePath = $@"C:\fake\{name} 1.1.ipa",
        FileName = name + " 1.1.ipa",
        Name = name,
        BundleId = bundleId,
        Version = "1.1",
        Build = "1",
        MinOs = "2.0",
        DeviceFamily = "iPhone",
        Arch = "armv6",
        AppSize = 140_621,
        FileSize = 109_116,
        ParseState = ParseStates.Ok,
    };
}
