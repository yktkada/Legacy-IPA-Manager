using System.IO;
using IdeviceIndex;
using Microsoft.Data.Sqlite;
using Xunit;
using IndexMainWindow = IdeviceIndex.MainWindow;
using IndexStrings = IdeviceIndex.Resources.Strings;

namespace IdeviceGui.Tests;

public sealed class IndexDatabaseTests
{
    private static string CreateSampleDatabase(TestWorkspace ws, bool includeSha256Column = true)
    {
        var dbPath = ws.Full("index.db");
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            var shaCol = includeSha256Column ? ", sha256 TEXT" : "";
            cmd.CommandText = $"""
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
                CREATE TABLE entries (
                    id TEXT PRIMARY KEY,
                    bundle_id TEXT NOT NULL,
                    bundle_lower TEXT NOT NULL,
                    title TEXT,
                    version TEXT,
                    min_os TEXT,
                    min_os_num INTEGER,
                    size INTEGER,
                    size_exact INTEGER,
                    device_mask INTEGER,
                    icon TEXT,
                    category TEXT,
                    subgenre TEXT,
                    developer TEXT
                );
                CREATE TABLE sources (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    entry_id TEXT NOT NULL,
                    source_id TEXT,
                    url TEXT NOT NULL,
                    page TEXT,
                    size INTEGER
                    {shaCol}
                );
                CREATE TABLE descriptions (
                    entry_id TEXT NOT NULL,
                    lang TEXT NOT NULL,
                    text TEXT NOT NULL
                );

                INSERT INTO meta VALUES ('schema_version', '1');
                INSERT INTO meta VALUES ('fts5', '0');

                INSERT INTO entries VALUES ('e1', 'com.alpha.game', 'com.alpha.game', 'Alpha 100% Fun', '2.0', '4.3', 40300, 3000, 1, 2, 'icons/e1.png', 'Games', 'Action', 'DevA');
                INSERT INTO entries VALUES ('e2', 'com.beta_app.tool', 'com.beta_app.tool', 'beta_1 Tool', '1.0', '6.1.3', 60103, 1000, 1, 4, '', 'Utilities', '', 'DevB');
                INSERT INTO entries VALUES ('e3', 'com.gamma.both', 'com.gamma.both', 'Gamma\Slash', '3.0', '8.0', 80000, 5000, 0, 6, '', 'Productivity', '', 'DevC');
                INSERT INTO entries VALUES ('e4', 'com.delta.unknown', 'com.delta.unknown', 'Delta ZeroMinOs', '0.9', '', 0, 2000, 1, 2, '', 'Reference', '', 'DevD');
                INSERT INTO entries VALUES ('e5', 'com.epsilon.plain', 'com.epsilon.plain', 'Alpha 1000 Fun', '1.5', '5.1.1', 50101, 4000, 1, 2, '', 'Games', '', 'DevE');

                INSERT INTO descriptions VALUES ('e1', 'und', 'Und fallback');
                INSERT INTO descriptions VALUES ('e1', 'en', 'English description');
                INSERT INTO descriptions VALUES ('e1', 'zh-Hans', 'Chinese description');
                INSERT INTO descriptions VALUES ('e2', 'und', 'Only und');
                INSERT INTO descriptions VALUES ('e3', 'ja', 'Japanese only');
                """;
            cmd.ExecuteNonQuery();

            using var srcCmd = conn.CreateCommand();
            if (includeSha256Column)
            {
                var sha = new string('A', 64);
                srcCmd.CommandText = $"""
                    INSERT INTO sources (entry_id, source_id, url, page, size, sha256) VALUES ('e1', 'mirror1', 'https://example.invalid/e1.ipa', 'https://example.invalid/p1', 3000, '{sha}');
                    INSERT INTO sources (entry_id, source_id, url, page, size, sha256) VALUES ('e1', 'mirror2', 'https://example.invalid/e1b.ipa', '', 3100, '');
                    INSERT INTO sources (entry_id, source_id, url, page, size, sha256) VALUES ('e2', 'mirror1', 'https://example.invalid/e2.ipa', '', 1000, NULL);
                    """;
            }
            else
            {
                srcCmd.CommandText = """
                    INSERT INTO sources (entry_id, source_id, url, page, size) VALUES ('e1', 'mirror1', 'https://example.invalid/e1.ipa', 'https://example.invalid/p1', 3000);
                    """;
            }
            srcCmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        return dbPath;
    }

    [Fact]
    public void GetMeta_ReadsExistingAndMissingKeys_AndThrowsOnMissingTableOrCorruptFile()
    {
        using var ws = new TestWorkspace("indexdb-meta");
        var dbPath = CreateSampleDatabase(ws);
        var db = new IndexDatabase(dbPath);

        Assert.Equal("1", db.GetMeta("schema_version"));
        Assert.Equal("0", db.GetMeta("fts5"));
        Assert.Null(db.GetMeta("missing_key"));

        // Missing meta table
        var noTablePath = ws.Full("no-table.db");
        using (var conn = new SqliteConnection($"Data Source={noTablePath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE dummy (x INTEGER);";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        var noTableDb = new IndexDatabase(noTablePath);
        Assert.Throws<SqliteException>(() => noTableDb.GetMeta("schema_version"));

        // Corrupt non-SQLite file
        var corruptPath = ws.Full("corrupt.db");
        File.WriteAllText(corruptPath, "not a sqlite file");
        var corruptDb = new IndexDatabase(corruptPath);
        Assert.Throws<SqliteException>(() => corruptDb.GetMeta("schema_version"));
    }

    [Fact]
    public void Search_And_Count_FilterByLikeEscaping_DeviceMask_MaxMinOs_Sort_AndPagination()
    {
        using var ws = new TestWorkspace("indexdb-query");
        var db = new IndexDatabase(CreateSampleDatabase(ws));

        // 1. LIKE escaping for %, _, \ (must match literal characters only, not wildcards)
        var pctQuery = new IndexQuery("100%", 0, 0, IndexSort.Title, false);
        Assert.Equal(1, db.Count(pctQuery));
        Assert.Equal("e1", Assert.Single(db.Search(pctQuery, 50)).Id);

        var underQuery = new IndexQuery("beta_1", 0, 0, IndexSort.Title, false);
        Assert.Equal(1, db.Count(underQuery));
        Assert.Equal("e2", Assert.Single(db.Search(underQuery, 50)).Id);

        var slashQuery = new IndexQuery(@"Gamma\Slash", 0, 0, IndexSort.Title, false);
        Assert.Equal(1, db.Count(slashQuery));
        Assert.Equal("e3", Assert.Single(db.Search(slashQuery, 50)).Id);

        // BundleId search (case-insensitive via bundle_lower)
        var bundleQuery = new IndexQuery("BETA_APP", 0, 0, IndexSort.Title, false);
        Assert.Equal("e2", Assert.Single(db.Search(bundleQuery, 50)).Id);

        // 2. DeviceMask filter: iPhone (2), iPad (4), All (0)
        Assert.Equal(4, db.Count(new IndexQuery("", 2, 0, IndexSort.Title, false))); // e1, e3, e4, e5
        Assert.Equal(2, db.Count(new IndexQuery("", 4, 0, IndexSort.Title, false))); // e2, e3
        Assert.Equal(5, db.Count(new IndexQuery("", 0, 0, IndexSort.Title, false)));

        // 3. MaxMinOs filter excludes min_os_num == 0 (unknown) and values > MaxMinOs
        var ios6Query = new IndexQuery("", 0, 60103, IndexSort.MinOs, false);
        var ios6Rows = db.Search(ios6Query, 50);
        Assert.Equal(["e1", "e5", "e2"], ios6Rows.Select(r => r.Id).ToArray());

        // 4. All 4 sort fields ASC / DESC
        Assert.Equal(
            ["e1", "e5", "e2", "e4", "e3"],
            db.Search(new IndexQuery("", 0, 0, IndexSort.Title, false), 50).Select(r => r.Id).ToArray());
        Assert.Equal(
            ["e3", "e4", "e2", "e5", "e1"],
            db.Search(new IndexQuery("", 0, 0, IndexSort.Title, true), 50).Select(r => r.Id).ToArray());

        Assert.Equal(
            ["e4", "e2", "e5", "e1", "e3"],
            db.Search(new IndexQuery("", 0, 0, IndexSort.Version, false), 50).Select(r => r.Id).ToArray());
        Assert.Equal(
            ["e3", "e1", "e5", "e2", "e4"],
            db.Search(new IndexQuery("", 0, 0, IndexSort.Version, true), 50).Select(r => r.Id).ToArray());

        Assert.Equal(
            ["e4", "e1", "e5", "e2", "e3"],
            db.Search(new IndexQuery("", 0, 0, IndexSort.MinOs, false), 50).Select(r => r.Id).ToArray());
        Assert.Equal(
            ["e3", "e2", "e5", "e1", "e4"],
            db.Search(new IndexQuery("", 0, 0, IndexSort.MinOs, true), 50).Select(r => r.Id).ToArray());

        Assert.Equal(
            ["e2", "e4", "e1", "e5", "e3"],
            db.Search(new IndexQuery("", 0, 0, IndexSort.Size, false), 50).Select(r => r.Id).ToArray());
        Assert.Equal(
            ["e3", "e5", "e1", "e4", "e2"],
            db.Search(new IndexQuery("", 0, 0, IndexSort.Size, true), 50).Select(r => r.Id).ToArray());

        // 5. Pagination (limit + offset) + total count
        var allQuery = new IndexQuery("", 0, 0, IndexSort.Size, false);
        Assert.Equal(5, db.Count(allQuery));
        var page1 = db.Search(allQuery, limit: 2, offset: 0);
        var page2 = db.Search(allQuery, limit: 2, offset: 2);
        var page3 = db.Search(allQuery, limit: 2, offset: 4);
        Assert.Equal(["e2", "e4"], page1.Select(r => r.Id).ToArray());
        Assert.Equal(["e1", "e5"], page2.Select(r => r.Id).ToArray());
        Assert.Equal(["e3"], page3.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void GetById_GetByIds_GetSources_And_DescriptionsFallback_WorkCleanly()
    {
        using var ws = new TestWorkspace("indexdb-details");
        var db = new IndexDatabase(CreateSampleDatabase(ws, includeSha256Column: true));

        var e1 = db.GetById("e1");
        Assert.NotNull(e1);
        Assert.Equal(2, e1!.SourceCount);
        Assert.Equal("Games / Action", e1.CategoryText);
        Assert.Null(db.GetById("does-not-exist"));

        var batch = db.GetByIds(["e3", "missing", "e1", ""]);
        Assert.Equal(["e3", "e1"], batch.Select(r => r.Id).ToArray());
        Assert.Empty(db.GetByIds([]));

        var e1Sources = db.GetSources("e1");
        Assert.Equal(2, e1Sources.Count);
        Assert.Equal(new string('a', 64), e1Sources[0].Sha256);
        Assert.Equal(3000, e1Sources[0].Size);
        Assert.Equal("https://example.invalid/p1", e1Sources[0].Page);
        Assert.Null(e1Sources[1].Sha256);

        Assert.Single(db.GetSources("e2"));
        Assert.Empty(db.GetSources("e3"));

        // Legacy schema without sha256 column
        using var wsLegacy = new TestWorkspace("indexdb-legacy-sources");
        var legacyDb = new IndexDatabase(CreateSampleDatabase(wsLegacy, includeSha256Column: false));
        var legacySrc = Assert.Single(legacyDb.GetSources("e1"));
        Assert.Null(legacySrc.Sha256);
        Assert.Equal(3000, legacySrc.Size);

        // Descriptions + ChooseDescription language fallback
        var d1 = db.GetDescriptions("e1");
        Assert.Equal("Chinese description", IndexMainWindow.ChooseDescription(d1, preferChinese: true));
        Assert.Equal("English description", IndexMainWindow.ChooseDescription(d1, preferChinese: false));

        var d2 = db.GetDescriptions("e2");
        Assert.Equal("Only und", IndexMainWindow.ChooseDescription(d2, preferChinese: true));
        Assert.Equal("Only und", IndexMainWindow.ChooseDescription(d2, preferChinese: false));

        var d3 = db.GetDescriptions("e3");
        Assert.Equal("Japanese only", IndexMainWindow.ChooseDescription(d3, preferChinese: true));

        var d4 = db.GetDescriptions("e4");
        Assert.Empty(d4);
        Assert.Equal(IndexStrings.Detail_NoDescription, IndexMainWindow.ChooseDescription(d4, preferChinese: true));
    }

    private static string CreateGroupedAndFilterDatabase(TestWorkspace ws)
    {
        var dbPath = ws.Full("grouped-filter.db");
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
                CREATE TABLE entries (
                    id TEXT PRIMARY KEY,
                    bundle_id TEXT NOT NULL,
                    bundle_lower TEXT NOT NULL,
                    title TEXT,
                    version TEXT,
                    min_os TEXT,
                    min_os_num INTEGER,
                    size INTEGER,
                    size_exact INTEGER,
                    device_mask INTEGER,
                    icon TEXT,
                    category TEXT,
                    subgenre TEXT,
                    developer TEXT
                );
                CREATE TABLE sources (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    entry_id TEXT NOT NULL,
                    source_id TEXT,
                    url TEXT NOT NULL,
                    page TEXT,
                    size INTEGER,
                    sha256 TEXT
                );
                CREATE TABLE descriptions (
                    entry_id TEXT NOT NULL,
                    lang TEXT NOT NULL,
                    text TEXT NOT NULL
                );
                INSERT INTO meta VALUES ('schema_version', '1');

                -- Group 1: com.demo.alpha (3 entries: 1.0, 2.0, 1.5; min_os 4.3, 6.1.3, 5.1; sizes 1 MB, 3 MB, 2 MB)
                INSERT INTO entries VALUES ('a1', 'com.demo.alpha', 'com.demo.alpha', 'Alpha Old', '1.0', '4.3', 40300, 1048576, 1, 2, '', 'Games', 'Puzzle', 'StudioA');
                INSERT INTO entries VALUES ('a2', 'com.demo.alpha', 'com.demo.alpha', 'Alpha New', '2.0', '6.1.3', 60103, 3145728, 1, 4, '', 'Games', 'Action', 'StudioA');
                INSERT INTO entries VALUES ('a3', 'com.demo.alpha', 'com.demo.alpha', 'Alpha Mid', '1.5', '5.1', 50100, 2097152, 1, 6, '', 'Games', 'Arcade', 'StudioA');

                -- Group 2: com.demo.beta (2 entries)
                INSERT INTO entries VALUES ('b1', 'com.demo.beta', 'com.demo.beta', 'Beta One', '1.0', '7.0', 70000, 5242880, 1, 2, '', 'Utilities', '', 'StudioB');
                INSERT INTO entries VALUES ('b2', 'com.demo.beta', 'com.demo.beta', 'Beta Two', '2.0', '8.0', 80000, 10485760, 1, 6, '', 'Utilities', '', 'StudioB');

                -- Single entry: com.demo.single (100%_Literal)
                INSERT INTO entries VALUES ('s1', 'com.demo.single', 'com.demo.single', 'Solo 100%_App', '3.1', '6.0', 60000, 4194304, 1, 1, '', 'Music', '', 'SoloDev');

                -- Unknown min_os single entry
                INSERT INTO entries VALUES ('u1', 'com.demo.unknown', 'com.demo.unknown', 'Mystery App', '0.9', '', 0, 512000, 0, 2, '', 'Tools', '', 'Anon');

                INSERT INTO sources (entry_id, source_id, url, page, size, sha256) VALUES ('a1', 'ArchiveMirror', 'http://127.0.0.1/a1.ipa', '', 1048576, NULL);
                INSERT INTO sources (entry_id, source_id, url, page, size, sha256) VALUES ('b1', 'BetaMirror', 'http://127.0.0.1/b1.ipa', '', 5242880, NULL);
                INSERT INTO descriptions VALUES ('a2', 'en', 'Unique keyword retro_rocket inside description');
                INSERT INTO descriptions VALUES ('s1', 'en', 'Solo sound synthesis engine');
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        return dbPath;
    }

    [Fact]
    public void IosVersionCatalog_Has93Versions_ValidGroups_AndParsesValidAndInvalidVersions()
    {
        Assert.Equal(93, IosVersionCatalog.AllVersions.Count);
        Assert.Equal("2.0", IosVersionCatalog.AllVersions[0]);
        Assert.Equal("10.3.4", IosVersionCatalog.AllVersions[^1]);

        var g42 = Assert.Single(IosVersionCatalog.Groups, g => g.MajorMinor == "4.2");
        Assert.Equal(["4.2.1", "4.2.6", "4.2.7", "4.2.8", "4.2.9", "4.2.10"], g42.Revisions);
        Assert.DoesNotContain("4.2", g42.Revisions);

        Assert.True(IosVersionCatalog.TryParseVersionNum("6.1.3", out var v613));
        Assert.Equal(60103, v613);
        Assert.True(IosVersionCatalog.TryParseVersionNum("10.3.4", out var v1034));
        Assert.Equal(100304, v1034);
        Assert.True(IosVersionCatalog.TryParseVersionNum("4.2.10", out var v4210));
        Assert.Equal(40210, v4210);
        Assert.Equal("6.1.3", IosVersionCatalog.FormatVersionNum(60103));
        Assert.Equal("7.0", IosVersionCatalog.FormatVersionNum(70000));

        Assert.False(IosVersionCatalog.TryParseVersionNum("", out _));
        Assert.False(IosVersionCatalog.TryParseVersionNum("abc", out _));
        Assert.False(IosVersionCatalog.TryParseVersionNum("6.100.0", out _));
        Assert.False(IosVersionCatalog.TryParseVersionNum("1.2.3.4", out _));
        Assert.False(IosVersionCatalog.TryParseVersionNum("-1.0", out _));

        Assert.Equal("<=", IosVersionCatalog.SqlOperator(MinOsOperator.LessOrEqual));
        Assert.Equal("<", IosVersionCatalog.SqlOperator(MinOsOperator.Less));
        Assert.Equal("=", IosVersionCatalog.SqlOperator(MinOsOperator.Equal));
        Assert.Equal(">=", IosVersionCatalog.SqlOperator(MinOsOperator.GreaterOrEqual));
        Assert.Equal(">", IosVersionCatalog.SqlOperator(MinOsOperator.Greater));
        Assert.Equal(MinOsOperator.GreaterOrEqual, IosVersionCatalog.ParseOperator(">="));
        Assert.Equal(MinOsOperator.GreaterOrEqual, IosVersionCatalog.ParseOperator("≥"));
        Assert.Equal(MinOsOperator.LessOrEqual, IosVersionCatalog.ParseOperator("unknown"));
    }

    [Fact]
    public void Search_MinOsOperators_AllFiveWorkCorrectly_AndExcludeUnknownWhenFiltered()
    {
        using var ws = new TestWorkspace("indexdb-minos-ops");
        var db = new IndexDatabase(CreateGroupedAndFilterDatabase(ws));

        // min_os_num values: a1=40300, a3=50100, s1=60000, a2=60103, b1=70000, b2=80000, u1=0 (excluded)
        Assert.Equal(
            ["a1", "a3", "s1"],
            db.Search(new IndexQuery("", 0, 60000, IndexSort.MinOs, false, MinOsOperator.LessOrEqual), 50)
                .Select(r => r.Id).ToArray());

        Assert.Equal(
            ["a1", "a3"],
            db.Search(new IndexQuery("", 0, 60000, IndexSort.MinOs, false, MinOsOperator.Less), 50)
                .Select(r => r.Id).ToArray());

        Assert.Equal(
            ["s1"],
            db.Search(new IndexQuery("", 0, 60000, IndexSort.MinOs, false, MinOsOperator.Equal), 50)
                .Select(r => r.Id).ToArray());

        Assert.Equal(
            ["s1", "a2", "b1", "b2"],
            db.Search(new IndexQuery("", 0, 60000, IndexSort.MinOs, false, MinOsOperator.GreaterOrEqual), 50)
                .Select(r => r.Id).ToArray());

        Assert.Equal(
            ["a2", "b1", "b2"],
            db.Search(new IndexQuery("", 0, 60000, IndexSort.MinOs, false, MinOsOperator.Greater), 50)
                .Select(r => r.Id).ToArray());
    }

    [Fact]
    public void SearchScope_And_MatchModes_WorkAcrossFields_Joins_And_JsonRoundTrip()
    {
        using var ws = new TestWorkspace("indexdb-search-scope");
        var db = new IndexDatabase(CreateGroupedAndFilterDatabase(ws));

        // JSON / CSV round-trip including EmptyToken
        var scope = new IndexSearchScope
        {
            IsTitle = false,
            IsBundleId = false,
            IsDescription = true,
            IsSource = true,
        };
        var parsedScope = IndexSearchScope.ParseJson(scope.ToJsonArray());
        Assert.False(parsedScope.IsTitle);
        Assert.False(parsedScope.IsBundleId);
        Assert.True(parsedScope.IsDescription);
        Assert.True(parsedScope.IsSource);

        var emptyScope = IndexSearchScope.Parse("none");
        Assert.True(emptyScope.IsEmpty);
        // P3-3: Empty scope falls back to default fields (Title + BundleId) instead of returning 0 matches
        Assert.Equal(3, db.Count(new IndexQuery("Alpha", 0, 0, IndexSort.Title, false, SearchScope: emptyScope)));

        // Description join search
        var descScope = IndexSearchScope.Parse("description");
        Assert.Equal(
            ["a2"],
            db.Search(new IndexQuery("retro_rocket", 0, 0, IndexSort.Title, false, SearchScope: descScope), 50)
                .Select(r => r.Id).ToArray());

        // Source join search (matches sources.source_id / url)
        var srcScope = IndexSearchScope.Parse("source");
        Assert.Equal(
            ["a1"],
            db.Search(new IndexQuery("ArchiveMirror", 0, 0, IndexSort.Title, false, SearchScope: srcScope), 50)
                .Select(r => r.Id).ToArray());

        // MatchModes: Contains vs Prefix vs Exact
        var titleOnly = IndexSearchScope.Parse("title");
        Assert.Equal(3, db.Count(new IndexQuery("Alpha", 0, 0, IndexSort.Title, false, SearchScope: titleOnly, MatchMode: IndexSearchMatchMode.Contains)));
        Assert.Equal(3, db.Count(new IndexQuery("Alpha", 0, 0, IndexSort.Title, false, SearchScope: titleOnly, MatchMode: IndexSearchMatchMode.Prefix)));
        Assert.Equal(0, db.Count(new IndexQuery("Alpha", 0, 0, IndexSort.Title, false, SearchScope: titleOnly, MatchMode: IndexSearchMatchMode.Exact)));
        Assert.Equal(1, db.Count(new IndexQuery("Alpha New", 0, 0, IndexSort.Title, false, SearchScope: titleOnly, MatchMode: IndexSearchMatchMode.Exact)));
        Assert.Equal(0, db.Count(new IndexQuery("New", 0, 0, IndexSort.Title, false, SearchScope: titleOnly, MatchMode: IndexSearchMatchMode.Prefix)));
        Assert.Equal(1, db.Count(new IndexQuery("New", 0, 0, IndexSort.Title, false, SearchScope: titleOnly, MatchMode: IndexSearchMatchMode.Contains)));
    }

    [Fact]
    public void AdvancedFilter_SqlConditions_DeviceCombo_SizeRange_AndOr_AndEscaping_WorkCleanly()
    {
        using var ws = new TestWorkspace("indexdb-adv-filter");
        var db = new IndexDatabase(CreateGroupedAndFilterDatabase(ws));

        // Build complex filter: Group1(AND: SizeMb 2..6 AND DeviceFamily=iPhone+iPad) -> only a3 (2 MB, mask=6)
        var andState = new IndexAdvancedFilterState
        {
            Enabled = true,
            Groups =
            [
                new IndexAdvancedFilterGroup
                {
                    Join = IndexFilterJoin.And,
                    Conditions =
                    [
                        new IndexAdvancedFilterCondition
                        {
                            Column = IndexFilterColumn.SizeMb,
                            Min = "2",
                            Max = "6",
                        },
                        new IndexAdvancedFilterCondition
                        {
                            Column = IndexFilterColumn.DeviceFamily,
                            Selected = ["iPhone", "iPad"],
                        },
                    ],
                },
            ],
        };

        var roundTrip = IndexAdvancedFilterState.Parse(andState.Serialize());
        Assert.True(roundTrip.Enabled);
        Assert.Equal(2, roundTrip.ActiveConditionCount);

        var andRows = db.Search(new IndexQuery("", 0, 0, IndexSort.Title, false, AdvancedFilter: roundTrip), 50);
        Assert.Equal(["a3"], andRows.Select(r => r.Id).ToArray());

        // Group with OR join: (DeviceFamily=iPhone+iPad OR Source exact BetaMirror) -> a3, b1, b2
        var orState = new IndexAdvancedFilterState
        {
            Enabled = true,
            Groups =
            [
                new IndexAdvancedFilterGroup
                {
                    Join = IndexFilterJoin.Or,
                    Conditions =
                    [
                        new IndexAdvancedFilterCondition
                        {
                            Column = IndexFilterColumn.DeviceFamily,
                            Selected = ["iPhone", "iPad"],
                        },
                        new IndexAdvancedFilterCondition
                        {
                            Column = IndexFilterColumn.Source,
                            TextMode = IndexTextFilterMode.Exact,
                            Text = "BetaMirror",
                        },
                    ],
                },
            ],
        };
        var orRows = db.Search(new IndexQuery("", 0, 0, IndexSort.Title, false, AdvancedFilter: orState), 50);
        Assert.Equal(["a3", "b1", "b2"], orRows.Select(r => r.Id).ToArray());

        // MinOs range + LIKE special characters / SQL injection attempt must be safely parameterized
        var safeState = new IndexAdvancedFilterState
        {
            Enabled = true,
            Groups =
            [
                new IndexAdvancedFilterGroup
                {
                    Join = IndexFilterJoin.And,
                    Conditions =
                    [
                        new IndexAdvancedFilterCondition
                        {
                            Column = IndexFilterColumn.Title,
                            TextMode = IndexTextFilterMode.Contains,
                            Text = "100%_App",
                        },
                    ],
                },
            ],
        };
        Assert.Equal(["s1"], db.Search(new IndexQuery("", 0, 0, IndexSort.Title, false, AdvancedFilter: safeState), 50).Select(r => r.Id).ToArray());

        var injectionState = new IndexAdvancedFilterState
        {
            Enabled = true,
            Groups =
            [
                new IndexAdvancedFilterGroup
                {
                    Join = IndexFilterJoin.And,
                    Conditions =
                    [
                        new IndexAdvancedFilterCondition
                        {
                            Column = IndexFilterColumn.Title,
                            TextMode = IndexTextFilterMode.Contains,
                            Text = "' OR 1=1 --",
                        },
                    ],
                },
            ],
        };
        Assert.Empty(db.Search(new IndexQuery("", 0, 0, IndexSort.Title, false, AdvancedFilter: injectionState), 50));
    }

    [Fact]
    public void CountGrouped_SearchGrouped_GetGroupMembers_SortColumns_AndUnlimitedLimit_WorkCleanly()
    {
        using var ws = new TestWorkspace("indexdb-grouped");
        var db = new IndexDatabase(CreateGroupedAndFilterDatabase(ws));

        var baseQuery = new IndexQuery("", 0, 0, IndexSort.Title, false);
        var (totalEntries, totalGroups) = db.CountGrouped(baseQuery);
        Assert.Equal(7, totalEntries);
        // 4 distinct bundle_lower values: com.demo.alpha (3), com.demo.beta (2), com.demo.single (1), com.demo.unknown (1)
        Assert.Equal(4, totalGroups);

        // limit <= 0 returns all rows (unlimited)
        Assert.Equal(7, db.Search(baseQuery, limit: 0).Count);
        var groupedRows = db.SearchGrouped(baseQuery, limit: 0);
        Assert.Equal(4, groupedRows.Count);

        var alphaGroup = Assert.Single(groupedRows, r => r.BundleId == "com.demo.alpha");
        Assert.True(alphaGroup.IsGroup);
        Assert.True(alphaGroup.IsGroupEnd);
        Assert.Equal(3, alphaGroup.GroupCount);
        Assert.Equal("1.0 \u2013 2.0", alphaGroup.RangeVersionText);
        Assert.Equal("4.3 \u2013 6.1.3", alphaGroup.RangeMinOsText);
        Assert.NotEqual("", alphaGroup.RangeSizeText);

        // P2-1: Natural/semantic version comparison prevents "10.0" < "2.0" range inversion
        Assert.True(IndexDatabase.CompareVersionStrings("2.0", "10.0") < 0);
        Assert.True(IndexDatabase.CompareVersionStrings("1.9.10", "1.10.0") < 0);

        var singleRow = Assert.Single(groupedRows, r => r.BundleId == "com.demo.single");
        Assert.False(singleRow.IsGroup);
        Assert.Equal("s1", singleRow.Id);

        // Expand group members respecting active filter (min_os <= 5.1 excludes a2)
        var filteredQuery = new IndexQuery("", 0, 50100, IndexSort.Version, true, MinOsOperator.LessOrEqual);
        var members = db.GetGroupMembers(filteredQuery, "com.demo.alpha");
        Assert.Equal(["a3", "a1"], members.Select(m => m.Id).ToArray());
        Assert.All(members, m =>
        {
            Assert.True(m.IsGroupMember);
            Assert.Equal("com.demo.alpha", m.GroupKey);
        });

        // New sort columns: BundleId, DeviceFamily, Category
        var byBundleDesc = db.Search(new IndexQuery("", 0, 0, IndexSort.BundleId, true), 0);
        Assert.Equal("com.demo.unknown", byBundleDesc[0].BundleId);
        var byCategoryAsc = db.Search(new IndexQuery("", 0, 0, IndexSort.Category, false), 0);
        Assert.Equal("Games", byCategoryAsc[0].Category);
        var byDeviceMaskAsc = db.Search(new IndexQuery("", 0, 0, IndexSort.DeviceFamily, false), 0);
        Assert.Equal(1, byDeviceMaskAsc[0].DeviceMask);
    }
}
