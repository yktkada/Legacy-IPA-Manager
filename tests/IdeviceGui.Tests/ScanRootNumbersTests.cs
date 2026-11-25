using IdeviceGui.Services.Data;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 扫描根显示编号（2026-11-02 用户定）：`RootN`，**不重排**、删除后下次新增**补最小空号**，
/// 编号存 `Settings.Scan.RootNumbers`（docs/03 §3.x.1）。
/// </summary>
public sealed class ScanRootNumbersTests
{
    [Fact]
    public void Parse_正常映射()
    {
        var map = ScanRootNumbers.Parse("""{"D:\\A":1,"E:\\B":3}""");

        Assert.Equal(2, map.Count);
        Assert.Equal(1, map[@"d:\a"]);
        Assert.Equal(3, map[@"e:\b"]);
    }

    [Fact]
    public void Parse_坏JSON或非对象_回空表()
    {
        Assert.Empty(ScanRootNumbers.Parse(null));
        Assert.Empty(ScanRootNumbers.Parse(""));
        Assert.Empty(ScanRootNumbers.Parse("{bad json"));
        Assert.Empty(ScanRootNumbers.Parse("[1,2,3]"));
    }

    [Fact]
    public void Parse_非法编号丢弃()
    {
        var map = ScanRootNumbers.Parse("""{"D:\\A":0,"D:\\B":-2,"D:\\C":"x","D:\\D":2}""");

        Assert.Single(map);
        Assert.Equal(2, map[@"d:\d"]);
    }

    [Fact]
    public void NextFree_从1起_跳过已用()
    {
        Assert.Equal(1, ScanRootNumbers.NextFree(Array.Empty<int>()));
        Assert.Equal(2, ScanRootNumbers.NextFree(new[] { 1, 3 }));
        Assert.Equal(4, ScanRootNumbers.NextFree(new[] { 1, 2, 3 }));
    }

    [Fact]
    public void Label_是Root加编号()
    {
        Assert.Equal("Root1", ScanRootNumbers.Label(1));
        Assert.Equal("Root12", ScanRootNumbers.Label(12));
    }

    [Fact]
    public void Format_往返无损()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [@"d:\a"] = 1, [@"e:\b"] = 2 };
        var roundTrip = ScanRootNumbers.Parse(ScanRootNumbers.Format(map));

        Assert.Equal(2, roundTrip.Count);
        Assert.Equal(1, roundTrip[@"d:\a"]);
        Assert.Equal(2, roundTrip[@"e:\b"]);
    }
}
