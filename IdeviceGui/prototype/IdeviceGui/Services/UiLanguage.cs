using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace IdeviceGui.Services;

/// <summary>
/// 界面语言解析与设置预读（docs/11 §2）：
/// 默认跟随系统；命中 zh* 用 zh-CN，其余或未知一律回落 en。
/// </summary>
internal static class UiLanguage
{
    public const string SettingKey = "Ui.Language";
    public const string SystemSetting = "system";
    public const string Chinese = "zh-CN";
    public const string English = "en";

    public static string CurrentLanguage { get; private set; } =
        Resolve(SystemSetting, CultureInfo.CurrentUICulture.Name);

    public static bool IsCurrentEnglish => IsEnglish(CurrentLanguage);

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

    public static string Resolve(string? setting, string? systemCultureName)
    {
        var normalized = NormalizeSetting(setting);
        if (string.Equals(normalized, Chinese, StringComparison.Ordinal)) return Chinese;
        if (string.Equals(normalized, English, StringComparison.Ordinal)) return English;

        var system = systemCultureName?.Trim();
        return IsChineseTag(system) ? Chinese : English;
    }

    public static bool IsEnglish(string? resolved)
        => string.Equals(resolved, English, StringComparison.OrdinalIgnoreCase);

    public static string Apply(string? setting, string? systemCultureName = null)
    {
        var resolved = Resolve(setting, systemCultureName ?? CultureInfo.CurrentUICulture.Name);
        var culture = CultureInfo.GetCultureInfo(resolved);
        CurrentLanguage = resolved;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        IdeviceGui.Resources.Strings.Culture = culture;
        return resolved;
    }

    /// <summary>
    /// 启动期建窗前以只读短连接预读 `Ui.Language`；库不存在、未建表或损坏时静默回落 `system`。
    /// </summary>
    public static string PeekSettingFromDatabase(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
            return SystemSetting;

        try
        {
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString();
            using var conn = new SqliteConnection(connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Value FROM Settings WHERE Key = $key;";
            cmd.Parameters.AddWithValue("$key", SettingKey);
            return NormalizeSetting(cmd.ExecuteScalar() as string);
        }
        catch
        {
            return SystemSetting;
        }
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
