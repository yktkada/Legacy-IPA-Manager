using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using IdeviceGui.Services;
using IdeviceGui.Services.Data;
using IdeviceGui.Services.Diagnostics;
using IdeviceGui.Services.Parsing;
using IdeviceGui.ViewModels;
using IdeviceGui.ViewModels.Library;
using Microsoft.Data.Sqlite;
using Xunit;
using GuiStrings = IdeviceGui.Resources.Strings;
using GuiUiLanguage = IdeviceGui.Services.UiLanguage;
using IndexStrings = IdeviceIndex.Resources.Strings;
using IndexUiLanguage = IdeviceIndex.UiLanguage;
using IndexerSettings = IdeviceIndex.IndexerSettings;

namespace IdeviceGui.Tests;

public sealed class LocalizationTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    [Theory]
    [InlineData("system", "zh-CN", "zh-CN")]
    [InlineData("system", "zh-Hans", "zh-CN")]
    [InlineData("system", "zh-TW", "zh-CN")]
    [InlineData("system", "zh", "zh-CN")]
    [InlineData("system", "en-US", "en")]
    [InlineData("system", "ja-JP", "en")]
    [InlineData("system", "fr-FR", "en")]
    [InlineData("system", "", "en")]
    [InlineData("system", null, "en")]
    [InlineData("zh-CN", "en-US", "zh-CN")]
    [InlineData("en", "zh-CN", "en")]
    [InlineData("invalid", "zh-CN", "zh-CN")]
    [InlineData("invalid", "de-DE", "en")]
    [InlineData(null, "ko-KR", "en")]
    public void 主程序语言解析_按规则映射到中文或英文(string? setting, string? systemCulture, string expected)
    {
        Assert.Equal(expected, GuiUiLanguage.Resolve(setting, systemCulture));
    }

    [Theory]
    [InlineData("\"zh-CN\"", "zh-CN")]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("EN-US", "en")]
    [InlineData("system", "system")]
    [InlineData("", "system")]
    [InlineData("fr-FR", "system")]
    public void 主程序语言设置规范化_坏值回落System(string? raw, string expected)
    {
        Assert.Equal(expected, GuiUiLanguage.NormalizeSetting(raw));
    }

    [Theory]
    [InlineData("en", "zh-CN", "zh-CN", "en")]
    [InlineData("zh-CN", "en", "en-US", "zh-CN")]
    [InlineData("system", "en", "zh-CN", "en")]
    [InlineData(null, "zh-CN", "en-US", "zh-CN")]
    [InlineData(null, "system", "zh-Hans-CN", "zh-CN")]
    [InlineData(null, "system", "ja-JP", "en")]
    [InlineData("garbage", "garbage", "unknown", "en")]
    public void 索引工具语言解析_命令行优先于配置和系统(
        string? cliLang, string? setting, string? systemCulture, string expected)
    {
        Assert.Equal(expected, IndexUiLanguage.Resolve(cliLang, setting, systemCulture));
    }

    [Fact]
    public void 主程序数据库预读语言_覆盖缺库与读写场景()
    {
        using var ws = new TestWorkspace("l10n-db");
        var missingPath = ws.Full("missing.db");
        Assert.Equal(GuiUiLanguage.SystemSetting, GuiUiLanguage.PeekSettingFromDatabase(missingPath));

        var corruptPath = ws.AddFile("corrupt.db", 128, 0x55);
        Assert.Equal(GuiUiLanguage.SystemSetting, GuiUiLanguage.PeekSettingFromDatabase(corruptPath));

        var dbPath = ws.Full("library.db");
        using (var db = AppDatabase.Open(dbPath))
        {
            Assert.Equal(GuiUiLanguage.SystemSetting, GuiUiLanguage.PeekSettingFromDatabase(dbPath));
            SettingsStore.SetString(db, GuiUiLanguage.SettingKey, "en");
        }
        SqliteConnection.ClearAllPools();
        Assert.Equal("en", GuiUiLanguage.PeekSettingFromDatabase(dbPath));

        using (var db = AppDatabase.Open(dbPath))
        {
            SettingsStore.SetString(db, GuiUiLanguage.SettingKey, "zh-CN");
        }
        SqliteConnection.ClearAllPools();
        Assert.Equal("zh-CN", GuiUiLanguage.PeekSettingFromDatabase(dbPath));
    }

    [Fact]
    public void 索引工具JSON配置_缺省与损坏回落System且保留其它设置()
    {
        using var ws = new TestWorkspace("l10n-indexer-json");
        var missing = ws.Full("indexer-missing.json");
        Assert.Equal(IndexUiLanguage.SystemSetting, IndexerSettings.LoadLanguage(missing));

        var corrupt = ws.Full("indexer-corrupt.json");
        File.WriteAllText(corrupt, "{ not valid json");
        Assert.Equal(IndexUiLanguage.SystemSetting, IndexerSettings.LoadLanguage(corrupt));

        var configPath = ws.Full("data/indexer.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, """
            {
              "Download.Connections": 8,
              "Ui.Language": "en"
            }
            """);

        Assert.Equal("en", IndexerSettings.LoadLanguage(configPath));

        IndexerSettings.SaveLanguage("zh-CN", configPath);
        Assert.Equal("zh-CN", IndexerSettings.LoadLanguage(configPath));

        var raw = File.ReadAllText(configPath);
        Assert.Contains("\"Download.Connections\": 8", raw, StringComparison.Ordinal);
        Assert.Contains("\"Ui.Language\": \"zh-CN\"", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void 主程序资源文件与强类型访问器_中英卫星程序集均可正常读取()
    {
        var root = RepoRoot();
        var resDir = Path.Combine(root, "IdeviceGui", "prototype", "IdeviceGui", "Resources");
        AssertResourceSetMatches<object>(
            resDir,
            typeof(GuiStrings),
            GuiStrings.ResourceManager,
            mustDifferKey: "Settings_Layout_Language");
    }

    [Fact]
    public void 索引工具资源文件与强类型访问器_中英卫星程序集均可正常读取()
    {
        var root = RepoRoot();
        var resDir = Path.Combine(root, "IdeviceGui", "prototype", "IdeviceIndex", "Resources");
        AssertResourceSetMatches<object>(
            resDir,
            typeof(IndexStrings),
            IndexStrings.ResourceManager,
            mustDifferKey: "App_Title");
    }

    private static void AssertResourceSetMatches<TUnused>(
        string resDir,
        Type stringsType,
        System.Resources.ResourceManager manager,
        string mustDifferKey)
    {
        var enEntries = ReadResxEntries(Path.Combine(resDir, "Strings.resx"));
        var zhEntries = ReadResxEntries(Path.Combine(resDir, "Strings.zh-CN.resx"));

        Assert.NotEmpty(enEntries);
        var enKeys = enEntries.Keys.ToHashSet(StringComparer.Ordinal);
        var zhKeys = zhEntries.Keys.ToHashSet(StringComparer.Ordinal);
        Assert.Equal(enKeys, zhKeys);

        var propNames = stringsType
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(enKeys, propNames);

        var enCulture = CultureInfo.GetCultureInfo("en");
        var zhCulture = CultureInfo.GetCultureInfo("zh-CN");
        var cjkRegex = new Regex(@"[\u4e00-\u9fff]");

        foreach (var key in enKeys)
        {
            var enRaw = enEntries[key];
            var zhRaw = zhEntries[key];
            Assert.False(string.IsNullOrWhiteSpace(enRaw), $"Missing English XML value for {key}");
            Assert.False(string.IsNullOrWhiteSpace(zhRaw), $"Missing zh-CN XML value for {key}");

            if (key != "Settings_Layout_Language_ZhCn")
                Assert.False(cjkRegex.IsMatch(enRaw), $"Unexpected CJK character in neutral Strings.resx for {key}: {enRaw}");

            Assert.Equal(ExtractPlaceholders(enRaw), ExtractPlaceholders(zhRaw));

            var enVal = manager.GetString(key, enCulture);
            var zhVal = manager.GetString(key, zhCulture);
            Assert.False(string.IsNullOrWhiteSpace(enVal), $"Missing English resource for {key}");
            Assert.False(string.IsNullOrWhiteSpace(zhVal), $"Missing zh-CN resource for {key}");
        }

        Assert.NotEqual(
            manager.GetString(mustDifferKey, enCulture),
            manager.GetString(mustDifferKey, zhCulture));
    }

    private static HashSet<string> ExtractPlaceholders(string text)
    {
        var cleaned = text.Replace("{{", "").Replace("}}", "");
        return Regex.Matches(cleaned, @"\{(\d+)(?:[^}]*)\}")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void 业务显示层_中英文切换均返回非空且不同的译文()
    {
        var enCulture = CultureInfo.GetCultureInfo("en");
        var zhCulture = CultureInfo.GetCultureInfo("zh-CN");

        static (string Status, string RunState, string Origin, string LibCol, string QueueCol, string DeviceStatus, string GroupCount) Snapshot(CultureInfo culture)
        {
            using var _ = GuiStrings.UseCultureScope(culture);
            var groupRow = new LibraryRow { Id = -1, IsGroup = true, GroupCount = 3 };
            var device = new DeviceInfo { Udid = "test-udid", Online = false };
            return (
                QueueStatus.Installing.ToText(),
                QueueRunState.Offline.ToText(),
                PackageOrigin.DisplayText(PackageOrigin.Resigned),
                LibraryColumns.Defaults.Single(c => c.Key == LibraryColumns.AppSize).Header,
                QueueColumns.Header(QueueColumn.InstalledSize),
                device.StatusText,
                groupRow.GroupText);
        }

        var en = Snapshot(enCulture);
        var zh = Snapshot(zhCulture);

        Assert.False(string.IsNullOrWhiteSpace(en.Status));
        Assert.False(string.IsNullOrWhiteSpace(zh.Status));
        Assert.NotEqual(en.Status, zh.Status);

        Assert.False(string.IsNullOrWhiteSpace(en.RunState));
        Assert.False(string.IsNullOrWhiteSpace(zh.RunState));
        Assert.NotEqual(en.RunState, zh.RunState);

        Assert.False(string.IsNullOrWhiteSpace(en.Origin));
        Assert.False(string.IsNullOrWhiteSpace(zh.Origin));
        Assert.NotEqual(en.Origin, zh.Origin);

        Assert.False(string.IsNullOrWhiteSpace(en.LibCol));
        Assert.False(string.IsNullOrWhiteSpace(zh.LibCol));
        Assert.NotEqual(en.LibCol, zh.LibCol);

        Assert.False(string.IsNullOrWhiteSpace(en.QueueCol));
        Assert.False(string.IsNullOrWhiteSpace(zh.QueueCol));
        Assert.NotEqual(en.QueueCol, zh.QueueCol);

        Assert.False(string.IsNullOrWhiteSpace(en.DeviceStatus));
        Assert.False(string.IsNullOrWhiteSpace(zh.DeviceStatus));
        Assert.NotEqual(en.DeviceStatus, zh.DeviceStatus);

        Assert.False(string.IsNullOrWhiteSpace(en.GroupCount));
        Assert.False(string.IsNullOrWhiteSpace(zh.GroupCount));
        Assert.NotEqual(en.GroupCount, zh.GroupCount);
    }

    [Fact]
    public void 业务显示层枚举与列定义_中英文均非空且译文区分()
    {
        var enCulture = CultureInfo.GetCultureInfo("en");
        var zhCulture = CultureInfo.GetCultureInfo("zh-CN");

        static T ReadInCulture<T>(CultureInfo culture, Func<T> action)
        {
            using var _ = GuiStrings.UseCultureScope(culture);
            return action();
        }

        foreach (var status in Enum.GetValues<QueueStatus>())
        {
            var en = ReadInCulture(enCulture, () => status.ToText());
            var zh = ReadInCulture(zhCulture, () => status.ToText());
            Assert.False(string.IsNullOrWhiteSpace(en), $"Empty English text for QueueStatus.{status}");
            Assert.False(string.IsNullOrWhiteSpace(zh), $"Empty zh-CN text for QueueStatus.{status}");
            Assert.NotEqual(en, zh);
        }

        foreach (var state in Enum.GetValues<QueueRunState>())
        {
            var en = ReadInCulture(enCulture, () => state.ToText());
            var zh = ReadInCulture(zhCulture, () => state.ToText());
            Assert.False(string.IsNullOrWhiteSpace(en), $"Empty English text for QueueRunState.{state}");
            Assert.False(string.IsNullOrWhiteSpace(zh), $"Empty zh-CN text for QueueRunState.{state}");
            Assert.NotEqual(en, zh);
        }

        foreach (var origin in new[] { PackageOrigin.Original, PackageOrigin.Resigned, PackageOrigin.Decrypted, PackageOrigin.Mixed })
        {
            var en = ReadInCulture(enCulture, () => PackageOrigin.DisplayText(origin));
            var zh = ReadInCulture(zhCulture, () => PackageOrigin.DisplayText(origin));
            Assert.False(string.IsNullOrWhiteSpace(en), $"Empty English text for PackageOrigin.{origin}");
            Assert.False(string.IsNullOrWhiteSpace(zh), $"Empty zh-CN text for PackageOrigin.{origin}");
            Assert.NotEqual(en, zh);
        }

        var invariantLibraryCols = new HashSet<string>(StringComparer.Ordinal)
        {
            LibraryColumns.Installed,
            LibraryColumns.MinOs,
            LibraryColumns.BundleId,
        };
        foreach (var col in LibraryColumns.Defaults)
        {
            var en = ReadInCulture(enCulture, () => col.Header);
            var zh = ReadInCulture(zhCulture, () => col.Header);
            Assert.False(string.IsNullOrWhiteSpace(en), $"Empty English header for LibraryColumn.{col.Key}");
            Assert.False(string.IsNullOrWhiteSpace(zh), $"Empty zh-CN header for LibraryColumn.{col.Key}");
            if (!invariantLibraryCols.Contains(col.Key))
                Assert.NotEqual(en, zh);
        }

        foreach (var col in QueueColumns.Default)
        {
            var en = ReadInCulture(enCulture, () => QueueColumns.Header(col.Column));
            var zh = ReadInCulture(zhCulture, () => QueueColumns.Header(col.Column));
            Assert.False(string.IsNullOrWhiteSpace(en), $"Empty English header for QueueColumn.{col.Column}");
            Assert.False(string.IsNullOrWhiteSpace(zh), $"Empty zh-CN header for QueueColumn.{col.Column}");
            if (col.Column != QueueColumn.BundleId)
                Assert.NotEqual(en, zh);
        }
    }

    private static Dictionary<string, string> ReadResxEntries(string resxPath)
    {
        var doc = XDocument.Load(resxPath);
        return doc.Root!
            .Elements("data")
            .Select(e => (
                Name: e.Attribute("name")?.Value ?? "",
                Value: e.Element("value")?.Value ?? ""))
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);
    }
}
