using System.Globalization;
using IdeviceIndex;
using Xunit;
using IndexMainWindow = IdeviceIndex.MainWindow;
using IndexStrings = IdeviceIndex.Resources.Strings;

namespace IdeviceGui.Tests;

public sealed class IdeviceIndexLocalizationTests
{
    [Fact]
    public void ChooseDescription_ChinesePriority_PrefersZhHansThenEnThenUnd()
    {
        var list = new List<DescriptionRow>
        {
            new("und", "und text"),
            new("en", "en text"),
            new("zh-Hans", "zh text"),
        };

        Assert.Equal("zh text", IndexMainWindow.ChooseDescription(list, preferChinese: true));

        var noZh = new List<DescriptionRow>
        {
            new("und", "und text"),
            new("en", "en text"),
        };
        Assert.Equal("en text", IndexMainWindow.ChooseDescription(noZh, preferChinese: true));

        var onlyUnd = new List<DescriptionRow>
        {
            new("fr", "fr text"),
            new("und", "und text"),
        };
        Assert.Equal("und text", IndexMainWindow.ChooseDescription(onlyUnd, preferChinese: true));
    }

    [Fact]
    public void ChooseDescription_EnglishPriority_PrefersEnThenUndThenZhHans()
    {
        var list = new List<DescriptionRow>
        {
            new("zh-Hans", "zh text"),
            new("und", "und text"),
            new("en", "en text"),
        };

        Assert.Equal("en text", IndexMainWindow.ChooseDescription(list, preferChinese: false));

        var noEn = new List<DescriptionRow>
        {
            new("zh-Hans", "zh text"),
            new("und", "und text"),
        };
        Assert.Equal("und text", IndexMainWindow.ChooseDescription(noEn, preferChinese: false));

        var onlyZh = new List<DescriptionRow>
        {
            new("fr", "fr text"),
            new("zh-Hans", "zh text"),
        };
        Assert.Equal("zh text", IndexMainWindow.ChooseDescription(onlyZh, preferChinese: false));
    }

    [Fact]
    public void ChooseDescription_FallbackToFirst_WhenNoPreferredLanguageMatches()
    {
        var list = new List<DescriptionRow>
        {
            new("ja", "ja text"),
            new("fr", "fr text"),
        };

        Assert.Equal("ja text", IndexMainWindow.ChooseDescription(list, preferChinese: true));
        Assert.Equal("ja text", IndexMainWindow.ChooseDescription(list, preferChinese: false));
    }

    [Fact]
    public void ChooseDescription_EmptyList_ReturnsLocalizedNoDescription()
    {
        var empty = Array.Empty<DescriptionRow>();

        var resultZh = IndexMainWindow.ChooseDescription(empty, preferChinese: true);
        var resultEn = IndexMainWindow.ChooseDescription(empty, preferChinese: false);

        Assert.False(string.IsNullOrWhiteSpace(resultZh));
        Assert.Equal(IndexStrings.Detail_NoDescription, resultZh);
        Assert.Equal(IndexStrings.Detail_NoDescription, resultEn);
    }

    [Fact]
    public void CommonUnknown_ResourceLookup_MatchesEnglishAndChinese()
    {
        var en = IndexStrings.ResourceManager.GetString("Common_Unknown", CultureInfo.GetCultureInfo("en"));
        var zh = IndexStrings.ResourceManager.GetString("Common_Unknown", CultureInfo.GetCultureInfo("zh-CN"));

        Assert.Equal("Unknown", en);
        Assert.Equal("未知", zh);
    }

    [Fact]
    public void IndexRow_EmptyFields_UseLocalizedUnknown_AndKeepDeviceNames()
    {
        var emptyRow = new IndexRow { Id = "1", BundleId = "com.example.app" };

        Assert.Equal(IndexStrings.Common_Unknown, emptyRow.MinOsText);
        Assert.Equal(IndexStrings.Common_Unknown, emptyRow.DeviceFamilyText);
        Assert.Equal(IndexStrings.Common_Unknown, emptyRow.SizeText);

        var fullRow = new IndexRow
        {
            Id = "2",
            BundleId = "com.example.all",
            MinOs = "6.1.3",
            DeviceMask = 2 | 4 | 8,
            Size = 2048,
            SizeExact = true,
        };

        Assert.Equal("6.1.3", fullRow.MinOsText);
        Assert.Equal("iPhone / iPad / AppleTV", fullRow.DeviceFamilyText);
        Assert.Equal("2 KB", fullRow.SizeText);
    }
}
