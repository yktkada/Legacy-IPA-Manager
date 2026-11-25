using System.Text.Json;
using IdeviceIndex.Resources;

namespace IdeviceIndex;

/// <summary>索引网格列定义（对照适配自 <c>IdeviceGui\ViewModels\Library\LibraryColumns.cs</c>）。</summary>
internal sealed record IndexColumnSpec(string Key, Func<string> HeaderProvider, double DefaultWidth, double MinWidth = 60)
{
    public string Header => HeaderProvider();
}

internal sealed record IndexGridColumnState(string Key, double Width, bool Visible);

internal readonly record struct IndexGridColumnEntry(string Key, int DisplayIndex, double Width, bool Visible);

internal static class IndexGridColumns
{
    public const string GridColumnsKey = "Grid.Columns";
    public const string GridRowHeightKey = "Grid.RowHeight";

    public const double RowHeightSmall = 24d;
    public const double RowHeightMedium = 32d;
    public const double RowHeightLarge = 42d;
    public const double MinRowHeight = 16d;
    public const double MaxRowHeight = 48d;

    public const string Icon = "icon";
    public const string Title = "title";
    public const string BundleId = "bundleId";
    public const string Version = "version";
    public const string MinOs = "minOs";
    public const string DeviceFamily = "deviceFamily";
    public const string Size = "size";
    public const string Category = "category";

    public static IReadOnlyList<IndexColumnSpec> Defaults { get; } = new[]
    {
        new IndexColumnSpec(Icon, static () => Strings.Column_Icon, 44, 22),
        new IndexColumnSpec(Title, static () => Strings.Column_Title, 180, 100),
        new IndexColumnSpec(BundleId, static () => "BundleId", 220, 100),
        new IndexColumnSpec(Version, static () => Strings.Column_Version, 90, 50),
        new IndexColumnSpec(MinOs, static () => Strings.Column_MinOs, 80, 50),
        new IndexColumnSpec(DeviceFamily, static () => Strings.Column_DeviceFamily, 100, 60),
        new IndexColumnSpec(Size, static () => Strings.Column_Size, 90, 57),
        new IndexColumnSpec(Category, static () => Strings.Column_Category, 130, 70),
    };

    public static IReadOnlyList<IndexGridColumnState> DefaultStates =>
        Defaults.Select(spec => new IndexGridColumnState(spec.Key, spec.DefaultWidth, true)).ToArray();

    public static IndexColumnSpec? Find(string key) => Defaults.FirstOrDefault(s => s.Key == key);

    public static IReadOnlyList<IndexGridColumnState> OrderForSave(IEnumerable<IndexGridColumnEntry> entries) =>
        entries.OrderBy(entry => entry.DisplayIndex)
               .Select(entry => new IndexGridColumnState(entry.Key, entry.Width, entry.Visible))
               .ToList();

    public static IReadOnlyList<IndexGridColumnState> SetVisible(
        IReadOnlyList<IndexGridColumnState> states, string key, bool visible)
    {
        ArgumentNullException.ThrowIfNull(states);
        if (Find(key) is null) return states.ToList();
        return states
            .Select(state => string.Equals(state.Key, key, StringComparison.Ordinal)
                ? state with { Visible = visible }
                : state)
            .ToList();
    }

    public static double ClampRowHeight(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
            return RowHeightMedium;
        return Math.Clamp(value, MinRowHeight, MaxRowHeight);
    }

    internal static string Serialize(IReadOnlyList<IndexGridColumnState> states)
    {
        var items = states.Select(s => new ColumnDto
        {
            key = s.Key,
            width = Math.Round(s.Width, 1),
            visible = s.Visible,
        });
        return JsonSerializer.Serialize(items.ToArray(), JsonOptions);
    }

    internal static IReadOnlyList<IndexGridColumnState> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DefaultStates;

        ColumnDto[]? items;
        try
        {
            items = JsonSerializer.Deserialize<ColumnDto[]>(raw, JsonOptions);
        }
        catch
        {
            return DefaultStates;
        }

        if (items is null || items.Length == 0) return DefaultStates;

        var result = new List<IndexGridColumnState>(Defaults.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            var key = item.key?.Trim();
            if (string.IsNullOrEmpty(key)) continue;
            if (!seen.Add(key)) continue;
            var spec = Find(key);
            if (spec is null) continue;
            var width = double.IsNaN(item.width) || double.IsInfinity(item.width) || item.width <= 0
                ? spec.DefaultWidth
                : item.width;
            result.Add(new IndexGridColumnState(key, Math.Clamp(width, spec.MinWidth, 800), item.visible));
        }

        foreach (var spec in Defaults)
        {
            if (seen.Contains(spec.Key)) continue;
            result.Add(new IndexGridColumnState(spec.Key, spec.DefaultWidth, true));
        }

        return result.Count == 0 ? DefaultStates : result;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class ColumnDto
    {
        public string? key { get; set; }
        public double width { get; set; }
        public bool visible { get; set; } = true;
    }
}
