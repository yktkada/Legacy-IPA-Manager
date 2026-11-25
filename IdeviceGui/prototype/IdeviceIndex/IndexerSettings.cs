using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IdeviceIndex;

/// <summary>
/// `IdeviceIndex.exe` 本地设置（`&lt;程序目录&gt;\data\indexer.json`，docs/12 §3.4.1 / §5.4）。
/// 只读写自身设置，不写用户目录，不碰主程序 `library.db`。
/// </summary>
internal static class IndexerSettings
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static readonly object FileLock = new();

    public const int DefaultDisplayLimit = 0;
    public const string IndexDbPathKey = "Index.DbPath";
    public const string IndexUpdateUrlKey = "Index.UpdateUrl";
    public const string ListDisplayLimitKey = "List.DisplayLimit";
    public const string ListMergeSameBundleKey = "List.MergeSameBundle";
    public const string SearchScopeKey = "Search.Scope";
    public const string SearchMatchModeKey = "Search.MatchMode";
    public const string SearchAdvancedFilterKey = "Search.AdvancedFilter";
    public const string LinksOpenProgramKey = "Links.OpenProgram";

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "data", "indexer.json");

    public static string LoadLanguage(string? path = null)
    {
        var obj = TryReadObject(path);
        if (obj is null || !obj.TryGetPropertyValue(UiLanguage.SettingKey, out var node) || node is null)
            return UiLanguage.SystemSetting;

        return node is JsonValue jv && jv.TryGetValue<string>(out var s)
            ? UiLanguage.NormalizeSetting(s)
            : UiLanguage.SystemSetting;
    }

    public static void SaveLanguage(string language, string? path = null) =>
        UpdateObject(path, obj => obj[UiLanguage.SettingKey] = UiLanguage.NormalizeSetting(language));

    public static IndexLayoutState LoadLayout(string? path = null)
    {
        var obj = TryReadObject(path);
        if (obj is null) return IndexLayoutState.Default;

        var columns = IndexLayoutStore.ParsePair(ReadRawText(obj[IndexLayoutStore.MainColumnsKey]), IndexLayoutState.Default.Columns);
        var rightHidden = ReadBool(obj[IndexLayoutStore.RightHiddenKey], IndexLayoutState.Default.RightHidden);
        var overlayWidth = IndexLayoutStore.ParseOverlayWidth(ReadRawText(obj[IndexLayoutStore.RightOverlayWidthKey]));
        var rightSplit = IndexLayoutStore.ParsePair(ReadRawText(obj[IndexLayoutStore.RightSplitKey]), IndexLayoutState.Default.RightSplit);
        var rightSections = IndexLayoutStore.ParseRightSections(ReadRawText(obj[IndexLayoutStore.RightSectionsKey]));

        return new IndexLayoutState(columns, rightHidden, overlayWidth, rightSplit, rightSections);
    }

    public static void SaveLayout(IndexLayoutState layout, string? path = null) =>
        UpdateObject(path, obj => WriteLayoutProperties(obj, layout));

    public static double LoadGridRowHeight(string? path = null)
    {
        var obj = TryReadObject(path);
        if (obj is null) return IndexGridColumns.RowHeightMedium;

        var node = obj[IndexGridColumns.GridRowHeightKey];
        if (node is JsonValue jv)
        {
            if (jv.TryGetValue<double>(out var num)) return IndexGridColumns.ClampRowHeight(num);
            if (jv.TryGetValue<string>(out var str) &&
                double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return IndexGridColumns.ClampRowHeight(parsed);
        }
        return IndexGridColumns.RowHeightMedium;
    }

    public static void SaveGridRowHeight(double height, string? path = null) =>
        UpdateObject(path, obj => obj[IndexGridColumns.GridRowHeightKey] = IndexGridColumns.ClampRowHeight(height));

    public static IReadOnlyList<IndexGridColumnState> LoadGridColumns(string? path = null)
    {
        var obj = TryReadObject(path);
        return obj is null
            ? IndexGridColumns.DefaultStates
            : IndexGridColumns.Parse(ReadRawText(obj[IndexGridColumns.GridColumnsKey]));
    }

    public static void SaveGridColumns(IReadOnlyList<IndexGridColumnState> columns, string? path = null) =>
        UpdateObject(path, obj => obj[IndexGridColumns.GridColumnsKey] = JsonNode.Parse(IndexGridColumns.Serialize(columns)));

    public static IReadOnlyList<DownloadQueueColumnSetting> LoadQueueColumns(string? path = null)
    {
        var obj = TryReadObject(path);
        return obj is null
            ? DownloadQueueColumns.Default
            : DownloadQueueColumns.Parse(ReadRawText(obj[DownloadQueueColumns.SettingsKey]));
    }

    public static void SaveQueueColumns(IReadOnlyList<DownloadQueueColumnSetting> columns, string? path = null) =>
        UpdateObject(path, obj => obj[DownloadQueueColumns.SettingsKey] = JsonNode.Parse(DownloadQueueColumns.Serialize(columns)));

    public static bool LoadAllowOutsideProgramDirWrite(string? path = null)
    {
        var obj = TryReadObject(path);
        return obj is not null && ReadBool(obj[DownloadSettings.AllowOutsideProgramDirWriteKey], false);
    }

    public static void SaveAllowOutsideProgramDirWrite(bool allow, string? path = null) =>
        UpdateObject(path, obj => obj[DownloadSettings.AllowOutsideProgramDirWriteKey] = allow);

    public static int NormalizeDisplayLimit(int value)
        => value >= 0 ? value : DefaultDisplayLimit;

    public static int ParseDisplayLimit(string? raw, int fallback = DefaultDisplayLimit)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
            return parsed;
        return fallback;
    }

    public static int LoadDisplayLimit(string? path = null)
    {
        var obj = TryReadObject(path);
        if (obj is null) return DefaultDisplayLimit;
        var raw = ReadInt(obj[ListDisplayLimitKey], DefaultDisplayLimit);
        return NormalizeDisplayLimit(raw);
    }

    public static void SaveDisplayLimit(int limit, string? path = null) =>
        UpdateObject(path, obj => obj[ListDisplayLimitKey] = NormalizeDisplayLimit(limit));

    public static bool LoadMergeSameBundle(string? path = null)
    {
        var obj = TryReadObject(path);
        return obj is null || ReadBool(obj[ListMergeSameBundleKey], true);
    }

    public static void SaveMergeSameBundle(bool merge, string? path = null) =>
        UpdateObject(path, obj => obj[ListMergeSameBundleKey] = merge);

    public static IndexSearchScope LoadSearchScope(string? path = null)
    {
        var obj = TryReadObject(path);
        return obj is null ? new IndexSearchScope() : IndexSearchScope.ParseJson(obj[SearchScopeKey]);
    }

    public static void SaveSearchScope(IndexSearchScope scope, string? path = null) =>
        UpdateObject(path, obj => obj[SearchScopeKey] = scope.ToJsonArray());

    public static IndexSearchMatchMode LoadSearchMatchMode(string? path = null)
    {
        var obj = TryReadObject(path);
        if (obj is null) return IndexSearchMatchMode.Contains;
        return IndexSearchScope.ParseMatchMode(ReadRawText(obj[SearchMatchModeKey]));
    }

    public static void SaveSearchMatchMode(IndexSearchMatchMode mode, string? path = null) =>
        UpdateObject(path, obj => obj[SearchMatchModeKey] = IndexSearchScope.MatchModeKey(mode));

    public static IndexAdvancedFilterState LoadAdvancedFilter(string? path = null)
    {
        var obj = TryReadObject(path);
        return obj is null ? new IndexAdvancedFilterState() : IndexAdvancedFilterState.ParseJson(obj[SearchAdvancedFilterKey]);
    }

    public static void SaveAdvancedFilter(IndexAdvancedFilterState state, string? path = null) =>
        UpdateObject(path, obj => obj[SearchAdvancedFilterKey] = state.ToJsonObject());

    public static string LoadCustomDbPath(string? path = null)
    {
        var obj = TryReadObject(path);
        return obj is null ? "" : ReadStringAllowEmpty(obj[IndexDbPathKey], "").Trim();
    }

    public static void SaveCustomDbPath(string? dbPath, string? path = null) =>
        UpdateObject(path, obj => obj[IndexDbPathKey] = (dbPath ?? "").Trim());

    public static string LoadUpdateUrl(string? path = null)
    {
        var obj = TryReadObject(path);
        return obj is null ? "" : ReadStringAllowEmpty(obj[IndexUpdateUrlKey], "").Trim();
    }

    public static void SaveUpdateUrl(string? url, string? path = null) =>
        UpdateObject(path, obj => obj[IndexUpdateUrlKey] = (url ?? "").Trim());

    public static string LoadOpenLinkProgram(string? path = null)
    {
        var obj = TryReadObject(path);
        return obj is null ? "" : ReadStringAllowEmpty(obj[LinksOpenProgramKey], "").Trim();
    }

    public static void SaveOpenLinkProgram(string? programPath, string? path = null) =>
        UpdateObject(path, obj => obj[LinksOpenProgramKey] = (programPath ?? "").Trim());

    public static DownloadSettings LoadDownloadSettings(string? path = null, string? programDir = null)
    {
        var defaults = DownloadSettings.CreateDefault(programDir);
        var obj = TryReadObject(path);
        if (obj is null) return defaults;

        var raw = new DownloadSettings
        {
            Dir = ReadString(obj[DownloadSettings.DirKey], defaults.Dir),
            RememberLastDir = ReadBool(obj[DownloadSettings.RememberLastDirKey], defaults.RememberLastDir),
            Connections = ReadInt(obj[DownloadSettings.ConnectionsKey], defaults.Connections),
            Splits = ReadInt(obj[DownloadSettings.SplitsKey], defaults.Splits),
            MinSplitSize = ReadString(obj[DownloadSettings.MinSplitSizeKey], defaults.MinSplitSize),
            Concurrency = ReadInt(obj[DownloadSettings.ConcurrencyKey], defaults.Concurrency),
            SpeedLimit = ReadString(obj[DownloadSettings.SpeedLimitKey], defaults.SpeedLimit),
            PerTaskSpeedLimit = ReadString(obj[DownloadSettings.PerTaskSpeedLimitKey], defaults.PerTaskSpeedLimit),
            RetryTimes = ReadInt(obj[DownloadSettings.RetryTimesKey], defaults.RetryTimes),
            RetryWait = ReadInt(obj[DownloadSettings.RetryWaitKey], defaults.RetryWait),
            Timeout = ReadInt(obj[DownloadSettings.TimeoutKey], defaults.Timeout),
            Continue = ReadBool(obj[DownloadSettings.ContinueKey], defaults.Continue),
            ProxyMode = ReadString(obj[DownloadSettings.ProxyModeKey], defaults.ProxyMode),
            ProxyUrl = ReadString(obj[DownloadSettings.ProxyUrlKey], defaults.ProxyUrl),
            ProxyBypass = ReadStringAllowEmpty(obj[DownloadSettings.ProxyBypassKey], defaults.ProxyBypass),
            UserAgent = ReadString(obj[DownloadSettings.UserAgentKey], defaults.UserAgent),
            SessionDirMode = ReadString(obj[DownloadSettings.SessionDirModeKey], defaults.SessionDirMode),
            SessionDirCustom = ReadStringAllowEmpty(obj[DownloadSettings.SessionDirCustomKey], defaults.SessionDirCustom),
            ControlFileMode = ReadString(obj[DownloadSettings.ControlFileModeKey], defaults.ControlFileMode),
            KeepPartFiles = ReadBool(obj[DownloadSettings.KeepPartFilesKey], defaults.KeepPartFiles),
            PartTtlHours = ReadInt(obj[DownloadSettings.PartTtlHoursKey], defaults.PartTtlHours),
            SpaceMarginMB = ReadInt(obj[DownloadSettings.SpaceMarginMbKey], defaults.SpaceMarginMB),
            AutoImport = ReadBool(obj[DownloadSettings.AutoImportKey], defaults.AutoImport),
            AutoRefreshFallback = ReadBool(obj[DownloadSettings.AutoRefreshFallbackKey], defaults.AutoRefreshFallback),
            OpenFolderWhenDone = ReadBool(obj[DownloadSettings.OpenFolderWhenDoneKey], defaults.OpenFolderWhenDone),
            MaxFileSizeMB = ReadInt(obj[DownloadSettings.MaxFileSizeMbKey], defaults.MaxFileSizeMB),
        };
        return raw.Normalize(programDir);
    }

    public static void SaveDownloadSettings(DownloadSettings settings, string? path = null, string? programDir = null)
    {
        var normalized = settings.Normalize(programDir);
        UpdateObject(path, obj =>
        {
            obj[DownloadSettings.DirKey] = normalized.Dir;
            obj[DownloadSettings.RememberLastDirKey] = normalized.RememberLastDir;
            obj[DownloadSettings.ConnectionsKey] = normalized.Connections;
            obj[DownloadSettings.SplitsKey] = normalized.Splits;
            obj[DownloadSettings.MinSplitSizeKey] = normalized.MinSplitSize;
            obj[DownloadSettings.ConcurrencyKey] = normalized.Concurrency;
            obj[DownloadSettings.SpeedLimitKey] = normalized.SpeedLimit;
            obj[DownloadSettings.PerTaskSpeedLimitKey] = normalized.PerTaskSpeedLimit;
            obj[DownloadSettings.RetryTimesKey] = normalized.RetryTimes;
            obj[DownloadSettings.RetryWaitKey] = normalized.RetryWait;
            obj[DownloadSettings.TimeoutKey] = normalized.Timeout;
            obj[DownloadSettings.ContinueKey] = normalized.Continue;
            obj[DownloadSettings.ProxyModeKey] = normalized.ProxyMode;
            obj[DownloadSettings.ProxyUrlKey] = normalized.ProxyUrl;
            obj[DownloadSettings.ProxyBypassKey] = normalized.ProxyBypass;
            obj[DownloadSettings.UserAgentKey] = normalized.UserAgent;
            obj[DownloadSettings.SessionDirModeKey] = normalized.SessionDirMode;
            obj[DownloadSettings.SessionDirCustomKey] = normalized.SessionDirCustom;
            obj[DownloadSettings.ControlFileModeKey] = normalized.ControlFileMode;
            obj[DownloadSettings.KeepPartFilesKey] = normalized.KeepPartFiles;
            obj[DownloadSettings.PartTtlHoursKey] = normalized.PartTtlHours;
            obj[DownloadSettings.SpaceMarginMbKey] = normalized.SpaceMarginMB;
            obj[DownloadSettings.AutoImportKey] = normalized.AutoImport;
            obj[DownloadSettings.AutoRefreshFallbackKey] = normalized.AutoRefreshFallback;
            obj[DownloadSettings.OpenFolderWhenDoneKey] = normalized.OpenFolderWhenDone;
            obj[DownloadSettings.MaxFileSizeMbKey] = normalized.MaxFileSizeMB;
        });
    }

    public static bool RecordLastDownloadDir(string chosenDir, string? path = null, string? programDir = null)
    {
        if (string.IsNullOrWhiteSpace(chosenDir)) return false;
        var current = LoadDownloadSettings(path, programDir);
        if (!current.RememberLastDir) return false;

        var normalizedDir = DownloadSettings.NormalizeDir(chosenDir, DownloadSettings.DefaultDir(programDir));
        UpdateObject(path, obj => obj[DownloadSettings.DirKey] = normalizedDir);
        return true;
    }

    public static void SaveWindowState(
        IndexLayoutState layout,
        double rowHeight,
        IReadOnlyList<IndexGridColumnState> gridColumns,
        string? path = null)
    {
        UpdateObject(path, obj =>
        {
            WriteLayoutProperties(obj, layout);
            obj[IndexGridColumns.GridRowHeightKey] = IndexGridColumns.ClampRowHeight(rowHeight);
            obj[IndexGridColumns.GridColumnsKey] = JsonNode.Parse(IndexGridColumns.Serialize(gridColumns));
        });
    }

    private static void WriteLayoutProperties(JsonObject obj, IndexLayoutState layout)
    {
        obj[IndexLayoutStore.MainColumnsKey] = JsonNode.Parse(
            IndexLayoutStore.FormatPair(layout.Columns, IndexLayoutState.Default.Columns));
        obj[IndexLayoutStore.RightHiddenKey] = layout.RightHidden;
        obj[IndexLayoutStore.RightOverlayWidthKey] = Math.Clamp(
            layout.RightOverlayWidth, IndexLayoutStore.MinOverlayWidth, IndexLayoutStore.MaxOverlayWidth);
        obj[IndexLayoutStore.RightSplitKey] = JsonNode.Parse(
            IndexLayoutStore.FormatPair(layout.RightSplit, IndexLayoutState.Default.RightSplit));
        obj[IndexLayoutStore.RightSectionsKey] = JsonNode.Parse(
            IndexLayoutStore.SerializeRightSections(layout.RightSections));
    }

    private static JsonObject? TryReadObject(string? path)
    {
        var target = Path.GetFullPath(path ?? DefaultPath);
        lock (FileLock)
        {
            if (!File.Exists(target)) return null;

            try
            {
                var node = JsonNode.Parse(File.ReadAllText(target));
                return node as JsonObject;
            }
            catch
            {
                return null;
            }
        }
    }

    private static void UpdateObject(string? path, Action<JsonObject> mutate)
    {
        var target = Path.GetFullPath(path ?? DefaultPath);
        lock (FileLock)
        {
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var obj = TryReadObject(target) ?? new JsonObject();
            mutate(obj);

            var tmp = target + ".tmp";
            File.WriteAllText(tmp, obj.ToJsonString(WriteOptions));
            File.Move(tmp, target, overwrite: true);
        }
    }

    private static string? ReadRawText(JsonNode? node) => node switch
    {
        null => null,
        JsonValue jv when jv.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString(),
    };

    private static bool ReadBool(JsonNode? node, bool fallback)
    {
        if (node is not JsonValue jv) return fallback;
        if (jv.TryGetValue<bool>(out var b)) return b;
        if (jv.TryGetValue<string>(out var s) && bool.TryParse(s, out var parsed)) return parsed;
        return fallback;
    }

    private static int ReadInt(JsonNode? node, int fallback)
    {
        if (node is not JsonValue jv) return fallback;
        if (jv.TryGetValue<int>(out var i)) return i;
        if (jv.TryGetValue<long>(out var l) && l is >= int.MinValue and <= int.MaxValue) return (int)l;
        if (jv.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return fallback;
    }

    private static string ReadString(JsonNode? node, string fallback)
    {
        if (node is not JsonValue jv) return fallback;
        if (jv.TryGetValue<string>(out var s))
            return string.IsNullOrWhiteSpace(s) ? fallback : s;
        return fallback;
    }

    private static string ReadStringAllowEmpty(JsonNode? node, string fallback)
    {
        if (node is not JsonValue jv) return fallback;
        if (jv.TryGetValue<string>(out var s))
            return s;
        return fallback;
    }
}
