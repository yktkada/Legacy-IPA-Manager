using System.Globalization;
using System.Text;
using System.Text.Json;
using IdeviceIndex.Resources;

namespace IdeviceIndex;

internal enum RightSection
{
    Queue,
    Details,
}

internal readonly record struct RightSectionSetting(RightSection Section, bool Visible, int Order);

internal sealed record IndexLayoutState(
    double[] Columns,
    bool RightHidden,
    double RightOverlayWidth,
    double[] RightSplit,
    IReadOnlyList<RightSectionSetting> RightSections)
{
    public static IndexLayoutState Default { get; } = new(
        [62d, 38d],
        false,
        420d,
        [55d, 45d],
        [
            new(RightSection.Queue, true, 0),
            new(RightSection.Details, true, 1),
        ]);
}

/// <summary>
/// 两栏可配布局与右侧模块顺序持久化（对照适配自 <c>IdeviceGui\Services\LayoutStore.cs</c>）。
/// </summary>
internal static class IndexLayoutStore
{
    public const string MainColumnsKey = "Layout.MainColumns";
    public const string RightHiddenKey = "Layout.RightHidden";
    public const string RightOverlayWidthKey = "Layout.RightOverlayWidth";
    public const string RightSplitKey = "Layout.RightSplit";
    public const string RightSectionsKey = "Layout.RightSections";

    public const double MinLeftWidth = 420d;
    public const double MinRightWidth = 260d;
    public const double SplitterSize = 5d;

    public const double DefaultOverlayWidth = 420d;
    public const double MinOverlayWidth = 240d;
    public const double MaxOverlayWidth = 1200d;

    public static string Header(RightSection section) => section switch
    {
        RightSection.Queue => Strings.RightSection_Queue,
        RightSection.Details => Strings.RightSection_Details,
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, "Undefined right section"),
    };

    public static IReadOnlyList<RightSection> OrderedVisibleSections(IReadOnlyList<RightSectionSetting> settings)
    {
        return settings.Where(s => s.Visible)
                       .OrderBy(s => s.Order)
                       .ThenBy(s => DefaultOrder(s.Section))
                       .Select(s => s.Section)
                       .ToList();
    }

    internal static double[] ParsePair(string? raw, double[] fallback)
    {
        var text = (raw ?? "").Trim().Trim('[', ']', '{', '}');
        if (text.Length == 0) return fallback;

        var parts = text.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return fallback;

        var values = new double[2];
        for (var i = 0; i < 2; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return fallback;
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) return fallback;
            values[i] = value;
        }

        return NormalizePair(values, fallback);
    }

    internal static double[] NormalizePair(IReadOnlyList<double> values, double[] fallback)
    {
        if (values.Count != 2) return fallback;
        var sum = values[0] + values[1];
        if (sum <= 0 || double.IsNaN(sum) || double.IsInfinity(sum) || values[0] <= 0 || values[1] <= 0)
            return fallback;

        return
        [
            Math.Round(values[0] / sum * 100d, 2, MidpointRounding.AwayFromZero),
            Math.Round(values[1] / sum * 100d, 2, MidpointRounding.AwayFromZero),
        ];
    }

    internal static string FormatPair(IReadOnlyList<double> values, double[] fallback)
    {
        var normalized = NormalizePair(values, fallback);
        var builder = new StringBuilder("[");
        for (var i = 0; i < normalized.Length; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append(normalized[i].ToString("0.##", CultureInfo.InvariantCulture));
        }
        return builder.Append(']').ToString();
    }

    internal static double ParseOverlayWidth(string? raw)
    {
        var text = (raw ?? "").Trim().Trim('"');
        if (text.Length == 0) return DefaultOverlayWidth;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return DefaultOverlayWidth;
        if (double.IsNaN(value) || double.IsInfinity(value)) return DefaultOverlayWidth;
        return Math.Clamp(value, MinOverlayWidth, MaxOverlayWidth);
    }

    internal static IReadOnlyList<RightSectionSetting> ParseRightSections(string? json)
    {
        var defaults = IndexLayoutState.Default.RightSections;
        if (string.IsNullOrWhiteSpace(json)) return defaults;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return defaults; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return defaults;

            var seen = new HashSet<RightSection>();
            var parsed = new List<RightSectionSetting>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()
                    : null;
                if (id is null || !TryParseSection(id, out var section) || !seen.Add(section)) continue;

                var visible = !item.TryGetProperty("visible", out var visEl)
                              || visEl.ValueKind != JsonValueKind.False;
                var order = item.TryGetProperty("order", out var ordEl) && ordEl.TryGetInt32(out var o)
                    ? o
                    : DefaultOrder(section);
                parsed.Add(new RightSectionSetting(section, visible, order));
            }

            if (parsed.Count == 0) return defaults;
            foreach (var d in defaults)
                if (!seen.Contains(d.Section)) parsed.Add(d);
            return parsed;
        }
    }

    internal static string SerializeRightSections(IReadOnlyList<RightSectionSetting> settings)
    {
        var payload = settings
            .OrderBy(s => s.Order)
            .ThenBy(s => DefaultOrder(s.Section))
            .Select(s => new Dictionary<string, object>
            {
                ["id"] = ToId(s.Section),
                ["visible"] = s.Visible,
                ["order"] = s.Order,
            });
        return JsonSerializer.Serialize(payload);
    }

    public static string ToId(RightSection section) => section switch
    {
        RightSection.Queue => "queue",
        RightSection.Details => "details",
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, "Undefined right section id"),
    };

    public static bool TryParseSection(string id, out RightSection section)
    {
        switch (id.Trim().ToLowerInvariant())
        {
            case "queue": section = RightSection.Queue; return true;
            case "details": section = RightSection.Details; return true;
            default: section = RightSection.Queue; return false;
        }
    }

    private static int DefaultOrder(RightSection section)
    {
        var defaults = IndexLayoutState.Default.RightSections;
        for (var i = 0; i < defaults.Count; i++)
            if (defaults[i].Section == section) return defaults[i].Order;
        return int.MaxValue;
    }
}
