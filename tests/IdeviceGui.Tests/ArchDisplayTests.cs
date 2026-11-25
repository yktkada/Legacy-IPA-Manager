using IdeviceGui.ViewModels.Library;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 架构列显示（2026-11-02 用户定）：32 位去 `arm`（v6/v7/v7s/v7f）、`arm64` 保留，固定顺序 v6 → v7 → v7f → v7s → arm64 → 未知，去重。
/// </summary>
public sealed class ArchDisplayTests
{
    [Theory]
    [InlineData("armv6,armv7", "v6, v7")]
    [InlineData("armv7,armv6", "v6, v7")]                 // 顺序被规范化
    [InlineData("armv7s,armv6", "v6, v7s")]
    [InlineData("armv6,armv7s,arm64", "v6, v7s, arm64")]
    [InlineData("arm64,armv7", "v7, arm64")]
    [InlineData("arm64", "arm64")]
    [InlineData("armv6", "v6")]
    [InlineData("armv7f", "v7f")]
    [InlineData("armv7,armv7", "v7")]                     // 去重
    [InlineData("armv7,arm64,armv7s", "v7, v7s, arm64")]
    [InlineData("x86_64,arm64", "arm64, x86_64")]         // 未知排最后
    [InlineData("", "")]
    public void 架构显示_规范化顺序与去arm前缀(string raw, string expected)
        => Assert.Equal(expected, LibraryRow.FormatArch(raw));

    [Fact]
    public void 架构为空或null_显示空串()
    {
        Assert.Equal("", LibraryRow.FormatArch(null));
        Assert.Equal("", LibraryRow.FormatArch("   "));
    }

    [Fact]
    public void 架构列用ArchText_搜索v7也能命中()
    {
        var view = new LibraryView();
        var row = new LibraryRow { Id = 1, FilePath = @"C:\tmp\x.ipa", FileName = "x.ipa" };
        row.UpdateFrom(new IdeviceGui.Services.Data.PackageRow
        {
            Id = 1,
            FilePath = @"C:\tmp\x.ipa",
            FileName = "x.ipa",
            Arch = "armv7,arm64",
            ParseState = IdeviceGui.Services.Data.ParseStates.Ok,
            ExistsFlag = true,
        });
        view.ReplaceAll(new[] { new IdeviceGui.Services.Data.PackageRow
        {
            Id = 1,
            FilePath = @"C:\tmp\x.ipa",
            FileName = "x.ipa",
            Arch = "armv7,arm64",
            ParseState = IdeviceGui.Services.Data.ParseStates.Ok,
            ExistsFlag = true,
        } });

        Assert.Equal("v7, arm64", row.ArchText);
    }
}
