using System.Globalization;

namespace IdeviceIndex;

public enum MinOsOperator
{
    LessOrEqual,
    Less,
    Equal,
    GreaterOrEqual,
    Greater,
}

public sealed record IosVersionGroup(string MajorMinor, IReadOnlyList<string> Revisions);

/// <summary>
/// Built-in catalog of 93 legacy iOS versions (2.0–10.3.4) plus version comparison operators
/// and numeric parser (zero external file dependency).
/// </summary>
internal static class IosVersionCatalog
{
    public static readonly IReadOnlyList<string> AllVersions =
    [
        "2.0", "2.0.1", "2.0.2",
        "2.1", "2.1.1",
        "2.2", "2.2.1",
        "3.0", "3.0.1",
        "3.1", "3.1.1", "3.1.2", "3.1.3",
        "3.2", "3.2.1", "3.2.2",
        "4.0", "4.0.1", "4.0.2",
        "4.1",
        "4.2.1", "4.2.6", "4.2.7", "4.2.8", "4.2.9", "4.2.10",
        "4.3", "4.3.1", "4.3.2", "4.3.3", "4.3.4", "4.3.5",
        "5.0", "5.0.1",
        "5.1", "5.1.1",
        "6.0", "6.0.1", "6.0.2",
        "6.1", "6.1.1", "6.1.2", "6.1.3", "6.1.4", "6.1.5", "6.1.6",
        "7.0", "7.0.1", "7.0.2", "7.0.3", "7.0.4", "7.0.5", "7.0.6",
        "7.1", "7.1.1", "7.1.2",
        "8.0", "8.0.1", "8.0.2",
        "8.1", "8.1.1", "8.1.2", "8.1.3",
        "8.2",
        "8.3",
        "8.4", "8.4.1",
        "9.0", "9.0.1", "9.0.2",
        "9.1",
        "9.2", "9.2.1",
        "9.3", "9.3.1", "9.3.2", "9.3.3", "9.3.4", "9.3.5", "9.3.6",
        "10.0", "10.0.1", "10.0.2", "10.0.3",
        "10.1", "10.1.1",
        "10.2", "10.2.1",
        "10.3", "10.3.1", "10.3.2", "10.3.3", "10.3.4",
    ];

    public static readonly IReadOnlyList<IosVersionGroup> Groups = BuildGroups();

    private static IReadOnlyList<IosVersionGroup> BuildGroups()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var version in AllVersions)
        {
            var firstDot = version.IndexOf('.');
            var secondDot = firstDot >= 0 ? version.IndexOf('.', firstDot + 1) : -1;
            var majorMinor = secondDot >= 0 ? version[..secondDot] : version;

            if (!map.TryGetValue(majorMinor, out var revisions))
            {
                revisions = [];
                map[majorMinor] = revisions;
                order.Add(majorMinor);
            }

            if (secondDot >= 0)
                revisions.Add(version);
        }

        return order.Select(mm => new IosVersionGroup(mm, map[mm].AsReadOnly())).ToList().AsReadOnly();
    }

    public static string SqlOperator(MinOsOperator op) => op switch
    {
        MinOsOperator.Less => "<",
        MinOsOperator.Equal => "=",
        MinOsOperator.GreaterOrEqual => ">=",
        MinOsOperator.Greater => ">",
        _ => "<=",
    };

    public static string Symbol(MinOsOperator op) => op switch
    {
        MinOsOperator.Less => "<",
        MinOsOperator.Equal => "=",
        MinOsOperator.GreaterOrEqual => "≥",
        MinOsOperator.Greater => ">",
        _ => "≤",
    };

    public static MinOsOperator ParseOperator(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return MinOsOperator.LessOrEqual;
        return raw.Trim() switch
        {
            "<" or nameof(MinOsOperator.Less) => MinOsOperator.Less,
            "=" or "==" or nameof(MinOsOperator.Equal) => MinOsOperator.Equal,
            ">=" or "≥" or nameof(MinOsOperator.GreaterOrEqual) => MinOsOperator.GreaterOrEqual,
            ">" or nameof(MinOsOperator.Greater) => MinOsOperator.Greater,
            _ => MinOsOperator.LessOrEqual,
        };
    }

    public static bool TryParseVersionNum(string? raw, out int versionNum)
    {
        versionNum = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var trimmed = raw.Trim();
        var parts = trimmed.Split('.');
        if (parts.Length is < 1 or > 3) return false;

        Span<int> nums = stackalloc int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) return false;
            foreach (var ch in parts[i])
            {
                if (ch is < '0' or > '9') return false;
            }
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var seg) ||
                seg < 0 || seg > 99)
            {
                return false;
            }
            nums[i] = seg;
        }

        var value = nums[0] * 10000 + nums[1] * 100 + nums[2];
        if (value <= 0) return false;

        versionNum = value;
        return true;
    }

    public static string FormatVersionNum(int versionNum)
    {
        if (versionNum <= 0) return "";
        var major = versionNum / 10000;
        var minor = (versionNum / 100) % 100;
        var patch = versionNum % 100;
        return patch == 0
            ? $"{major.ToString(CultureInfo.InvariantCulture)}.{minor.ToString(CultureInfo.InvariantCulture)}"
            : $"{major.ToString(CultureInfo.InvariantCulture)}.{minor.ToString(CultureInfo.InvariantCulture)}.{patch.ToString(CultureInfo.InvariantCulture)}";
    }
}
