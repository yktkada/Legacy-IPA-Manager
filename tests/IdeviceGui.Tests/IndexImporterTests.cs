using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IdeviceIndex;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IdeviceGui.Tests;

public sealed class IndexImporterTests
{
    private static readonly string[] ExpectedIndexes =
    [
        "ix_entries_bundle",
        "ix_entries_minos",
        "ix_entries_device",
        "ix_entries_arch",
        "ix_entries_size",
        "ix_entries_title",
        "ix_sources_entry",
        "ix_sources_host",
        "ix_descriptions_lang",
    ];

    [Fact]
    public async Task ImportDatabaseFileAsync_ValidatesCopyVsReadOnlyLink_AndRejectsInvalidSchemas()
    {
        using var ws = new TestWorkspace("index-import-db");
        var progDir = ws.Full("app");
        var settingsPath = Path.Combine(ws.Root, "app", "data", "indexer.json");

        // Build a valid source DB via FullReplace JSONL
        var jsonlPath = ws.Full("seed.jsonl");
        File.WriteAllLines(jsonlPath,
        [
            JsonSerializer.Serialize(new
            {
                id = "com.test.one@1.0",
                bundle_id = "com.test.one",
                title = "App One",
                version = "1.0",
                min_os = "6.0",
                device_family = new[] { "iPhone" },
                arch = new[] { "armv7" },
                size = 1024,
                sources = new[] { new { source_id = "s1", url = "https://example.invalid/1.ipa", size = 1024 } },
            }),
        ]);

        var sourceDb = Path.Combine(ws.Root, "external", "custom.db");
        await IndexImporter.ImportJsonAsync(jsonlPath, sourceDb, new IndexImportOptions(IndexImportMode.FullReplace), ct: TestContext.Current.CancellationToken);

        var check = IndexImporter.ValidateDatabase(sourceDb);
        Assert.True(check.IsValid);
        Assert.Equal(1, check.EntryCount);

        // Link read-only -> sets Index.DbPath and does not copy to <progDir>\index\index.db
        await IndexImporter.ImportDatabaseFileAsync(sourceDb, progDir, copyToProgramDir: false, settingsPath, TestContext.Current.CancellationToken);
        Assert.Equal(Path.GetFullPath(sourceDb), IndexerSettings.LoadCustomDbPath(settingsPath));
        Assert.Equal(Path.GetFullPath(sourceDb), IndexImporter.ResolveStartupDbPath(null, settingsPath, progDir));
        Assert.False(File.Exists(IndexImporter.DefaultDbPath(progDir)));

        // Copy to program dir -> atomically places <progDir>\index\index.db and clears Index.DbPath
        await IndexImporter.ImportDatabaseFileAsync(sourceDb, progDir, copyToProgramDir: true, settingsPath, TestContext.Current.CancellationToken);
        Assert.Equal("", IndexerSettings.LoadCustomDbPath(settingsPath));
        Assert.Equal(Path.GetFullPath(IndexImporter.DefaultDbPath(progDir)), IndexImporter.ResolveStartupDbPath(null, settingsPath, progDir));
        Assert.True(File.Exists(IndexImporter.DefaultDbPath(progDir)));

        // Invalid DB missing required tables or wrong schema_version is rejected
        var badDb = ws.Full("bad.db");
        using (var conn = new SqliteConnection($"Data Source={badDb};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT); INSERT INTO meta VALUES ('schema_version', '99');";
            cmd.ExecuteNonQuery();
        }
        var badRes = IndexImporter.ValidateDatabase(badDb);
        Assert.False(badRes.IsValid);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IndexImporter.ImportDatabaseFileAsync(badDb, progDir, copyToProgramDir: true, settingsPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ImportJsonAsync_FullReplace_SupportsJsonlJsonlGzJsonArray_AndCreatesAll9Indexes()
    {
        using var ws = new TestWorkspace("index-import-full");

        var entries = new object[]
        {
            new
            {
                id = "com.alpha@2.0",
                bundle_id = "com.alpha",
                title = "Alpha",
                version = "2.0",
                min_os = "6.1.3",
                device_family = new[] { "iPhone", "iPad" },
                arch = new[] { "armv7", "arm64" },
                size = 2048,
                size_exact = true,
                icon = "../escape/bad.png",
                category = "Games",
                developer = "AlphaDev",
                descriptions = new Dictionary<string, string> { ["en"] = "Alpha desc", ["zh-Hans"] = "Alpha 中文" },
                sources = new[]
                {
                    new { source_id = "bad", url = "javascript:alert(1)", size = 100 },
                    new { source_id = "good", url = "https://example.invalid/alpha.ipa", size = 2048 },
                },
            },
            new
            {
                id = "com.alpha@10.0",
                bundle_id = "com.alpha",
                title = "Alpha 10",
                version = "10.0",
                min_os = "7.0",
                device_family = new[] { "iPhone" },
                arch = new[] { "arm64" },
                size = 4096,
                icon = "icons/ab/cd.jpg",
                sources = new[]
                {
                    new { source_id = "good", url = "http://127.0.0.1/alpha10.ipa", size = 4096 },
                },
            },
        };

        // 1. .jsonl.gz full rebuild
        var gzPath = ws.Full("entries.jsonl.gz");
        await using (var fs = File.Create(gzPath))
        await using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
        await using (var sw = new StreamWriter(gz, new UTF8Encoding(false)))
        {
            foreach (var e in entries)
                await sw.WriteLineAsync(JsonSerializer.Serialize(e));
        }

        var dbFromGz = ws.Full("from-gz.db");
        var resGz = await IndexImporter.ImportJsonAsync(
            gzPath, dbFromGz, new IndexImportOptions(IndexImportMode.FullReplace), ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, resGz.TotalEntries);
        Assert.Equal(2, resGz.UpsertedCount);

        // Verify all 9 indexes exist, unsafe source URL was dropped, unsafe icon was stripped, semantic range works
        using (var conn = new SqliteConnection($"Data Source={dbFromGz};Mode=ReadOnly;Pooling=False"))
        {
            conn.Open();
            var idxNames = new HashSet<string>(StringComparer.Ordinal);
            using var idxCmd = conn.CreateCommand();
            idxCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index'";
            using var r = idxCmd.ExecuteReader();
            while (r.Read()) idxNames.Add(r.GetString(0));
            foreach (var expected in ExpectedIndexes)
                Assert.Contains(expected, idxNames);
        }

        var db = new IndexDatabase(dbFromGz);
        var a2 = db.GetById("com.alpha@2.0")!;
        Assert.Equal("", a2.IconPath); // stripped "../escape/bad.png"
        Assert.Equal("icons/ab/cd.jpg", db.GetById("com.alpha@10.0")!.IconPath);
        var a2Sources = db.GetSources("com.alpha@2.0");
        Assert.Single(a2Sources);
        Assert.Equal("https://example.invalid/alpha.ipa", a2Sources[0].Url);
        Assert.Equal(2, db.GetDescriptions("com.alpha@2.0").Count);

        // Group version range uses natural semantic order ("2.0 – 10.0", NOT "10.0 – 2.0")
        var grouped = db.SearchGrouped(new IndexQuery("", 0, 0, IndexSort.Title, false), 50);
        var grp = Assert.Single(grouped);
        Assert.True(grp.IsGroup);
        Assert.True(grp.IsGroupEnd);
        Assert.Equal("2.0 \u2013 10.0", grp.RangeVersionText);

        // 2. .json array full rebuild
        var jsonArrPath = ws.Full("entries.json");
        File.WriteAllText(jsonArrPath, JsonSerializer.Serialize(entries));
        var dbFromJson = ws.Full("from-json.db");
        var resJson = await IndexImporter.ImportJsonAsync(
            jsonArrPath, dbFromJson, new IndexImportOptions(IndexImportMode.FullReplace), ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, resJson.TotalEntries);
    }

    [Fact]
    public async Task ImportJsonAsync_ManifestVerification_RejectsShaMismatchAndPathTraversal()
    {
        using var ws = new TestWorkspace("index-import-manifest");
        var entriesPath = Path.Combine(ws.Root, "pkg", "entries.jsonl.gz");
        Directory.CreateDirectory(Path.GetDirectoryName(entriesPath)!);
        await using (var fs = File.Create(entriesPath))
        await using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
        await using (var sw = new StreamWriter(gz, new UTF8Encoding(false)))
        {
            await sw.WriteLineAsync(JsonSerializer.Serialize(new
            {
                id = "com.m@1.0",
                bundle_id = "com.m",
                title = "Manifest App",
                version = "1.0",
                sources = new[] { new { source_id = "s", url = "https://example.invalid/m.ipa" } },
            }));
        }

        var gzBytes = await File.ReadAllBytesAsync(entriesPath, TestContext.Current.CancellationToken);
        var gzSha = Convert.ToHexString(SHA256.HashData(gzBytes)).ToLowerInvariant();

        var manifestPath = Path.Combine(ws.Root, "pkg", "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
        {
            schema = "legacy-ipa-index",
            schema_version = 1,
            revision = "rev-1",
            entry_count = 1,
            entries = new
            {
                path = "entries.jsonl.gz",
                format = "jsonl",
                compression = "gzip",
                bytes = gzBytes.Length,
                sha256 = gzSha,
            },
        }));

        var targetDb = Path.Combine(ws.Root, "out", "index.db");
        var okRes = await IndexImporter.ImportJsonAsync(
            manifestPath, targetDb, new IndexImportOptions(IndexImportMode.FullReplace), ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, okRes.TotalEntries);
        Assert.Equal("rev-1", okRes.Revision);

        // SHA mismatch rejected
        var badShaDir = ws.Full("badsha");
        Directory.CreateDirectory(badShaDir);
        File.Copy(entriesPath, Path.Combine(badShaDir, "entries.jsonl.gz"));
        var badShaManifestReal = Path.Combine(badShaDir, "manifest.json");
        File.WriteAllText(badShaManifestReal, File.ReadAllText(manifestPath).Replace(gzSha, new string('0', 64)));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IndexImporter.ImportJsonAsync(badShaManifestReal, Path.Combine(ws.Root, "out", "badsha.db"), new IndexImportOptions(IndexImportMode.FullReplace), ct: TestContext.Current.CancellationToken));

        // Path traversal ("../pkg/entries.jsonl.gz") rejected
        var escDir = ws.Full("esc");
        Directory.CreateDirectory(escDir);
        var escManifest = Path.Combine(escDir, "manifest.json");
        File.WriteAllText(escManifest, JsonSerializer.Serialize(new
        {
            schema = "legacy-ipa-index",
            schema_version = 1,
            entry_count = 1,
            entries = new
            {
                path = "../pkg/entries.jsonl.gz",
                format = "jsonl",
                compression = "gzip",
                bytes = gzBytes.Length,
                sha256 = gzSha,
            },
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IndexImporter.ImportJsonAsync(escManifest, Path.Combine(ws.Root, "out", "esc.db"), new IndexImportOptions(IndexImportMode.FullReplace), ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ImportJsonAsync_Incremental_UpsertDeleteMergeSourcesRevisionAndRollback()
    {
        using var ws = new TestWorkspace("index-import-inc");
        var seedJson = ws.Full("seed.json");
        File.WriteAllText(seedJson, JsonSerializer.Serialize(new
        {
            target_revision = "rev-1",
            entries = new object[]
            {
                new
                {
                    id = "com.keep@1.0",
                    bundle_id = "com.keep",
                    title = "Keep App",
                    version = "1.0",
                    sources = new[] { new { source_id = "old", url = "https://old.invalid/keep.ipa", size = 100 } },
                },
                new
                {
                    id = "com.del@1.0",
                    bundle_id = "com.del",
                    title = "Delete App",
                    version = "1.0",
                    descriptions = new Dictionary<string, string> { ["en"] = "Will be deleted" },
                    sources = new[] { new { source_id = "s", url = "https://old.invalid/del.ipa", size = 100 } },
                },
            },
        }));

        var externalDb = Path.Combine(ws.Root, "external", "ro.db");
        await IndexImporter.ImportJsonAsync(
            seedJson, externalDb, new IndexImportOptions(IndexImportMode.FullReplace), ct: TestContext.Current.CancellationToken);
        var originalExternalBytes = await File.ReadAllBytesAsync(externalDb, TestContext.Current.CancellationToken);

        // Incremental patch onto targetDb seeded from external read-only DB (must NOT touch externalDb!)
        var patchJson = ws.Full("patch1.json");
        File.WriteAllText(patchJson, JsonSerializer.Serialize(new
        {
            base_revision = "rev-1",
            target_revision = "rev-2",
            operations = new object[]
            {
                new
                {
                    op = "upsert",
                    id = "com.keep@1.0",
                    bundle_id = "com.keep",
                    title = "Keep App Updated",
                    version = "1.0",
                    sources = new[]
                    {
                        new { source_id = "old", url = "https://old.invalid/keep.ipa", size = 100 },
                        new { source_id = "new", url = "https://new.invalid/keep.ipa", size = 200 },
                    },
                },
                new
                {
                    op = "upsert",
                    id = "com.added@2.0",
                    bundle_id = "com.added",
                    title = "Added App",
                    version = "2.0",
                    sources = new[] { new { source_id = "s", url = "https://new.invalid/added.ipa", size = 300 } },
                },
                new { op = "delete", id = "com.del@1.0" },
            },
        }));

        var localDb = Path.Combine(ws.Root, "app", "index", "index.db");
        var incRes = await IndexImporter.ImportJsonAsync(
            patchJson,
            localDb,
            new IndexImportOptions(IndexImportMode.Incremental, MergeSources: true, VerifyBaseRevision: true),
            currentExternalDbPath: externalDb,
            ct: TestContext.Current.CancellationToken);

        Assert.True(incRes.WasIncremental);
        Assert.Equal(2, incRes.TotalEntries);
        Assert.Equal(2, incRes.UpsertedCount);
        Assert.Equal(1, incRes.DeletedCount);
        Assert.Equal("rev-2", incRes.Revision);

        // External DB remained byte-for-byte untouched
        Assert.Equal(originalExternalBytes, await File.ReadAllBytesAsync(externalDb, TestContext.Current.CancellationToken));

        var db = new IndexDatabase(localDb);
        Assert.Equal("Keep App Updated", db.GetById("com.keep@1.0")!.Title);
        // MergeSources kept old URL + added new URL without duplicating old URL
        Assert.Equal(2, db.GetSources("com.keep@1.0").Count);
        Assert.Null(db.GetById("com.del@1.0"));
        Assert.Empty(db.GetSources("com.del@1.0"));
        Assert.Empty(db.GetDescriptions("com.del@1.0"));
        Assert.NotNull(db.GetById("com.added@2.0"));

        // Idempotent replay with VerifyBaseRevision: false works identically
        var replay = await IndexImporter.ImportJsonAsync(
            patchJson,
            localDb,
            new IndexImportOptions(IndexImportMode.Incremental, MergeSources: true, VerifyBaseRevision: false),
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, replay.TotalEntries);

        // Base revision mismatch is rejected without modifying DB
        var wrongRevPatch = ws.Full("wrong-rev.json");
        File.WriteAllText(wrongRevPatch, JsonSerializer.Serialize(new
        {
            base_revision = "rev-999",
            target_revision = "rev-1000",
            operations = new object[] { new { op = "delete", id = "com.keep@1.0" } },
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IndexImporter.ImportJsonAsync(
                wrongRevPatch,
                localDb,
                new IndexImportOptions(IndexImportMode.Incremental, VerifyBaseRevision: true),
                ct: TestContext.Current.CancellationToken));
        Assert.NotNull(db.GetById("com.keep@1.0"));

        // Bad row in middle of transaction rolls back earlier valid ops atomically
        var brokenPatch = ws.Full("broken-patch.json");
        File.WriteAllText(brokenPatch, JsonSerializer.Serialize(new
        {
            base_revision = "rev-2",
            target_revision = "rev-3",
            operations = new object[]
            {
                new
                {
                    op = "upsert",
                    id = "com.should.rollback@1.0",
                    bundle_id = "com.should.rollback",
                    title = "Must Rollback",
                    version = "1.0",
                },
                new { op = "delete", id = "com.keep@1.0" },
                new { op = "upsert", id = "bad-missing-bundle", title = "Invalid" },
            },
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IndexImporter.ImportJsonAsync(
                brokenPatch,
                localDb,
                new IndexImportOptions(IndexImportMode.Incremental, VerifyBaseRevision: true),
                ct: TestContext.Current.CancellationToken));
        Assert.Null(db.GetById("com.should.rollback@1.0"));
        Assert.NotNull(db.GetById("com.keep@1.0"));
        Assert.Equal("rev-2", db.GetMeta("revision"));
    }

    [Fact]
    public async Task ImportDatabaseFileAsync_And_Incremental_ClearReadOnlyAttributeOnCopiedFile()
    {
        using var ws = new TestWorkspace("index-import-ro-attr");
        var progDir = ws.Full("app");
        var settingsPath = Path.Combine(ws.Root, "app", "data", "indexer.json");

        var seedJson = ws.Full("seed.json");
        File.WriteAllText(seedJson, JsonSerializer.Serialize(new
        {
            target_revision = "rev-1",
            entries = new object[]
            {
                new { id = "com.ro@1.0", bundle_id = "com.ro", title = "ReadOnly Seed", version = "1.0" },
            },
        }));

        var externalDb = Path.Combine(ws.Root, "external", "ro-attr.db");
        await IndexImporter.ImportJsonAsync(
            seedJson, externalDb, new IndexImportOptions(IndexImportMode.FullReplace), ct: TestContext.Current.CancellationToken);

        File.SetAttributes(externalDb, File.GetAttributes(externalDb) | FileAttributes.ReadOnly);
        try
        {
            var patchJson = ws.Full("patch.json");
            File.WriteAllText(patchJson, JsonSerializer.Serialize(new
            {
                base_revision = "rev-1",
                target_revision = "rev-2",
                operations = new object[]
                {
                    new { op = "upsert", id = "com.ro@2.0", bundle_id = "com.ro", title = "Added After RO Copy", version = "2.0" },
                },
            }));

            var localDb = IndexImporter.DefaultDbPath(progDir);
            var incRes = await IndexImporter.ImportJsonAsync(
                patchJson,
                localDb,
                new IndexImportOptions(IndexImportMode.Incremental),
                currentExternalDbPath: externalDb,
                ct: TestContext.Current.CancellationToken);

            Assert.Equal(2, incRes.TotalEntries);
            Assert.False((File.GetAttributes(localDb) & FileAttributes.ReadOnly) != 0);

            // Direct ImportDatabaseFileAsync copy also clears ReadOnly on destination
            var copyRes = await IndexImporter.ImportDatabaseFileAsync(
                externalDb, progDir, copyToProgramDir: true, settingsPath, TestContext.Current.CancellationToken);
            Assert.Equal(1, copyRes.EntryCount);
            Assert.False((File.GetAttributes(localDb) & FileAttributes.ReadOnly) != 0);
        }
        finally
        {
            if (File.Exists(externalDb))
                File.SetAttributes(externalDb, File.GetAttributes(externalDb) & ~FileAttributes.ReadOnly);
        }
    }

    [Fact]
    public async Task DownloadAndApplyUpdateAsync_ManifestAndIncrementalJsonPatch_UsesLoopbackMockWithoutExternalNetwork()
    {
        using var ws = new TestWorkspace("index-remote-update");
        var progDir = ws.Full("app");
        var settingsPath = Path.Combine(ws.Root, "app", "data", "indexer.json");

        // Prepare entries.jsonl.gz bytes
        byte[] gzBytes;
        await using (var ms = new MemoryStream())
        {
            await using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            await using (var sw = new StreamWriter(gz, new UTF8Encoding(false)))
            {
                await sw.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    id = "com.net@1.0",
                    bundle_id = "com.net",
                    title = "Net One",
                    version = "1.0",
                    sources = new[] { new { source_id = "m1", url = "http://127.0.0.1/net1.ipa", size = 512 } },
                }));
            }
            gzBytes = ms.ToArray();
        }
        var gzSha = Convert.ToHexString(SHA256.HashData(gzBytes)).ToLowerInvariant();

        var manifestJson = JsonSerializer.Serialize(new
        {
            schema = "legacy-ipa-index",
            schema_version = 1,
            revision = "r1",
            entry_count = 1,
            entries = new
            {
                path = "entries.jsonl.gz",
                format = "jsonl",
                compression = "gzip",
                bytes = gzBytes.Length,
                sha256 = gzSha,
            },
        });

        var patchJson = JsonSerializer.Serialize(new
        {
            base_revision = "r1",
            target_revision = "r2",
            operations = new object[]
            {
                new
                {
                    op = "upsert",
                    id = "com.net@2.0",
                    bundle_id = "com.net",
                    title = "Net Two",
                    version = "2.0",
                    sources = new[] { new { source_id = "m1", url = "http://127.0.0.1/net2.ipa", size = 1024 } },
                },
            },
        });

        using var handler = new StubHttpMessageHandler(uri =>
        {
            if (uri.AbsoluteUri == "http://127.0.0.1/idx/manifest.json")
                return (HttpStatusCode.OK, Encoding.UTF8.GetBytes(manifestJson));
            if (uri.AbsoluteUri == "http://127.0.0.1/idx/entries.jsonl.gz")
                return (HttpStatusCode.OK, gzBytes);
            if (uri.AbsoluteUri == "http://127.0.0.1/idx/patch-r2.json")
                return (HttpStatusCode.OK, Encoding.UTF8.GetBytes(patchJson));
            return (HttpStatusCode.NotFound, []);
        });

        // Reject unsafe scheme
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IndexImporter.DownloadAndApplyUpdateAsync(
                "file:///C:/evil/manifest.json",
                progDir,
                null,
                IndexImportMode.FullReplace,
                httpHandler: handler,
                ct: TestContext.Current.CancellationToken));

        // 1. Download manifest.json + entries.jsonl.gz and full rebuild
        var fullRes = await IndexImporter.DownloadAndApplyUpdateAsync(
            "http://127.0.0.1/idx/manifest.json",
            progDir,
            null,
            IndexImportMode.FullReplace,
            mergeSources: true,
            settingsPath: settingsPath,
            ct: TestContext.Current.CancellationToken,
            httpHandler: handler);

        Assert.Equal(1, fullRes.TotalEntries);
        Assert.Equal("r1", fullRes.Revision);

        // 2. Download incremental patch.json and apply onto local DB
        var incRes = await IndexImporter.DownloadAndApplyUpdateAsync(
            "http://127.0.0.1/idx/patch-r2.json",
            progDir,
            fullRes.TargetDbPath,
            IndexImportMode.Incremental,
            mergeSources: true,
            settingsPath: settingsPath,
            ct: TestContext.Current.CancellationToken,
            httpHandler: handler);

        Assert.True(incRes.WasIncremental);
        Assert.Equal(2, incRes.TotalEntries);
        Assert.Equal("r2", incRes.Revision);

        // Verify temp directory cleaned up cleanly
        var tmpRoot = Path.Combine(progDir, "data", "tmp");
        if (Directory.Exists(tmpRoot))
            Assert.Empty(Directory.GetDirectories(tmpRoot));
    }

    private sealed class StubHttpMessageHandler(Func<Uri, (HttpStatusCode Status, byte[] Body)> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = responder(request.RequestUri!);
            var resp = new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(body),
            };
            return Task.FromResult(resp);
        }
    }
}
