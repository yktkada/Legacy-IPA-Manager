using IdeviceIndex;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>IdeviceIndex 的只读 SQLite 查询层（P2-6：新组件至少要有 DB 层回归网）。</summary>
public sealed class IdeviceIndexDatabaseTests
{
    private static string CreateDatabase(TestWorkspace ws)
    {
        var path = ws.Full("index.db");
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE entries (
                id TEXT PRIMARY KEY, bundle_id TEXT, title TEXT, version TEXT,
                min_os TEXT, min_os_num INTEGER, size INTEGER, size_exact INTEGER,
                device_mask INTEGER, icon TEXT, category TEXT, subgenre TEXT,
                developer TEXT, bundle_lower TEXT);
            CREATE TABLE sources (
                id INTEGER PRIMARY KEY, entry_id TEXT, source_id TEXT, url TEXT,
                page TEXT, size INTEGER);
            CREATE TABLE descriptions (entry_id TEXT, lang TEXT, text TEXT);
            CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);

            INSERT INTO entries VALUES
                ('a', 'com.example.alpha', 'Alpha 100%', '1.0', '4.3', 40300, 10485760, 1, 2, 'icons/a.jpg', 'Games', 'Action', 'DevA', 'com.example.alpha'),
                ('b', 'com.example.beta', 'Beta', '2.0', '6.0', 60000, 20971520, 1, 4, 'icons/b.jpg', 'Utilities', '', 'DevB', 'com.example.beta');
            INSERT INTO sources VALUES
                (1, 'a', 'stuffed18', 'https://example.test/a.ipa', 'https://example.test/a', 10485760),
                (2, 'a', 'appdrop', 'https://example.test/a2.ipa', 'https://example.test/a2', 10485760);
            INSERT INTO descriptions VALUES
                ('a', 'en', 'Alpha description'),
                ('a', 'zh-Hans', '阿尔法描述');
            INSERT INTO meta VALUES
                ('schema_version', '1'), ('fts5', '0');
            """;
        cmd.ExecuteNonQuery();
        return path;
    }

    [Fact]
    public void 搜索百分号按字面量处理_不当通配符()
    {
        using var ws = new TestWorkspace("index-db");
        var db = new IndexDatabase(CreateDatabase(ws));

        var query = new IndexQuery("%", 0, 0, IndexSort.Title, false);

        Assert.Equal(1, db.Count(query));
        Assert.Equal("a", Assert.Single(db.Search(query, 50)).Id);
    }

    [Fact]
    public void 搜索_设备族与最低iOS组合_排序与详情读回()
    {
        using var ws = new TestWorkspace("index-db");
        var db = new IndexDatabase(CreateDatabase(ws));

        var query = new IndexQuery("", 2, 40300, IndexSort.Size, true);
        var rows = db.Search(query, 50);

        var row = Assert.Single(rows);
        Assert.Equal("a", row.Id);
        Assert.Equal("Alpha 100%", row.DisplayTitle);
        Assert.Equal("4.3", row.MinOsText);
        Assert.Equal("iPhone", row.DeviceFamilyText);
        Assert.Equal(2, row.SourceCount);
        Assert.Equal(2, db.GetSources("a").Count);
        Assert.Equal(2, db.GetDescriptions("a").Count);
        Assert.Equal("1", db.GetMeta("schema_version"));
    }

    [Fact]
    public void 排序_大小降序_白名单列()
    {
        using var ws = new TestWorkspace("index-db");
        var db = new IndexDatabase(CreateDatabase(ws));

        var rows = db.Search(new IndexQuery("", 0, 0, IndexSort.Size, true), 50);

        Assert.Equal(new[] { "b", "a" }, rows.Select(r => r.Id));
    }

    [Fact]
    public void 无匹配_返回空而不是抛()
    {
        using var ws = new TestWorkspace("index-db");
        var db = new IndexDatabase(CreateDatabase(ws));

        var rows = db.Search(new IndexQuery("no-such-app", 0, 0, IndexSort.Title, false), 50);

        Assert.Empty(rows);
    }
}
