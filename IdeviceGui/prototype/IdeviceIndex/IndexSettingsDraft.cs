using System.Globalization;
using System.IO;

namespace IdeviceIndex;

internal enum IndexSettingsGroup
{
    General,
    Download,
    NetworkSession,
    Security,
}

/// <summary>
/// 设置窗口草稿与批量读写模型（纯逻辑，支持无 UI 单元测试）。
/// 覆盖 docs/12 §3.4.1 全部设置项、`Ui.Language`、`Layout.RightSections`、`Queue.Columns` 与 `Security.AllowOutsideProgramDirWrite`。
/// </summary>
internal sealed class IndexSettingsDraft
{
    public string UiLanguageSetting { get; set; } = UiLanguage.SystemSetting;
    public int DisplayLimit { get; set; } = IndexerSettings.DefaultDisplayLimit;
    public string OpenLinkProgram { get; set; } = "";
    public IReadOnlyList<RightSectionSetting> RightSections { get; set; } = IndexLayoutState.Default.RightSections;
    public IReadOnlyList<DownloadQueueColumnSetting> QueueColumns { get; set; } = DownloadQueueColumns.Default;
    public DownloadSettings Download { get; set; } = DownloadSettings.CreateDefault();
    public bool AllowOutsideProgramDirWrite { get; set; }

    public static IndexSettingsDraft Load(string? settingsPath = null, string? programDir = null)
    {
        var layout = IndexerSettings.LoadLayout(settingsPath);
        return new IndexSettingsDraft
        {
            UiLanguageSetting = IndexerSettings.LoadLanguage(settingsPath),
            DisplayLimit = IndexerSettings.LoadDisplayLimit(settingsPath),
            OpenLinkProgram = IndexerSettings.LoadOpenLinkProgram(settingsPath),
            RightSections = layout.RightSections.OrderBy(s => s.Order).ToList(),
            QueueColumns = IndexerSettings.LoadQueueColumns(settingsPath).OrderBy(c => c.Order).ToList(),
            Download = IndexerSettings.LoadDownloadSettings(settingsPath, programDir),
            AllowOutsideProgramDirWrite = IndexerSettings.LoadAllowOutsideProgramDirWrite(settingsPath),
        };
    }

    public IndexSettingsDraft Clone() => new()
    {
        UiLanguageSetting = UiLanguageSetting,
        DisplayLimit = DisplayLimit,
        OpenLinkProgram = OpenLinkProgram,
        RightSections = RightSections.ToList(),
        QueueColumns = QueueColumns.ToList(),
        Download = Download with { },
        AllowOutsideProgramDirWrite = AllowOutsideProgramDirWrite,
    };

    public void Save(string? settingsPath = null, string? programDir = null)
    {
        var normalizedDownload = Download.Normalize(programDir);
        Download = normalizedDownload;
        UiLanguageSetting = UiLanguage.NormalizeSetting(UiLanguageSetting);
        DisplayLimit = IndexerSettings.NormalizeDisplayLimit(DisplayLimit);
        OpenLinkProgram = (OpenLinkProgram ?? "").Trim();

        var currentLayout = IndexerSettings.LoadLayout(settingsPath);
        var updatedLayout = currentLayout with { RightSections = ReindexSections(RightSections) };

        IndexerSettings.SaveLanguage(UiLanguageSetting, settingsPath);
        IndexerSettings.SaveDisplayLimit(DisplayLimit, settingsPath);
        IndexerSettings.SaveOpenLinkProgram(OpenLinkProgram, settingsPath);
        IndexerSettings.SaveLayout(updatedLayout, settingsPath);
        IndexerSettings.SaveQueueColumns(ReindexQueueColumns(QueueColumns), settingsPath);
        IndexerSettings.SaveAllowOutsideProgramDirWrite(AllowOutsideProgramDirWrite, settingsPath);
        IndexerSettings.SaveDownloadSettings(normalizedDownload, settingsPath, programDir);
    }

    public void ResetGroup(IndexSettingsGroup group, string? programDir = null)
    {
        var defaults = DownloadSettings.CreateDefault(programDir);
        switch (group)
        {
            case IndexSettingsGroup.General:
                UiLanguageSetting = UiLanguage.SystemSetting;
                DisplayLimit = IndexerSettings.DefaultDisplayLimit;
                OpenLinkProgram = "";
                RightSections = IndexLayoutState.Default.RightSections.ToList();
                QueueColumns = DownloadQueueColumns.Default.ToList();
                break;

            case IndexSettingsGroup.Download:
                Download = Download with
                {
                    Dir = defaults.Dir,
                    RememberLastDir = defaults.RememberLastDir,
                    Connections = defaults.Connections,
                    Splits = defaults.Splits,
                    MinSplitSize = defaults.MinSplitSize,
                    Concurrency = defaults.Concurrency,
                    SpeedLimit = defaults.SpeedLimit,
                    PerTaskSpeedLimit = defaults.PerTaskSpeedLimit,
                    RetryTimes = defaults.RetryTimes,
                    RetryWait = defaults.RetryWait,
                    Timeout = defaults.Timeout,
                    Continue = defaults.Continue,
                };
                break;

            case IndexSettingsGroup.NetworkSession:
                Download = Download with
                {
                    ProxyMode = defaults.ProxyMode,
                    ProxyUrl = defaults.ProxyUrl,
                    ProxyBypass = defaults.ProxyBypass,
                    UserAgent = defaults.UserAgent,
                    SessionDirMode = defaults.SessionDirMode,
                    SessionDirCustom = defaults.SessionDirCustom,
                    ControlFileMode = defaults.ControlFileMode,
                    KeepPartFiles = defaults.KeepPartFiles,
                    PartTtlHours = defaults.PartTtlHours,
                    SpaceMarginMB = defaults.SpaceMarginMB,
                    MaxFileSizeMB = defaults.MaxFileSizeMB,
                    AutoImport = defaults.AutoImport,
                    AutoRefreshFallback = defaults.AutoRefreshFallback,
                    OpenFolderWhenDone = defaults.OpenFolderWhenDone,
                };
                break;

            case IndexSettingsGroup.Security:
                AllowOutsideProgramDirWrite = false;
                break;
        }
    }

    public static IReadOnlyList<RightSectionSetting> MoveSection(
        IReadOnlyList<RightSectionSetting> items, int index, int delta)
    {
        var list = items.ToList();
        var target = index + delta;
        if (index < 0 || index >= list.Count || target < 0 || target >= list.Count)
            return list;

        (list[index], list[target]) = (list[target], list[index]);
        return ReindexSections(list);
    }

    public static IReadOnlyList<DownloadQueueColumnSetting> MoveQueueColumn(
        IReadOnlyList<DownloadQueueColumnSetting> items, int index, int delta)
    {
        var list = items.ToList();
        var target = index + delta;
        if (index < 0 || index >= list.Count || target < 0 || target >= list.Count)
            return list;

        (list[index], list[target]) = (list[target], list[index]);
        return ReindexQueueColumns(list);
    }

    public static int ParseIntOrFallback(string? raw, int fallback)
        => int.TryParse(raw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    public static DownloadSettings NormalizeFromInputs(
        string? dir,
        bool rememberLastDir,
        string? connectionsText,
        string? splitsText,
        string? minSplitSize,
        string? concurrencyText,
        string? speedLimit,
        string? perTaskSpeedLimit,
        string? retryTimesText,
        string? retryWaitText,
        string? timeoutText,
        bool continueDownload,
        string? proxyMode,
        string? proxyUrl,
        string? proxyBypass,
        string? userAgent,
        string? sessionDirMode,
        string? sessionDirCustom,
        string? controlFileMode,
        bool keepPartFiles,
        string? partTtlHoursText,
        string? spaceMarginMbText,
        string? maxFileSizeMbText,
        bool autoImport,
        bool autoRefreshFallback,
        bool openFolderWhenDone,
        string? programDir = null)
    {
        var defaults = DownloadSettings.CreateDefault(programDir);
        var raw = new DownloadSettings
        {
            Dir = string.IsNullOrWhiteSpace(dir) ? defaults.Dir : dir.Trim(),
            RememberLastDir = rememberLastDir,
            Connections = ParseIntOrFallback(connectionsText, -1),
            Splits = ParseIntOrFallback(splitsText, -1),
            MinSplitSize = minSplitSize ?? "",
            Concurrency = ParseIntOrFallback(concurrencyText, -1),
            SpeedLimit = speedLimit ?? "",
            PerTaskSpeedLimit = perTaskSpeedLimit ?? "",
            RetryTimes = ParseIntOrFallback(retryTimesText, -1),
            RetryWait = ParseIntOrFallback(retryWaitText, -1),
            Timeout = ParseIntOrFallback(timeoutText, -1),
            Continue = continueDownload,
            ProxyMode = proxyMode ?? defaults.ProxyMode,
            ProxyUrl = proxyUrl ?? "",
            ProxyBypass = proxyBypass ?? "",
            UserAgent = userAgent ?? "",
            SessionDirMode = sessionDirMode ?? defaults.SessionDirMode,
            SessionDirCustom = sessionDirCustom ?? "",
            ControlFileMode = controlFileMode ?? defaults.ControlFileMode,
            KeepPartFiles = keepPartFiles,
            PartTtlHours = ParseIntOrFallback(partTtlHoursText, -1),
            SpaceMarginMB = ParseIntOrFallback(spaceMarginMbText, -1),
            MaxFileSizeMB = ParseIntOrFallback(maxFileSizeMbText, -1),
            AutoImport = autoImport,
            AutoRefreshFallback = autoRefreshFallback,
            OpenFolderWhenDone = openFolderWhenDone,
        };
        return raw.Normalize(programDir);
    }

    private static IReadOnlyList<RightSectionSetting> ReindexSections(IReadOnlyList<RightSectionSetting> items)
    {
        var result = new List<RightSectionSetting>(items.Count);
        for (var i = 0; i < items.Count; i++)
            result.Add(items[i] with { Order = i });
        return result;
    }

    private static IReadOnlyList<DownloadQueueColumnSetting> ReindexQueueColumns(IReadOnlyList<DownloadQueueColumnSetting> items)
    {
        var result = new List<DownloadQueueColumnSetting>(items.Count);
        for (var i = 0; i < items.Count; i++)
            result.Add(items[i] with { Order = i });
        return result;
    }
}
