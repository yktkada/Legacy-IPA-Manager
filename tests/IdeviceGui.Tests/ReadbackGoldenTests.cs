using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using IdeviceGui.Services;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 真机回读 golden 测试：fixture 是 2026-10-29 从 iPhone 4（iPhone3,1 / iOS 4.1，UDID 2d6f1142…）
/// 原样抓取的 `ideviceinstaller -u &lt;UDID&gt; list --xml` 完整 stdout
/// ——50 个 App，144 230 字节，UTF-8 无 BOM，CRLF 换行未改一个字节。
///
/// 手写内联 XML（<see cref="ReadbackServiceTests"/>）只能验证解析逻辑，覆盖不到真机输出的真实结构；
/// 这里用原文锁住"结构漂移"：libplist 输出格式一变、或解析器漏读 dict，这些断言就会失败。
/// 只读 fixture，不需要真机、不启子进程。
/// </summary>
public sealed class ReadbackGoldenTests
{
    private const string FixtureFileName = "iphone4-ios4.1-list.xml";

    /// <summary>真机 50 个 App 里 42 个缺 CFBundleShortVersionString 键，此 App 回退到 CFBundleVersion。</summary>
    private const string BuildOnlyBundleId = "com.chairentertainment.birthright";
    private const string BuildOnlyExpectedVersion = "7936.3";

    /// <summary>两个版本键都有但值不同：Short=1.1、Build=2816，按口径必须优先 Short。</summary>
    private const string BothVersionsBundleId = "com.popcap.ios.apac.PvZ";
    private const string BothVersionsExpectedVersion = "1.1";
    private const string BothVersionsBuildVersion = "2816";

    private const int ExpectedAppCount = 50;

    /// <summary>定位 fixtures 目录：从测试输出目录向上找（不依赖 csproj 复制规则）。</summary>
    private static string FixturesRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "fixtures");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"找不到 fixtures 目录（从 {AppContext.BaseDirectory} 向上查找）。" +
            "请确认 tests\\IdeviceGui.Tests\\fixtures 存在。");
    }

    private static string FixturePath() => Path.Combine(FixturesRoot(), "readback", FixtureFileName);

    private static string ReadFixtureXml() => File.ReadAllText(FixturePath());

    [Fact]
    public void Parse_真机iPhone4XML_App数一致()
    {
        var xml = ReadFixtureXml();
        var apps = ReadbackService.Parse(xml);

        // 期望条数不靠硬编码猜：直接数原文里的 CFBundleIdentifier 键，解析器不许漏 dict
        var expectedCount = XDocument.Parse(xml)
            .Descendants("key")
            .Count(key => key.Value == "CFBundleIdentifier");

        Assert.Equal(ExpectedAppCount, expectedCount);
        Assert.Equal(expectedCount, apps.Count);
        Assert.All(apps, app => Assert.False(string.IsNullOrWhiteSpace(app.BundleId)));
    }

    [Fact]
    public void Parse_真机iPhone4XML_ShortVersion缺失App回退Build()
    {
        var apps = ReadbackService.Parse(ReadFixtureXml());
        var app = Assert.Single(apps, a => a.BundleId == BuildOnlyBundleId);

        Assert.True(app.VersionKnown);
        Assert.Equal(BuildOnlyExpectedVersion, app.Version);
    }

    [Fact]
    public void Parse_真机iPhone4XML_ShortVersion存在App优先Short()
    {
        var apps = ReadbackService.Parse(ReadFixtureXml());
        var app = Assert.Single(apps, a => a.BundleId == BothVersionsBundleId);

        Assert.True(app.VersionKnown);
        Assert.Equal(BothVersionsExpectedVersion, app.Version);
        Assert.NotEqual(BothVersionsBuildVersion, app.Version);
    }
}
