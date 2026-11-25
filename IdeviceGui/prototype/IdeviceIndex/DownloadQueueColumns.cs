using System.Text.Json;
using IdeviceIndex.Resources;

namespace IdeviceIndex;

/// <summary>
/// 下载队列行显示字段（对照适配自 <c>IdeviceGui\Services\QueueColumns.cs</c>）：
/// 默认只显示 状态·备注·名称·版本·大小，其余字段可通过配置显隐并排序。
/// </summary>
internal enum DownloadQueueColumn
{
    Status,
    Note,
    Name,
    Version,
    Size,
    BundleId,
    MinOs,
    DeviceFamily,
    Source,
    TargetPath,
}

internal readonly record struct DownloadQueueColumnSetting(DownloadQueueColumn Column, bool Visible, int Order);

internal static class DownloadQueueColumns
{
    public const string SettingsKey = "Queue.Columns";

    public static IReadOnlyList<DownloadQueueColumnSetting> Default { get; } = BuildDefault();

    /// <summary>按配置取可见字段，按 order 升序；同 order 按默认顺序稳定。</summary>
    public static IReadOnlyList<DownloadQueueColumn> OrderedVisible(IReadOnlyList<DownloadQueueColumnSetting> settings)
    {
        var visible = settings.Where(s => s.Visible)
                              .OrderBy(s => s.Order)
                              .ThenBy(s => DefaultOrder(s.Column))
                              .Select(s => s.Column)
                              .ToList();
        return visible.Count > 0 ? visible : [DownloadQueueColumn.Status];
    }

    public static string Format(DownloadJobRow job, IReadOnlyList<DownloadQueueColumn> columns)
    {
        var parts = new List<string>(columns.Count);
        foreach (var column in columns)
        {
            switch (column)
            {
                case DownloadQueueColumn.Status:
                    parts.Add($"[{job.StatusText}{job.DisplayPercentText}]");
                    break;
                case DownloadQueueColumn.Note:
                    if (job.Status is (DownloadJobStatus.Succeeded or DownloadJobStatus.Cancelled)
                        || string.IsNullOrWhiteSpace(job.Note))
                        continue;
                    parts.Add(job.Note.Trim());
                    break;
                case DownloadQueueColumn.Name:
                    parts.Add(job.DisplayNameText);
                    break;
                case DownloadQueueColumn.Version:
                    parts.Add(job.VersionText);
                    break;
                case DownloadQueueColumn.Size:
                    parts.Add(job.SizeText);
                    break;
                case DownloadQueueColumn.BundleId:
                    parts.Add(job.BundleId);
                    break;
                case DownloadQueueColumn.MinOs:
                    parts.Add(job.MinOsText);
                    break;
                case DownloadQueueColumn.DeviceFamily:
                    parts.Add(job.DeviceFamilyText);
                    break;
                case DownloadQueueColumn.Source:
                    parts.Add(job.SourceSummaryText);
                    break;
                case DownloadQueueColumn.TargetPath:
                    parts.Add(job.TargetPathText);
                    break;
            }
        }

        return string.Join(" | ", parts);
    }

    public static string Header(DownloadQueueColumn column) => column switch
    {
        DownloadQueueColumn.Status => Strings.Queue_Col_Status,
        DownloadQueueColumn.Note => Strings.Queue_Col_Note,
        DownloadQueueColumn.Name => Strings.Queue_Col_Name,
        DownloadQueueColumn.Version => Strings.Queue_Col_Version,
        DownloadQueueColumn.Size => Strings.Queue_Col_Size,
        DownloadQueueColumn.BundleId => "BundleId",
        DownloadQueueColumn.MinOs => Strings.Queue_Col_MinOs,
        DownloadQueueColumn.DeviceFamily => Strings.Queue_Col_DeviceFamily,
        DownloadQueueColumn.Source => Strings.Queue_Col_Source,
        DownloadQueueColumn.TargetPath => Strings.Queue_Col_TargetPath,
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Undefined download queue column"),
    };

    public static IReadOnlyList<DownloadQueueColumnSetting> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return Default; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Default;

            var seen = new HashSet<DownloadQueueColumn>();
            var parsed = new List<DownloadQueueColumnSetting>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()
                    : null;
                if (id is null || !TryParseColumn(id, out var column) || !seen.Add(column)) continue;

                var visible = !item.TryGetProperty("visible", out var visEl)
                              || visEl.ValueKind != JsonValueKind.False;
                var order = item.TryGetProperty("order", out var ordEl) && ordEl.TryGetInt32(out var o)
                    ? o
                    : DefaultOrder(column);
                parsed.Add(new DownloadQueueColumnSetting(column, visible, order));
            }

            if (parsed.Count == 0) return Default;
            foreach (var d in Default)
                if (!seen.Contains(d.Column)) parsed.Add(d);
            return parsed;
        }
    }

    public static string Serialize(IReadOnlyList<DownloadQueueColumnSetting> settings)
    {
        var payload = settings
            .OrderBy(s => s.Order)
            .ThenBy(s => DefaultOrder(s.Column))
            .Select(s => new Dictionary<string, object>
            {
                ["id"] = ToId(s.Column),
                ["visible"] = s.Visible,
                ["order"] = s.Order,
            });
        return JsonSerializer.Serialize(payload);
    }

    public static string ToId(DownloadQueueColumn column) => column switch
    {
        DownloadQueueColumn.Status => "status",
        DownloadQueueColumn.Note => "note",
        DownloadQueueColumn.Name => "name",
        DownloadQueueColumn.Version => "version",
        DownloadQueueColumn.Size => "size",
        DownloadQueueColumn.BundleId => "bundleId",
        DownloadQueueColumn.MinOs => "minOs",
        DownloadQueueColumn.DeviceFamily => "deviceFamily",
        DownloadQueueColumn.Source => "source",
        DownloadQueueColumn.TargetPath => "targetPath",
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Undefined download queue column id"),
    };

    public static bool TryParseColumn(string id, out DownloadQueueColumn column)
    {
        switch (id.Trim().ToLowerInvariant())
        {
            case "status": column = DownloadQueueColumn.Status; return true;
            case "note": column = DownloadQueueColumn.Note; return true;
            case "name": column = DownloadQueueColumn.Name; return true;
            case "version": column = DownloadQueueColumn.Version; return true;
            case "size": column = DownloadQueueColumn.Size; return true;
            case "bundleid": column = DownloadQueueColumn.BundleId; return true;
            case "minos":
            case "minios":
                column = DownloadQueueColumn.MinOs; return true;
            case "devicefamily": column = DownloadQueueColumn.DeviceFamily; return true;
            case "source": column = DownloadQueueColumn.Source; return true;
            case "targetpath": column = DownloadQueueColumn.TargetPath; return true;
            default: column = DownloadQueueColumn.Status; return false;
        }
    }

    private static int DefaultOrder(DownloadQueueColumn column)
    {
        for (var i = 0; i < Default.Count; i++)
            if (Default[i].Column == column) return Default[i].Order;
        return int.MaxValue;
    }

    private static DownloadQueueColumnSetting[] BuildDefault() =>
    [
        new(DownloadQueueColumn.Status, true, 0),
        new(DownloadQueueColumn.Note, true, 1),
        new(DownloadQueueColumn.Name, true, 2),
        new(DownloadQueueColumn.Version, true, 3),
        new(DownloadQueueColumn.Size, true, 4),
        new(DownloadQueueColumn.BundleId, false, 5),
        new(DownloadQueueColumn.MinOs, false, 6),
        new(DownloadQueueColumn.DeviceFamily, false, 7),
        new(DownloadQueueColumn.Source, false, 8),
        new(DownloadQueueColumn.TargetPath, false, 9),
    ];
}
