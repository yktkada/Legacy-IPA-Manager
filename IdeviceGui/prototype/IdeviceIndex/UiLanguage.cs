using System.Globalization;

namespace IdeviceIndex;

/// <summary>
/// 索引工具语言解析（docs/11 §2 / docs/12 §5.4）：
/// 优先级：命令行 `--lang` &gt; `data\indexer.json` 的 `Ui.Language` &gt; 系统语言（zh* → zh-CN，其余一律 en）。
/// </summary>
internal static class UiLanguage
{
    public const string SettingKey = "Ui.Language";
    public const string SystemSetting = "system";
    public const string Chinese = "zh-CN";
    public const string English = "en";

    public static string CurrentLanguage { get; private set; } =
        Resolve(null, SystemSetting, CultureInfo.CurrentUICulture.Name);

    public static bool IsCurrentChinese => string.Equals(CurrentLanguage, Chinese, StringComparison.OrdinalIgnoreCase);

    public static bool IsCurrentEnglish => string.Equals(CurrentLanguage, English, StringComparison.OrdinalIgnoreCase);

    public static string NormalizeSetting(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return SystemSetting;
        var trimmed = raw.Trim().Trim('"');
        if (string.Equals(trimmed, SystemSetting, StringComparison.OrdinalIgnoreCase))
            return SystemSetting;
        if (IsChineseTag(trimmed)) return Chinese;
        if (IsEnglishTag(trimmed)) return English;
        return SystemSetting;
    }

    public static string Resolve(string? cliLang, string? setting, string? systemCultureName)
    {
        var cliNormalized = NormalizeSetting(cliLang);
        if (string.Equals(cliNormalized, Chinese, StringComparison.Ordinal)) return Chinese;
        if (string.Equals(cliNormalized, English, StringComparison.Ordinal)) return English;

        var settingNormalized = NormalizeSetting(setting);
        if (string.Equals(settingNormalized, Chinese, StringComparison.Ordinal)) return Chinese;
        if (string.Equals(settingNormalized, English, StringComparison.Ordinal)) return English;

        var system = systemCultureName?.Trim();
        return IsChineseTag(system) ? Chinese : English;
    }

    public static string Apply(string? cliLang, string? setting, string? systemCultureName = null)
    {
        var resolved = Resolve(cliLang, setting, systemCultureName ?? CultureInfo.CurrentUICulture.Name);
        var culture = CultureInfo.GetCultureInfo(resolved);
        CurrentLanguage = resolved;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        IdeviceIndex.Resources.Strings.Culture = culture;
        return resolved;
    }

    private static bool IsChineseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return false;
        return string.Equals(tag, "zh", StringComparison.OrdinalIgnoreCase)
               || tag.StartsWith("zh-", StringComparison.OrdinalIgnoreCase)
               || tag.StartsWith("zh_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEnglishTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return false;
        return string.Equals(tag, "en", StringComparison.OrdinalIgnoreCase)
               || tag.StartsWith("en-", StringComparison.OrdinalIgnoreCase)
               || tag.StartsWith("en_", StringComparison.OrdinalIgnoreCase);
    }
}
