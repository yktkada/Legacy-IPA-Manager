using IdeviceGui.Services;
using IdeviceGui.ViewModels.Library;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// ✓ 判定（2026-11-02 用户定）：**必须 BundleId + 版本精确匹配**，不得只比 BundleId；
/// 任一侧版本未知 → 不算已装（宁可少勾，不可假勾）。
/// </summary>
public sealed class InstalledMatcherTests
{
    private static InstalledApp App(string bundleId, string version, bool known = true)
        => new(bundleId, version, known);

    [Fact]
    public void 同BundleId同版本_算已装()
        => Assert.True(InstalledMatcher.Matches(new[] { App("com.a", "1.2") }, "com.a", "1.2"));

    [Fact]
    public void 同BundleId但版本不同_不算已装()
        => Assert.False(InstalledMatcher.Matches(new[] { App("com.a", "1.2") }, "com.a", "1.3"));

    [Fact]
    public void 设备版本未知_不算已装()
        => Assert.False(InstalledMatcher.Matches(new[] { App("com.a", "1.2", known: false) }, "com.a", "1.2"));

    [Fact]
    public void 包版本未知_不算已装()
    {
        Assert.False(InstalledMatcher.Matches(new[] { App("com.a", "1.2") }, "com.a", null));
        Assert.False(InstalledMatcher.Matches(new[] { App("com.a", "1.2") }, "com.a", "  "));
    }

    [Fact]
    public void 设备上没有这个BundleId_不算已装()
        => Assert.False(InstalledMatcher.Matches(new[] { App("com.b", "1.2") }, "com.a", "1.2"));

    [Fact]
    public void 未回读_不算已装()
        => Assert.False(InstalledMatcher.Matches(null, "com.a", "1.2"));

    [Fact]
    public void BundleId大小写不敏感_版本比较忽略首尾空白()
        => Assert.True(InstalledMatcher.Matches(new[] { App("COM.A", "1.2") }, "com.a", " 1.2 "));
    [Fact]
    public void 三态_同版本为SameVersion()
        => Assert.Equal(InstalledMatchKind.SameVersion,
            InstalledMatcher.Classify(new[] { App("com.a", "1.2") }, "com.a", "1.2"));

    [Fact]
    public void 三态_不同版本为OtherVersion()
        => Assert.Equal(InstalledMatchKind.OtherVersion,
            InstalledMatcher.Classify(new[] { App("com.a", "1.2") }, "com.a", "1.3"));

    [Fact]
    public void 三态_版本未知为OtherVersion()
    {
        Assert.Equal(InstalledMatchKind.OtherVersion,
            InstalledMatcher.Classify(new[] { App("com.a", "1.2", known: false) }, "com.a", "1.2"));
        Assert.Equal(InstalledMatchKind.OtherVersion,
            InstalledMatcher.Classify(new[] { App("com.a", "1.2") }, "com.a", null));
    }

    [Fact]
    public void 名称加BundleId加版本_名称区分大小写()
    {
        var apps = new[] { new InstalledApp("com.a", "1.2", true, "Game") };
        Assert.True(InstalledMatcher.Matches(apps, "com.a", "1.2", "Game"));
        Assert.True(InstalledMatcher.Matches(apps, "com.a", "1.2", " Game "));
        Assert.False(InstalledMatcher.Matches(apps, "com.a", "1.2", "game"));
        Assert.False(InstalledMatcher.Matches(apps, "com.a", "1.2", "Other"));
    }

    [Fact]
    public void 名称不同_不算已安装_同版本也不同()
    {
        var apps = new[] { new InstalledApp("com.a", "1.2", true, "Game") };
        Assert.Equal(InstalledMatchKind.None, InstalledMatcher.Classify(apps, "com.a", "1.2", "Other"));
    }

    [Fact]
    public void 名称相同_版本不同仍为OtherVersion()
    {
        var apps = new[] { new InstalledApp("com.a", "1.2", true, "Game") };
        Assert.Equal(InstalledMatchKind.OtherVersion, InstalledMatcher.Classify(apps, "com.a", "1.3", "Game"));
    }

    [Fact]
    public void 三态_没有同BundleId为None()
        => Assert.Equal(InstalledMatchKind.None,
            InstalledMatcher.Classify(new[] { App("com.b", "1.2") }, "com.a", "1.2"));

}
