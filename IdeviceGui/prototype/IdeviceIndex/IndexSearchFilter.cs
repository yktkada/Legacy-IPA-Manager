using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IdeviceIndex;

public enum IndexSearchField
{
    Title,
    BundleId,
    Version,
    MinOs,
    Category,
    Developer,
    Description,
    Source,
}

public enum IndexSearchMatchMode
{
    Contains,
    Prefix,
    Exact,
}

public sealed class IndexSearchScope : INotifyPropertyChanged
{
    public const string EmptyToken = "none";

    private bool _title = true;
    private bool _bundleId = true;
    private bool _version;
    private bool _minOs;
    private bool _category;
    private bool _developer;
    private bool _description;
    private bool _source;

    public bool IsTitle { get => _title; set => SetField(ref _title, value); }
    public bool IsBundleId { get => _bundleId; set => SetField(ref _bundleId, value); }
    public bool IsVersion { get => _version; set => SetField(ref _version, value); }
    public bool IsMinOs { get => _minOs; set => SetField(ref _minOs, value); }
    public bool IsCategory { get => _category; set => SetField(ref _category, value); }
    public bool IsDeveloper { get => _developer; set => SetField(ref _developer, value); }
    public bool IsDescription { get => _description; set => SetField(ref _description, value); }
    public bool IsSource { get => _source; set => SetField(ref _source, value); }

    public bool IsEmpty => !(_title || _bundleId || _version || _minOs ||
                             _category || _developer || _description || _source);

    public IEnumerable<IndexSearchField> Fields
    {
        get
        {
            if (_title) yield return IndexSearchField.Title;
            if (_bundleId) yield return IndexSearchField.BundleId;
            if (_version) yield return IndexSearchField.Version;
            if (_minOs) yield return IndexSearchField.MinOs;
            if (_category) yield return IndexSearchField.Category;
            if (_developer) yield return IndexSearchField.Developer;
            if (_description) yield return IndexSearchField.Description;
            if (_source) yield return IndexSearchField.Source;
        }
    }

    public bool Includes(IndexSearchField field) => field switch
    {
        IndexSearchField.Title => _title,
        IndexSearchField.BundleId => _bundleId,
        IndexSearchField.Version => _version,
        IndexSearchField.MinOs => _minOs,
        IndexSearchField.Category => _category,
        IndexSearchField.Developer => _developer,
        IndexSearchField.Description => _description,
        IndexSearchField.Source => _source,
        _ => false,
    };

    public void Set(IndexSearchField field, bool value)
    {
        switch (field)
        {
            case IndexSearchField.Title: IsTitle = value; break;
            case IndexSearchField.BundleId: IsBundleId = value; break;
            case IndexSearchField.Version: IsVersion = value; break;
            case IndexSearchField.MinOs: IsMinOs = value; break;
            case IndexSearchField.Category: IsCategory = value; break;
            case IndexSearchField.Developer: IsDeveloper = value; break;
            case IndexSearchField.Description: IsDescription = value; break;
            case IndexSearchField.Source: IsSource = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(field), field, null);
        }
    }

    public IndexSearchScope Clone()
    {
        var copy = new IndexSearchScope
        {
            _title = _title,
            _bundleId = _bundleId,
            _version = _version,
            _minOs = _minOs,
            _category = _category,
            _developer = _developer,
            _description = _description,
            _source = _source,
        };
        return copy;
    }

    public static IndexSearchScope CreateEmpty() => new()
    {
        IsTitle = false,
        IsBundleId = false,
    };

    public static string Key(IndexSearchField field) => field switch
    {
        IndexSearchField.Title => "title",
        IndexSearchField.BundleId => "bundleId",
        IndexSearchField.Version => "version",
        IndexSearchField.MinOs => "minOs",
        IndexSearchField.Category => "category",
        IndexSearchField.Developer => "developer",
        IndexSearchField.Description => "description",
        IndexSearchField.Source => "source",
        _ => field.ToString().ToLowerInvariant(),
    };

    public static bool TryParseKey(string? raw, out IndexSearchField field)
    {
        field = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var trimmed = raw.Trim();
        foreach (var candidate in Enum.GetValues<IndexSearchField>())
        {
            if (string.Equals(Key(candidate), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                field = candidate;
                return true;
            }
        }
        return false;
    }

    public JsonArray ToJsonArray()
    {
        var arr = new JsonArray();
        foreach (var field in Fields)
            arr.Add(Key(field));
        return arr;
    }

    public string Serialize() => ToJsonArray().ToJsonString();

    public static IndexSearchScope ParseJson(JsonNode? node)
    {
        if (node is null) return new IndexSearchScope();
        if (node is JsonArray arr)
        {
            if (arr.Count == 0) return CreateEmpty();
            var parsed = CreateEmpty();
            var recognized = false;
            foreach (var item in arr)
            {
                if (item is JsonValue jv && jv.TryGetValue<string>(out var s) && TryParseKey(s, out var field))
                {
                    recognized = true;
                    parsed.Set(field, true);
                }
            }
            return recognized ? parsed : new IndexSearchScope();
        }
        if (node is JsonValue strVal && strVal.TryGetValue<string>(out var raw))
            return Parse(raw);
        return new IndexSearchScope();
    }

    public static IndexSearchScope Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new IndexSearchScope();
        var trimmed = raw.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                var node = JsonNode.Parse(trimmed);
                if (node is JsonArray arr) return ParseJson(arr);
            }
            catch
            {
                return new IndexSearchScope();
            }
        }

        trimmed = trimmed.Trim('"');
        if (string.Equals(trimmed, EmptyToken, StringComparison.OrdinalIgnoreCase))
            return CreateEmpty();

        var parsed = CreateEmpty();
        var recognized = false;
        foreach (var part in trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseKey(part, out var field)) continue;
            recognized = true;
            parsed.Set(field, true);
        }
        return recognized ? parsed : new IndexSearchScope();
    }

    public static string MatchModeKey(IndexSearchMatchMode mode) => mode switch
    {
        IndexSearchMatchMode.Prefix => "prefix",
        IndexSearchMatchMode.Exact => "exact",
        _ => "contains",
    };

    public static IndexSearchMatchMode ParseMatchMode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return IndexSearchMatchMode.Contains;
        return raw.Trim().Trim('"').ToLowerInvariant() switch
        {
            "prefix" => IndexSearchMatchMode.Prefix,
            "exact" => IndexSearchMatchMode.Exact,
            _ => IndexSearchMatchMode.Contains,
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField(ref bool slot, bool value, [CallerMemberName] string? name = null)
    {
        if (slot == value) return;
        slot = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEmpty)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Fields)));
    }
}

public enum IndexFilterColumn
{
    Title,
    BundleId,
    Version,
    MinOs,
    SizeMb,
    DeviceFamily,
    Category,
    Developer,
    Source,
    Description,
}

public enum IndexFilterJoin
{
    And,
    Or,
}

public enum IndexTextFilterMode
{
    Exact,
    Contains,
}

public sealed class IndexAdvancedFilterCondition
{
    public IndexFilterColumn Column { get; set; } = IndexFilterColumn.Title;
    public bool Enabled { get; set; } = true;
    public IndexTextFilterMode TextMode { get; set; } = IndexTextFilterMode.Contains;
    public string Text { get; set; } = "";
    public string Min { get; set; } = "";
    public string Max { get; set; } = "";
    public List<string> Selected { get; set; } = [];

    public bool IsBlank => Column switch
    {
        IndexFilterColumn.Title or IndexFilterColumn.BundleId or IndexFilterColumn.Category or
            IndexFilterColumn.Developer or IndexFilterColumn.Source or IndexFilterColumn.Description
            => string.IsNullOrWhiteSpace(Text),
        IndexFilterColumn.Version or IndexFilterColumn.MinOs
            => string.IsNullOrWhiteSpace(Text) && string.IsNullOrWhiteSpace(Min) && string.IsNullOrWhiteSpace(Max),
        IndexFilterColumn.SizeMb
            => string.IsNullOrWhiteSpace(Min) && string.IsNullOrWhiteSpace(Max),
        IndexFilterColumn.DeviceFamily
            => Selected.Count == 0,
        _ => false,
    };

    public bool IsActive => Enabled && !IsBlank;

    public IndexAdvancedFilterCondition Clone() => new()
    {
        Column = Column,
        Enabled = Enabled,
        TextMode = TextMode,
        Text = Text,
        Min = Min,
        Max = Max,
        Selected = Selected.ToList(),
    };

    public JsonObject ToJsonObject() => new()
    {
        ["column"] = Column.ToString(),
        ["enabled"] = Enabled,
        ["textMode"] = TextMode.ToString(),
        ["text"] = Text,
        ["min"] = Min,
        ["max"] = Max,
        ["selected"] = new JsonArray(Selected.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
    };

    public static IndexAdvancedFilterCondition? FromJson(JsonObject obj)
    {
        if (!TryReadEnum<IndexFilterColumn>(obj["column"], out var col))
            return null;

        var cond = new IndexAdvancedFilterCondition
        {
            Column = col,
            Enabled = obj["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var en) ? en : true,
            TextMode = TryReadEnum<IndexTextFilterMode>(obj["textMode"], out var tm) ? tm : IndexTextFilterMode.Contains,
            Text = ReadString(obj["text"]),
            Min = ReadString(obj["min"]),
            Max = ReadString(obj["max"]),
        };
        if (obj["selected"] is JsonArray selArr)
        {
            foreach (var item in selArr)
            {
                if (item is JsonValue sv && sv.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                    cond.Selected.Add(s.Trim());
            }
        }
        return cond;
    }

    private static string ReadString(JsonNode? node)
        => node is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : "";

    private static bool TryReadEnum<TEnum>(JsonNode? node, out TEnum value) where TEnum : struct, Enum
    {
        value = default;
        return node is JsonValue jv &&
               jv.TryGetValue<string>(out var s) &&
               Enum.TryParse(s, ignoreCase: true, out value);
    }
}

public sealed class IndexAdvancedFilterGroup
{
    public IndexFilterJoin Join { get; set; } = IndexFilterJoin.And;
    public List<IndexAdvancedFilterCondition> Conditions { get; set; } = [];

    public IndexAdvancedFilterGroup Clone() => new()
    {
        Join = Join,
        Conditions = Conditions.Select(c => c.Clone()).ToList(),
    };
}

public sealed class IndexAdvancedFilterState
{
    public bool Enabled { get; set; }
    public List<IndexAdvancedFilterGroup> Groups { get; set; } = [];

    public int ActiveConditionCount => Enabled
        ? Groups.Sum(g => g.Conditions.Count(c => c.IsActive))
        : 0;

    public IndexAdvancedFilterState Clone() => new()
    {
        Enabled = Enabled,
        Groups = Groups.Select(g => g.Clone()).ToList(),
    };

    public JsonObject ToJsonObject()
    {
        var groupsArr = new JsonArray();
        foreach (var group in Groups)
        {
            var condArr = new JsonArray();
            foreach (var c in group.Conditions)
                condArr.Add(c.ToJsonObject());

            groupsArr.Add(new JsonObject
            {
                ["join"] = group.Join.ToString(),
                ["conditions"] = condArr,
            });
        }
        return new JsonObject
        {
            ["enabled"] = Enabled,
            ["groups"] = groupsArr,
        };
    }

    public string Serialize() => ToJsonObject().ToJsonString();

    public static IndexAdvancedFilterState ParseJson(JsonNode? node)
    {
        if (node is JsonValue sv && sv.TryGetValue<string>(out var rawStr))
            return Parse(rawStr);
        if (node is not JsonObject obj)
            return new IndexAdvancedFilterState();

        var state = new IndexAdvancedFilterState
        {
            Enabled = obj["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var en) && en,
        };
        if (obj["groups"] is JsonArray groupsArr)
        {
            foreach (var gNode in groupsArr)
            {
                if (gNode is not JsonObject gObj) continue;
                var group = new IndexAdvancedFilterGroup
                {
                    Join = gObj["join"] is JsonValue jv &&
                           jv.TryGetValue<string>(out var js) &&
                           string.Equals(js, nameof(IndexFilterJoin.Or), StringComparison.OrdinalIgnoreCase)
                        ? IndexFilterJoin.Or
                        : IndexFilterJoin.And,
                };
                if (gObj["conditions"] is JsonArray condArr)
                {
                    foreach (var cNode in condArr)
                    {
                        if (cNode is not JsonObject cObj) continue;
                        var parsed = IndexAdvancedFilterCondition.FromJson(cObj);
                        if (parsed is not null) group.Conditions.Add(parsed);
                    }
                }
                if (group.Conditions.Count > 0)
                    state.Groups.Add(group);
            }
        }
        return state;
    }

    public static IndexAdvancedFilterState Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new IndexAdvancedFilterState();
        try
        {
            return ParseJson(JsonNode.Parse(raw));
        }
        catch
        {
            return new IndexAdvancedFilterState();
        }
    }

    internal static int ParseDeviceMask(IReadOnlyList<string> selected)
    {
        var mask = 0;
        foreach (var item in selected)
        {
            if (string.Equals(item, "iPhone", StringComparison.OrdinalIgnoreCase)) mask |= 2;
            else if (string.Equals(item, "iPad", StringComparison.OrdinalIgnoreCase)) mask |= 4;
            else if (string.Equals(item, "AppleTV", StringComparison.OrdinalIgnoreCase)) mask |= 8;
        }
        return mask;
    }

    internal static bool TryParseSizeBytes(string? raw, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var mb) ||
            double.IsNaN(mb) || double.IsInfinity(mb) || mb < 0)
        {
            return false;
        }
        try
        {
            bytes = checked((long)Math.Round(mb * 1024d * 1024d));
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
