using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace IdeviceIndex;

public enum IndexSort
{
    Title,
    BundleId,
    Version,
    MinOs,
    DeviceFamily,
    Size,
    Category,
}

public sealed record IndexQuery(
    string Text,
    int DeviceMask,
    int MaxMinOs,
    IndexSort Sort,
    bool Descending,
    MinOsOperator MinOsOp = MinOsOperator.LessOrEqual,
    IndexSearchScope? SearchScope = null,
    IndexSearchMatchMode MatchMode = IndexSearchMatchMode.Contains,
    IndexAdvancedFilterState? AdvancedFilter = null);

public sealed record SourceRow(string SourceId, string Url, string Page, long Size, string? Sha256 = null, bool SizeExact = true);

public sealed record DescriptionRow(string Language, string Text);

/// <summary>
/// Read-only index database. Every method opens its own pooled read-only connection,
/// so background ThreadPool calls are safe and never touch the UI thread.
/// </summary>
public sealed class IndexDatabase
{
    private readonly string _connectionString;
    private int _sourcesHasSha256 = -1;

    public string Path { get; }

    public IndexDatabase(string path)
    {
        Path = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragmaCmd = connection.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA temp_store=MEMORY;";
        pragmaCmd.ExecuteNonQuery();
        return connection;
    }

    public string? GetMeta(string key)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    public long Count(IndexQuery query)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM entries e " + BuildWhere(cmd, query);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public (long TotalEntries, long TotalGroups) CountGrouped(IndexQuery query)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*), count(DISTINCT e.bundle_lower) FROM entries e " + BuildWhere(cmd, query);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (0, 0);
        var entries = reader.IsDBNull(0) ? 0L : reader.GetInt64(0);
        var groups = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
        return (entries, groups);
    }

    public List<IndexRow> Search(IndexQuery query, int limit, int offset = 0, bool includeSourceCount = true, bool skipSqlOrder = false)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        var effectiveLimit = limit <= 0 ? -1 : limit;
        var sourceCountExpr = includeSourceCount
            ? "(SELECT count(*) FROM sources s WHERE s.entry_id = e.id) AS source_count"
            : "0 AS source_count";
        var orderClause = skipSqlOrder ? "" : OrderBy(query, "e");
        cmd.CommandText = $"""
            SELECT e.id, e.bundle_id, e.title, e.version, e.min_os, e.size, e.size_exact,
                   e.device_mask, e.icon, e.category, e.subgenre, e.developer,
                   {sourceCountExpr}
            FROM entries e
            {BuildWhere(cmd, query)}
            {orderClause}
            LIMIT $limit OFFSET $offset
            """;
        cmd.Parameters.AddWithValue("$limit", effectiveLimit);
        cmd.Parameters.AddWithValue("$offset", Math.Max(0, offset));

        var rows = new List<IndexRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(ReadRow(reader));
        return rows;
    }

    public List<IndexRow> SearchGrouped(IndexQuery query, int limit, int offset = 0, bool includeSourceCount = true, bool skipSqlOrder = false)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        var effectiveLimit = limit <= 0 ? -1 : limit;
        var sourceCountExpr = includeSourceCount
            ? "(SELECT count(*) FROM sources s WHERE s.entry_id = r.id) AS source_count"
            : "0 AS source_count";
        var orderClause = skipSqlOrder ? "" : OrderBy(query, "r");
        cmd.CommandText = $"""
            WITH filtered AS (
                SELECT e.id, e.bundle_id, e.bundle_lower, e.title, e.version, e.min_os, e.min_os_num,
                       e.size, e.size_exact, e.device_mask, e.icon, e.category, e.subgenre, e.developer
                FROM entries e
                {BuildWhere(cmd, query)}
            ),
            grouped AS (
                SELECT bundle_lower,
                       COUNT(*) AS grp_count,
                       MIN(NULLIF(min_os_num, 0)) AS min_os_min,
                       MAX(min_os_num) AS min_os_max,
                       MIN(NULLIF(size, 0)) AS size_min,
                       MAX(size) AS size_max,
                       GROUP_CONCAT(NULLIF(version, ''), char(31)) AS ver_list
                FROM filtered
                GROUP BY bundle_lower
            ),
            ranked AS (
                SELECT f.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY f.bundle_lower
                           ORDER BY (CASE WHEN f.title != '' THEN 0 ELSE 1 END),
                                    (CASE WHEN f.icon != '' THEN 0 ELSE 1 END),
                                    f.size DESC,
                                    f.version DESC,
                                    f.id ASC
                       ) AS rn
                FROM filtered f
            )
            SELECT r.id, r.bundle_id, r.title, r.version, r.min_os, r.size, r.size_exact,
                   r.device_mask, r.icon, r.category, r.subgenre, r.developer,
                   {sourceCountExpr},
                   g.grp_count, g.min_os_min, g.min_os_max, g.size_min, g.size_max, g.ver_list,
                   r.bundle_lower
            FROM ranked r
            JOIN grouped g ON g.bundle_lower = r.bundle_lower
            WHERE r.rn = 1
            {orderClause}
            LIMIT $limit OFFSET $offset
            """;
        cmd.Parameters.AddWithValue("$limit", effectiveLimit);
        cmd.Parameters.AddWithValue("$offset", Math.Max(0, offset));

        var rows = new List<IndexRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(ReadGroupedRow(reader));
        return rows;
    }

    public List<IndexRow> GetGroupMembers(IndexQuery query, string bundleLower)
    {
        if (string.IsNullOrWhiteSpace(bundleLower)) return [];

        using var connection = Open();
        using var cmd = connection.CreateCommand();
        var where = BuildWhere(cmd, query);
        cmd.CommandText = $"""
            SELECT e.id, e.bundle_id, e.title, e.version, e.min_os, e.size, e.size_exact,
                   e.device_mask, e.icon, e.category, e.subgenre, e.developer,
                   (SELECT count(*) FROM sources s WHERE s.entry_id = e.id) AS source_count
            FROM entries e
            {where} AND e.bundle_lower = $groupBundleLower
            {OrderBy(query, "e")}
            """;
        cmd.Parameters.AddWithValue("$groupBundleLower", bundleLower.Trim().ToLowerInvariant());

        var rows = new List<IndexRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(ReadRow(reader, isGroupMember: true, groupKey: bundleLower));
        return rows;
    }

    public IndexRow? GetById(string entryId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT e.id, e.bundle_id, e.title, e.version, e.min_os, e.size, e.size_exact,
                   e.device_mask, e.icon, e.category, e.subgenre, e.developer,
                   (SELECT count(*) FROM sources s WHERE s.entry_id = e.id) AS source_count
            FROM entries e
            WHERE e.id = $id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$id", entryId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    public List<IndexRow> GetByIds(IReadOnlyList<string> entryIds)
    {
        var rows = new List<IndexRow>(entryIds.Count);
        if (entryIds.Count == 0) return rows;

        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT e.id, e.bundle_id, e.title, e.version, e.min_os, e.size, e.size_exact,
                   e.device_mask, e.icon, e.category, e.subgenre, e.developer,
                   (SELECT count(*) FROM sources s WHERE s.entry_id = e.id) AS source_count
            FROM entries e
            WHERE e.id = $id
            LIMIT 1
            """;
        var idParam = cmd.Parameters.Add("$id", SqliteType.Text);
        foreach (var entryId in entryIds)
        {
            if (string.IsNullOrEmpty(entryId)) continue;
            idParam.Value = entryId;
            using var reader = cmd.ExecuteReader();
            if (reader.Read()) rows.Add(ReadRow(reader));
        }
        return rows;
    }

    private static IndexRow ReadRow(SqliteDataReader reader, bool isGroupMember = false, string groupKey = "") => new()
    {
        Id = reader.GetString(0),
        BundleId = reader.GetString(1),
        Title = reader.IsDBNull(2) ? "" : reader.GetString(2),
        Version = reader.IsDBNull(3) ? "" : reader.GetString(3),
        MinOs = reader.IsDBNull(4) ? "" : reader.GetString(4),
        Size = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
        SizeExact = !reader.IsDBNull(6) && reader.GetInt64(6) != 0,
        DeviceMask = reader.IsDBNull(7) ? 0 : Convert.ToInt32(reader.GetValue(7)),
        IconPath = reader.IsDBNull(8) ? "" : reader.GetString(8),
        Category = reader.IsDBNull(9) ? "" : reader.GetString(9),
        Subgenre = reader.IsDBNull(10) ? "" : reader.GetString(10),
        Developer = reader.IsDBNull(11) ? "" : reader.GetString(11),
        SourceCount = reader.IsDBNull(12) ? 0 : Convert.ToInt32(reader.GetValue(12)),
        IsGroupMember = isGroupMember,
        GroupKey = groupKey,
    };

    private static IndexRow ReadGroupedRow(SqliteDataReader reader)
    {
        var count = reader.IsDBNull(13) ? 1 : Convert.ToInt32(reader.GetValue(13));
        if (count <= 1) return ReadRow(reader);

        var minOsMin = reader.IsDBNull(14) ? 0 : Convert.ToInt32(reader.GetValue(14));
        var minOsMax = reader.IsDBNull(15) ? 0 : Convert.ToInt32(reader.GetValue(15));
        var sizeMin = reader.IsDBNull(16) ? 0L : reader.GetInt64(16);
        var sizeMax = reader.IsDBNull(17) ? 0L : reader.GetInt64(17);
        var verList = reader.IsDBNull(18) ? "" : reader.GetString(18);
        var bundleLower = reader.IsDBNull(19) ? "" : reader.GetString(19);
        var sizeExact = !reader.IsDBNull(6) && reader.GetInt64(6) != 0;

        string? rangeMinOs = null;
        if (minOsMin > 0 && minOsMax > 0)
        {
            var sMin = IosVersionCatalog.FormatVersionNum(minOsMin);
            var sMax = IosVersionCatalog.FormatVersionNum(minOsMax);
            rangeMinOs = minOsMin == minOsMax ? sMin : $"{sMin} – {sMax}";
        }

        string? rangeSize = null;
        if (sizeMax > 0)
        {
            rangeSize = sizeMin <= 0 || sizeMin == sizeMax
                ? IndexRow.FormatSize(sizeMax, sizeExact)
                : $"{IndexRow.FormatSize(sizeMin, sizeExact)} – {IndexRow.FormatSize(sizeMax, sizeExact)}";
        }

        var rangeVer = ComputeVersionRange(verList);

        return new IndexRow
        {
            Id = reader.GetString(0),
            BundleId = reader.GetString(1),
            Title = reader.IsDBNull(2) ? "" : reader.GetString(2),
            Version = reader.IsDBNull(3) ? "" : reader.GetString(3),
            MinOs = reader.IsDBNull(4) ? "" : reader.GetString(4),
            Size = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
            SizeExact = sizeExact,
            DeviceMask = reader.IsDBNull(7) ? 0 : Convert.ToInt32(reader.GetValue(7)),
            IconPath = reader.IsDBNull(8) ? "" : reader.GetString(8),
            Category = reader.IsDBNull(9) ? "" : reader.GetString(9),
            Subgenre = reader.IsDBNull(10) ? "" : reader.GetString(10),
            Developer = reader.IsDBNull(11) ? "" : reader.GetString(11),
            SourceCount = reader.IsDBNull(12) ? 0 : Convert.ToInt32(reader.GetValue(12)),
            IsGroup = true,
            IsGroupEnd = true,
            GroupKey = bundleLower,
            GroupCount = count,
            RangeVersionText = rangeVer,
            RangeMinOsText = rangeMinOs,
            RangeSizeText = rangeSize,
        };
    }

    private static string? ComputeVersionRange(string verList)
    {
        if (string.IsNullOrWhiteSpace(verList)) return null;

        string? minVer = null;
        string? maxVer = null;
        foreach (var raw in verList.Split('\u001F', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;

            if (minVer is null || CompareVersionStrings(trimmed, minVer) < 0)
                minVer = trimmed;
            if (maxVer is null || CompareVersionStrings(trimmed, maxVer) > 0)
                maxVer = trimmed;
        }

        if (maxVer is null) return null;
        if (minVer is null || string.Equals(minVer, maxVer, StringComparison.OrdinalIgnoreCase))
            return maxVer;
        return $"{minVer} – {maxVer}";
    }

    internal static int CompareVersionStrings(string a, string b)
    {
        var i = 0;
        var j = 0;
        while (i < a.Length && j < b.Length)
        {
            var ca = a[i];
            var cb = b[j];
            if (char.IsAsciiDigit(ca) && char.IsAsciiDigit(cb))
            {
                var startA = i;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                var startB = j;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;

                var segA = a.AsSpan(startA, i - startA).TrimStart('0');
                var segB = b.AsSpan(startB, j - startB).TrimStart('0');
                if (segA.Length != segB.Length)
                    return segA.Length.CompareTo(segB.Length);
                var cmpDigits = segA.SequenceCompareTo(segB);
                if (cmpDigits != 0) return cmpDigits;
                continue;
            }

            if (IsVersionSeparator(ca) && IsVersionSeparator(cb))
            {
                i++;
                j++;
                continue;
            }

            var cmp = char.ToUpperInvariant(ca).CompareTo(char.ToUpperInvariant(cb));
            if (cmp != 0) return cmp;
            i++;
            j++;
        }

        if (i < a.Length && IsVersionSeparator(a[i]))
        {
            var rest = a.AsSpan(i + 1);
            if (rest.SequenceEqual("0")) return 0;
        }
        if (j < b.Length && IsVersionSeparator(b[j]))
        {
            var rest = b.AsSpan(j + 1);
            if (rest.SequenceEqual("0")) return 0;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    private static bool IsVersionSeparator(char c) => c is '.' or '-' or '_';

    public List<DescriptionRow> GetDescriptions(string entryId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT lang, text FROM descriptions WHERE entry_id = $id ORDER BY lang";
        cmd.Parameters.AddWithValue("$id", entryId);
        var rows = new List<DescriptionRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new DescriptionRow(reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    public List<SourceRow> GetSources(string entryId)
    {
        using var connection = Open();
        var hasSha256 = HasSourcesSha256(connection);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = hasSha256
            ? """
              SELECT s.source_id, s.url, s.page, s.size, s.sha256, COALESCE(e.size_exact, 1)
              FROM sources s
              LEFT JOIN entries e ON e.id = s.entry_id
              WHERE s.entry_id = $id ORDER BY s.id
              """
            : """
              SELECT s.source_id, s.url, s.page, s.size, COALESCE(e.size_exact, 1)
              FROM sources s
              LEFT JOIN entries e ON e.id = s.entry_id
              WHERE s.entry_id = $id ORDER BY s.id
              """;
        cmd.Parameters.AddWithValue("$id", entryId);
        var rows = new List<SourceRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string? sha256 = null;
            if (hasSha256 && !reader.IsDBNull(4))
            {
                var rawSha = reader.GetString(4);
                if (!string.IsNullOrWhiteSpace(rawSha))
                    sha256 = rawSha.Trim().ToLowerInvariant();
            }
            var exactIdx = hasSha256 ? 5 : 4;
            var sizeExact = reader.IsDBNull(exactIdx) || Convert.ToInt64(reader.GetValue(exactIdx)) != 0;
            rows.Add(new SourceRow(
                reader.IsDBNull(0) ? "" : reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                sha256,
                sizeExact));
        }
        return rows;
    }

    private bool HasSourcesSha256(SqliteConnection connection)
    {
        var cached = Volatile.Read(ref _sourcesHasSha256);
        if (cached >= 0) return cached == 1;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM pragma_table_info('sources') WHERE name = 'sha256' LIMIT 1";
        var exists = cmd.ExecuteScalar() is not null;
        Volatile.Write(ref _sourcesHasSha256, exists ? 1 : 0);
        return exists;
    }

    private static string BuildWhere(SqliteCommand cmd, IndexQuery query)
    {
        var sb = new StringBuilder("WHERE 1=1");
        var paramIdx = 0;

        string NextParam(object value)
        {
            var name = $"$p{paramIdx++.ToString(CultureInfo.InvariantCulture)}";
            cmd.Parameters.AddWithValue(name, value);
            return name;
        }

        var text = query.Text.Trim();
        var scope = query.SearchScope ?? new IndexSearchScope();
        if (text.Length > 0)
        {
            var effectiveScope = scope.IsEmpty ? new IndexSearchScope() : scope;
            var clause = BuildTextSearchClause(text, effectiveScope, query.MatchMode, NextParam);
            if (!string.IsNullOrEmpty(clause))
                sb.Append(" AND (").Append(clause).Append(')');
        }

        if (query.DeviceMask != 0)
        {
            var maskParam = NextParam(query.DeviceMask);
            sb.Append($" AND (e.device_mask & {maskParam}) != 0");
        }

        if (query.MaxMinOs > 0)
        {
            var osParam = NextParam(query.MaxMinOs);
            var sqlOp = IosVersionCatalog.SqlOperator(query.MinOsOp);
            sb.Append($" AND e.min_os_num > 0 AND e.min_os_num {sqlOp} {osParam}");
        }

        if (query.AdvancedFilter is { Enabled: true } adv)
        {
            foreach (var group in adv.Groups)
            {
                var preds = new List<string>();
                foreach (var cond in group.Conditions)
                {
                    if (!cond.IsActive) continue;
                    var sql = BuildConditionSql(cond, NextParam);
                    if (!string.IsNullOrEmpty(sql))
                        preds.Add(sql);
                }

                if (preds.Count == 0) continue;
                var joiner = group.Join == IndexFilterJoin.Or ? " OR " : " AND ";
                sb.Append(" AND (").Append(string.Join(joiner, preds)).Append(')');
            }
        }

        return sb.ToString();
    }

    private static string BuildTextSearchClause(
        string text,
        IndexSearchScope scope,
        IndexSearchMatchMode mode,
        Func<object, string> addParam)
    {
        var clauses = new List<string>(8);
        var exactParam = mode == IndexSearchMatchMode.Exact ? addParam(text) : null;
        var exactLowerParam = mode == IndexSearchMatchMode.Exact ? addParam(text.ToLowerInvariant()) : null;
        var likePattern = mode switch
        {
            IndexSearchMatchMode.Prefix => EscapeLike(text) + "%",
            IndexSearchMatchMode.Exact => "",
            _ => "%" + EscapeLike(text) + "%",
        };
        var likeParam = mode == IndexSearchMatchMode.Exact ? null : addParam(likePattern);

        string MatchCol(string columnExpr, bool isLowerCol = false)
        {
            if (mode == IndexSearchMatchMode.Exact)
                return isLowerCol
                    ? $"{columnExpr} = {exactLowerParam}"
                    : $"{columnExpr} = {exactParam} COLLATE NOCASE";
            return $"{columnExpr} LIKE {likeParam} ESCAPE '\\'";
        }

        if (scope.IsBundleId) clauses.Add(MatchCol("e.bundle_lower", isLowerCol: true));
        if (scope.IsTitle) clauses.Add(MatchCol("e.title"));
        if (scope.IsVersion) clauses.Add(MatchCol("e.version"));
        if (scope.IsMinOs) clauses.Add(MatchCol("e.min_os"));
        if (scope.IsCategory) clauses.Add($"({MatchCol("e.category")} OR {MatchCol("e.subgenre")})");
        if (scope.IsDeveloper) clauses.Add(MatchCol("e.developer"));
        if (scope.IsDescription)
            clauses.Add($"EXISTS (SELECT 1 FROM descriptions d WHERE d.entry_id = e.id AND {MatchCol("d.text")})");
        if (scope.IsSource)
            clauses.Add($"EXISTS (SELECT 1 FROM sources s WHERE s.entry_id = e.id AND ({MatchCol("s.source_id")} OR {MatchCol("s.url")}))");

        return string.Join(" OR ", clauses);
    }

    private static string BuildConditionSql(IndexAdvancedFilterCondition cond, Func<object, string> addParam)
    {
        string MatchTextCol(string colExpr, bool isLowerCol = false)
        {
            var val = cond.Text.Trim();
            if (cond.TextMode == IndexTextFilterMode.Exact)
            {
                var p = addParam(isLowerCol ? val.ToLowerInvariant() : val);
                return isLowerCol ? $"{colExpr} = {p}" : $"{colExpr} = {p} COLLATE NOCASE";
            }
            var lp = addParam("%" + EscapeLike(val) + "%");
            return $"{colExpr} LIKE {lp} ESCAPE '\\'";
        }

        switch (cond.Column)
        {
            case IndexFilterColumn.Title:
                return MatchTextCol("e.title");
            case IndexFilterColumn.BundleId:
                return MatchTextCol("e.bundle_lower", isLowerCol: true);
            case IndexFilterColumn.Category:
                return $"({MatchTextCol("e.category")} OR {MatchTextCol("e.subgenre")})";
            case IndexFilterColumn.Developer:
                return MatchTextCol("e.developer");
            case IndexFilterColumn.Source:
                return $"EXISTS (SELECT 1 FROM sources s WHERE s.entry_id = e.id AND ({MatchTextCol("s.source_id")} OR {MatchTextCol("s.url")}))";
            case IndexFilterColumn.Description:
                return $"EXISTS (SELECT 1 FROM descriptions d WHERE d.entry_id = e.id AND {MatchTextCol("d.text")})";
            case IndexFilterColumn.Version:
            {
                var hasRange = !string.IsNullOrWhiteSpace(cond.Min) || !string.IsNullOrWhiteSpace(cond.Max);
                if (!hasRange)
                {
                    var p = addParam(cond.Text.Trim());
                    return $"e.version = {p} COLLATE NOCASE";
                }
                var parts = new List<string>(2);
                if (!string.IsNullOrWhiteSpace(cond.Min))
                {
                    var minP = addParam(cond.Min.Trim());
                    parts.Add($"e.version >= {minP} COLLATE NOCASE");
                }
                if (!string.IsNullOrWhiteSpace(cond.Max))
                {
                    var maxP = addParam(cond.Max.Trim());
                    parts.Add($"e.version <= {maxP} COLLATE NOCASE");
                }
                return "(" + string.Join(" AND ", parts) + ")";
            }
            case IndexFilterColumn.MinOs:
            {
                var hasRange = !string.IsNullOrWhiteSpace(cond.Min) || !string.IsNullOrWhiteSpace(cond.Max);
                if (!hasRange)
                {
                    if (IosVersionCatalog.TryParseVersionNum(cond.Text, out var exactNum))
                    {
                        var p = addParam(exactNum);
                        return $"e.min_os_num = {p}";
                    }
                    var tp = addParam(cond.Text.Trim());
                    return $"e.min_os = {tp} COLLATE NOCASE";
                }
                var parts = new List<string>(3) { "e.min_os_num > 0" };
                if (!string.IsNullOrWhiteSpace(cond.Min))
                {
                    if (!IosVersionCatalog.TryParseVersionNum(cond.Min, out var minNum))
                        return "1=0";
                    parts.Add($"e.min_os_num >= {addParam(minNum)}");
                }
                if (!string.IsNullOrWhiteSpace(cond.Max))
                {
                    if (!IosVersionCatalog.TryParseVersionNum(cond.Max, out var maxNum))
                        return "1=0";
                    parts.Add($"e.min_os_num <= {addParam(maxNum)}");
                }
                return "(" + string.Join(" AND ", parts) + ")";
            }
            case IndexFilterColumn.SizeMb:
            {
                var parts = new List<string>(3) { "e.size > 0" };
                if (!string.IsNullOrWhiteSpace(cond.Min))
                {
                    if (!IndexAdvancedFilterState.TryParseSizeBytes(cond.Min, out var minBytes))
                        return "1=0";
                    parts.Add($"e.size >= {addParam(minBytes)}");
                }
                if (!string.IsNullOrWhiteSpace(cond.Max))
                {
                    if (!IndexAdvancedFilterState.TryParseSizeBytes(cond.Max, out var maxBytes))
                        return "1=0";
                    parts.Add($"e.size <= {addParam(maxBytes)}");
                }
                return "(" + string.Join(" AND ", parts) + ")";
            }
            case IndexFilterColumn.DeviceFamily:
            {
                var mask = IndexAdvancedFilterState.ParseDeviceMask(cond.Selected);
                if (mask == 0) return "";
                var mp = addParam(mask);
                return $"(e.device_mask & {mp}) = {mp}";
            }
            default:
                return "";
        }
    }

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string OrderBy(IndexQuery query, string alias)
    {
        var column = query.Sort switch
        {
            IndexSort.BundleId => $"{alias}.bundle_lower",
            IndexSort.Version => $"{alias}.version COLLATE NOCASE",
            IndexSort.MinOs => $"{alias}.min_os_num",
            IndexSort.DeviceFamily => $"{alias}.device_mask",
            IndexSort.Size => $"{alias}.size",
            IndexSort.Category => $"{alias}.category COLLATE NOCASE, {alias}.subgenre COLLATE NOCASE",
            _ => $"{alias}.title COLLATE NOCASE",
        };
        var dir = query.Descending ? " DESC" : " ASC";
        if (query.Sort == IndexSort.Category)
            return $"ORDER BY {alias}.category COLLATE NOCASE{dir}, {alias}.subgenre COLLATE NOCASE{dir}, {alias}.title COLLATE NOCASE ASC, {alias}.id ASC";
        return $"ORDER BY {column}{dir}, {alias}.id ASC";
    }
}
