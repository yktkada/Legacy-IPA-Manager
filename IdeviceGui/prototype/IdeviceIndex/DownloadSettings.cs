using System.Globalization;
using System.IO;

namespace IdeviceIndex;

/// <summary>
/// 下载配置（覆盖 docs/12 §3.4.1 全部设置项与范围回落）。
/// </summary>
internal sealed record DownloadSettings
{
    public const string DirKey = "Download.Dir";
    public const string RememberLastDirKey = "Download.RememberLastDir";
    public const string ConnectionsKey = "Download.Connections";
    public const string SplitsKey = "Download.Splits";
    public const string MinSplitSizeKey = "Download.MinSplitSize";
    public const string ConcurrencyKey = "Download.Concurrency";
    public const string SpeedLimitKey = "Download.SpeedLimit";
    public const string PerTaskSpeedLimitKey = "Download.PerTaskSpeedLimit";
    public const string RetryTimesKey = "Download.RetryTimes";
    public const string RetryWaitKey = "Download.RetryWait";
    public const string TimeoutKey = "Download.Timeout";
    public const string ContinueKey = "Download.Continue";
    public const string ProxyModeKey = "Download.ProxyMode";
    public const string ProxyUrlKey = "Download.ProxyUrl";
    public const string ProxyBypassKey = "Download.ProxyBypass";
    public const string UserAgentKey = "Download.UserAgent";
    public const string SessionDirModeKey = "Download.SessionDirMode";
    public const string SessionDirCustomKey = "Download.SessionDirCustom";
    public const string ControlFileModeKey = "Download.ControlFileMode";
    public const string KeepPartFilesKey = "Download.KeepPartFiles";
    public const string PartTtlHoursKey = "Download.PartTtlHours";
    public const string SpaceMarginMbKey = "Download.SpaceMarginMB";
    public const string AutoImportKey = "Download.AutoImport";
    public const string AutoRefreshFallbackKey = "Download.AutoRefreshFallback";
    public const string OpenFolderWhenDoneKey = "Download.OpenFolderWhenDone";
    public const string MaxFileSizeMbKey = "Download.MaxFileSizeMB";
    public const string AllowOutsideProgramDirWriteKey = "Security.AllowOutsideProgramDirWrite";

    public const string ProxySystem = "system";
    public const string ProxyManual = "manual";
    public const string ProxyNone = "none";

    public const string SessionProgram = "program";
    public const string SessionTarget = "target";
    public const string SessionCustom = "custom";

    public const string ControlSameAsPart = "same-as-part";
    public const string ControlSession = "session";

    public string Dir { get; init; } = DefaultDir(AppContext.BaseDirectory);
    public bool RememberLastDir { get; init; } = true;
    public int Connections { get; init; } = 8;
    public int Splits { get; init; } = 8;
    public string MinSplitSize { get; init; } = "4M";
    public int Concurrency { get; init; } = 4;
    public string SpeedLimit { get; init; } = "0";
    public string PerTaskSpeedLimit { get; init; } = "0";
    public int RetryTimes { get; init; } = 3;
    public int RetryWait { get; init; } = 10;
    public int Timeout { get; init; } = 60;
    public bool Continue { get; init; } = true;
    public string ProxyMode { get; init; } = ProxySystem;
    public string ProxyUrl { get; init; } = "";
    public string ProxyBypass { get; init; } = "<local>";
    public string UserAgent { get; init; } = "LegacyIPAManager/1.0";
    public string SessionDirMode { get; init; } = SessionProgram;
    public string SessionDirCustom { get; init; } = "";
    public string ControlFileMode { get; init; } = ControlSameAsPart;
    public bool KeepPartFiles { get; init; } = true;
    public int PartTtlHours { get; init; } = 24;
    public int SpaceMarginMB { get; init; } = 50;
    public bool AutoImport { get; init; } = true;
    public bool AutoRefreshFallback { get; init; } = true;
    public bool OpenFolderWhenDone { get; init; }
    public int MaxFileSizeMB { get; init; }

    public static string DefaultDir(string? programDir = null)
        => Path.Combine(Path.GetFullPath(programDir ?? AppContext.BaseDirectory), "library");

    public static DownloadSettings CreateDefault(string? programDir = null)
        => new() { Dir = DefaultDir(programDir) };

    public DownloadSettings Normalize(string? programDir = null)
    {
        var fallbackDir = DefaultDir(programDir);
        var normalizedDir = NormalizeDir(Dir, fallbackDir);

        return new DownloadSettings
        {
            Dir = normalizedDir,
            RememberLastDir = RememberLastDir,
            Connections = FallbackInt(Connections, 1, 16, 8),
            Splits = FallbackInt(Splits, 1, 16, 8),
            MinSplitSize = NormalizeSize(MinSplitSize, 1L * 1024 * 1024, 64L * 1024 * 1024, "4M", allowZero: false),
            Concurrency = FallbackInt(Concurrency, 1, 8, 4),
            SpeedLimit = NormalizeSize(SpeedLimit, 64L * 1024, 100L * 1024 * 1024, "0", allowZero: true),
            PerTaskSpeedLimit = NormalizeSize(PerTaskSpeedLimit, 64L * 1024, 100L * 1024 * 1024, "0", allowZero: true),
            RetryTimes = FallbackInt(RetryTimes, 0, 10, 3),
            RetryWait = FallbackInt(RetryWait, 1, 120, 10),
            Timeout = FallbackInt(Timeout, 10, 600, 60),
            Continue = Continue,
            ProxyMode = NormalizeEnum(ProxyMode, ProxySystem, ProxySystem, ProxyManual, ProxyNone),
            ProxyUrl = (ProxyUrl ?? "").Trim(),
            ProxyBypass = ProxyBypass is null ? "<local>" : ProxyBypass.Trim(),
            UserAgent = string.IsNullOrWhiteSpace(UserAgent) ? "LegacyIPAManager/1.0" : UserAgent.Trim(),
            SessionDirMode = NormalizeEnum(SessionDirMode, SessionProgram, SessionProgram, SessionTarget, SessionCustom),
            SessionDirCustom = (SessionDirCustom ?? "").Trim(),
            ControlFileMode = NormalizeEnum(ControlFileMode, ControlSameAsPart, ControlSameAsPart, ControlSession),
            KeepPartFiles = KeepPartFiles,
            PartTtlHours = FallbackInt(PartTtlHours, 1, 720, 24),
            SpaceMarginMB = FallbackInt(SpaceMarginMB, 0, 1024, 50),
            AutoImport = AutoImport,
            AutoRefreshFallback = AutoRefreshFallback,
            OpenFolderWhenDone = OpenFolderWhenDone,
            MaxFileSizeMB = MaxFileSizeMB < 0 ? 0 : MaxFileSizeMB,
        };
    }

    internal static int FallbackInt(int value, int min, int max, int fallback)
        => value >= min && value <= max ? value : fallback;

    internal static string NormalizeDir(string? raw, string fallbackDir)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallbackDir;
        try
        {
            return Path.GetFullPath(raw.Trim());
        }
        catch
        {
            return fallbackDir;
        }
    }

    internal static string NormalizeEnum(string? raw, string fallback, params string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var trimmed = raw.Trim();
        foreach (var item in allowed)
        {
            if (string.Equals(trimmed, item, StringComparison.OrdinalIgnoreCase))
                return item;
        }
        return fallback;
    }

    internal static string NormalizeSize(string? raw, long minBytes, long maxBytes, string fallback, bool allowZero)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var trimmed = raw.Trim();
        if (allowZero && string.Equals(trimmed, "0", StringComparison.Ordinal))
            return "0";

        var suffix = '\0';
        var numberPart = trimmed;
        var last = trimmed[^1];
        if (last is 'K' or 'k' or 'M' or 'm')
        {
            suffix = char.ToUpperInvariant(last);
            numberPart = trimmed[..^1];
        }

        if (!long.TryParse(numberPart, NumberStyles.None, CultureInfo.InvariantCulture, out var num) || num <= 0)
            return fallback;

        long bytes;
        try
        {
            bytes = suffix switch
            {
                'K' => checked(num * 1024L),
                'M' => checked(num * 1024L * 1024L),
                _ => num,
            };
        }
        catch (OverflowException)
        {
            return fallback;
        }

        if (bytes < minBytes || bytes > maxBytes)
            return fallback;

        return suffix == '\0'
            ? num.ToString(CultureInfo.InvariantCulture)
            : num.ToString(CultureInfo.InvariantCulture) + suffix;
    }
}
