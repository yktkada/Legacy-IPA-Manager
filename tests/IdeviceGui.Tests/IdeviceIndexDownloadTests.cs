using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IdeviceIndex;
using IdeviceIndex.Resources;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IdeviceGui.Tests;

[Collection("Aria2Process")]
public sealed class IdeviceIndexDownloadTests
{
    private static string ToolkitDir { get; } = ResolveToolkitDir();

    private static string ResolveToolkitDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (var name in new[] { "idevice-toolkit", "toolkit" })
            {
                var candidate = Path.Combine(dir.FullName, name);
                if (File.Exists(Path.Combine(candidate, "aria2.manifest.json")))
                    return candidate;
            }
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }

    private static Aria2VerifyResult VerifyToolkitAria2()
    {
        var result = Aria2Verifier.Verify(ToolkitDir);
        Assert.True(result.Ok, $"Toolkit aria2 verification failed: {result.FailedStage} - {result.Message}");
        return result;
    }

    private static string ToolkitManifestSha256()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ToolkitDir, "aria2.manifest.json")));
        return doc.RootElement.GetProperty("sha256").GetString()!;
    }

    private static byte[] CreateMinimalZipPayload(int targetBytes = 16 * 1024)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("Payload/Demo.app/Info.plist", CompressionLevel.NoCompression);
            using var s = entry.Open();
            var body = new byte[Math.Max(1024, targetBytes - 256)];
            for (var i = 0; i < body.Length; i++)
                body[i] = (byte)(i & 0xFF);
            s.Write(body, 0, body.Length);
        }
        return ms.ToArray();
    }

    private static IndexRow SampleIndexRow(string id = "id-dl-1", string title = "Demo App", string bundleId = "com.example.demo") => new()
    {
        Id = id,
        BundleId = bundleId,
        Title = title,
        Version = "1.0.0",
        MinOs = "6.0",
        Size = 16 * 1024,
        SizeExact = true,
        DeviceMask = 2,
        SourceCount = 1,
        Category = "Games",
    };

    [Fact]
    [Trait("Category", "Integration")]
    public void Aria2Verifier_FourStep_RealPass_Missing_ManifestInvalid_ProcessFail_VersionMismatch_HashMismatch()
    {
        var realExe = Path.Combine(ToolkitDir, "aria2c.exe");
        var realManifest = Path.Combine(ToolkitDir, "aria2.manifest.json");
        var realSha = ToolkitManifestSha256();

        // Full 4-step pass against real aria2c.exe + aria2.manifest.json
        var pass = Aria2Verifier.Verify(ToolkitDir);
        Assert.True(pass.Ok, pass.Message);
        Assert.Equal(Aria2VerifyStage.None, pass.FailedStage);
        Assert.Equal("1.37.0", pass.Version);
        Assert.Equal(Path.GetFullPath(realExe), pass.ExePath);

        using var ws = new TestWorkspace("aria2-verify");

        // Step 1: missing files in empty directory
        var missing = Aria2Verifier.Verify(ws.Root);
        Assert.False(missing.Ok);
        Assert.Equal(Aria2VerifyStage.FilesMissing, missing.FailedStage);

        // Step 1: missing manifest against real exe
        var missingManifest = Aria2Verifier.VerifyFiles(realExe, ws.Full("missing.json"));
        Assert.False(missingManifest.Ok);
        Assert.Equal(Aria2VerifyStage.FilesMissing, missingManifest.FailedStage);

        // Step 1: corrupt / invalid manifest
        var badManifestPath = ws.Full("bad.manifest.json");
        File.WriteAllText(badManifestPath, "{ \"version\": \"1.37.0\", \"sha256\": \"not-hex\" }");
        var badManifest = Aria2Verifier.VerifyFiles(realExe, badManifestPath);
        Assert.False(badManifest.Ok);
        Assert.Equal(Aria2VerifyStage.ManifestInvalid, badManifest.FailedStage);

        // Step 2 (P3-9): hash mismatch must fail BEFORE executing external binary
        var wrongHashPath = ws.Full("wrong-hash.manifest.json");
        File.WriteAllText(wrongHashPath, JsonSerializer.Serialize(new
        {
            version = "1.37.0",
            sha256 = new string('0', 64),
        }));
        var versionInvocationsOnHashFail = 0;
        var hashMismatch = Aria2Verifier.VerifyFiles(realExe, wrongHashPath, _ =>
        {
            versionInvocationsOnHashFail++;
            return (0, "aria2 version 1.37.0", "");
        });
        Assert.False(hashMismatch.Ok);
        Assert.Equal(Aria2VerifyStage.HashMismatch, hashMismatch.FailedStage);
        Assert.Equal(0, versionInvocationsOnHashFail);

        // Step 3: process returns non-zero exit code
        var procFail = Aria2Verifier.VerifyFiles(realExe, realManifest, _ => (1, "", "simulated error"));
        Assert.False(procFail.Ok);
        Assert.Equal(Aria2VerifyStage.ProcessFailed, procFail.FailedStage);

        // Step 4: version mismatch against real aria2c.exe (runVersion: null)
        var wrongVersionPath = ws.Full("wrong-ver.manifest.json");
        File.WriteAllText(wrongVersionPath, JsonSerializer.Serialize(new
        {
            version = "1.36.0",
            sha256 = realSha,
        }));
        var versionMismatch = Aria2Verifier.VerifyFiles(realExe, wrongVersionPath, runVersion: null);
        Assert.False(versionMismatch.Ok);
        Assert.Equal(Aria2VerifyStage.VersionMismatch, versionMismatch.FailedStage);
    }

    [Fact]
    public void DownloadSettings_And_IndexerSettings_RoundTrip_UnknownPreservation_AndFallback()
    {
        using var ws = new TestWorkspace("dl-settings");
        var settingsPath = ws.Full("data/indexer.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, """
            {
              "Ui.Language": "en",
              "Custom.Unknown": "keep-value"
            }
            """);

        Assert.False(IndexerSettings.LoadAllowOutsideProgramDirWrite(settingsPath));
        IndexerSettings.SaveAllowOutsideProgramDirWrite(true, settingsPath);
        Assert.True(IndexerSettings.LoadAllowOutsideProgramDirWrite(settingsPath));

        var customDir = ws.Full("custom-lib");
        var customSession = ws.Full("custom-sess");
        var custom = new DownloadSettings
        {
            Dir = customDir,
            RememberLastDir = false,
            Connections = 12,
            Splits = 4,
            MinSplitSize = "16M",
            Concurrency = 6,
            SpeedLimit = "2M",
            PerTaskSpeedLimit = "512K",
            RetryTimes = 7,
            RetryWait = 30,
            Timeout = 120,
            Continue = false,
            ProxyMode = DownloadSettings.ProxyManual,
            ProxyUrl = "http://127.0.0.1:8888",
            ProxyBypass = "localhost;127.0.0.1",
            UserAgent = "CustomAgent/2.0",
            SessionDirMode = DownloadSettings.SessionCustom,
            SessionDirCustom = customSession,
            ControlFileMode = DownloadSettings.ControlSession,
            KeepPartFiles = false,
            PartTtlHours = 48,
            SpaceMarginMB = 128,
            AutoImport = false,
            AutoRefreshFallback = false,
            OpenFolderWhenDone = true,
            MaxFileSizeMB = 512,
        };

        IndexerSettings.SaveDownloadSettings(custom, settingsPath, ws.Root);
        var loaded = IndexerSettings.LoadDownloadSettings(settingsPath, ws.Root);

        Assert.Equal(Path.GetFullPath(customDir), loaded.Dir);
        Assert.False(loaded.RememberLastDir);
        Assert.Equal(12, loaded.Connections);
        Assert.Equal(4, loaded.Splits);
        Assert.Equal("16M", loaded.MinSplitSize);
        Assert.Equal(6, loaded.Concurrency);
        Assert.Equal("2M", loaded.SpeedLimit);
        Assert.Equal("512K", loaded.PerTaskSpeedLimit);
        Assert.Equal(7, loaded.RetryTimes);
        Assert.Equal(30, loaded.RetryWait);
        Assert.Equal(120, loaded.Timeout);
        Assert.False(loaded.Continue);
        Assert.Equal(DownloadSettings.ProxyManual, loaded.ProxyMode);
        Assert.Equal("http://127.0.0.1:8888", loaded.ProxyUrl);
        Assert.Equal("localhost;127.0.0.1", loaded.ProxyBypass);
        Assert.Equal("CustomAgent/2.0", loaded.UserAgent);
        Assert.Equal(DownloadSettings.SessionCustom, loaded.SessionDirMode);
        Assert.Equal(Path.GetFullPath(customSession), loaded.SessionDirCustom);
        Assert.Equal(DownloadSettings.ControlSession, loaded.ControlFileMode);
        Assert.False(loaded.KeepPartFiles);
        Assert.Equal(48, loaded.PartTtlHours);
        Assert.Equal(128, loaded.SpaceMarginMB);
        Assert.False(loaded.AutoImport);
        Assert.False(loaded.AutoRefreshFallback);
        Assert.True(loaded.OpenFolderWhenDone);
        Assert.Equal(512, loaded.MaxFileSizeMB);

        // Preserves existing & unknown keys
        Assert.Equal("en", IndexerSettings.LoadLanguage(settingsPath));
        Assert.True(IndexerSettings.LoadAllowOutsideProgramDirWrite(settingsPath));
        var raw = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        Assert.Equal("keep-value", raw["Custom.Unknown"]!.GetValue<string>());

        // Out-of-range values fall back to exact defaults
        var outOfRangePath = ws.Full("data/out-of-range.json");
        File.WriteAllText(outOfRangePath, """
            {
              "Download.Connections": 99,
              "Download.Splits": 0,
              "Download.MinSplitSize": "512M",
              "Download.Concurrency": 99,
              "Download.SpeedLimit": "1K",
              "Download.PerTaskSpeedLimit": "999M",
              "Download.RetryTimes": -1,
              "Download.RetryWait": 999,
              "Download.Timeout": 5,
              "Download.ProxyMode": "bogus",
              "Download.SessionDirMode": "unknown",
              "Download.ControlFileMode": "bad",
              "Download.PartTtlHours": 0,
              "Download.SpaceMarginMB": 9999,
              "Download.MaxFileSizeMB": -5
            }
            """);
        var defaults = DownloadSettings.CreateDefault(ws.Root);
        var fallback = IndexerSettings.LoadDownloadSettings(outOfRangePath, ws.Root);
        Assert.Equal(defaults, fallback);

        // Corrupt JSON falls back cleanly
        var corruptPath = ws.Full("data/corrupt.json");
        File.WriteAllText(corruptPath, "{ not json");
        Assert.Equal(defaults, IndexerSettings.LoadDownloadSettings(corruptPath, ws.Root));
        Assert.False(IndexerSettings.LoadAllowOutsideProgramDirWrite(corruptPath));
    }

    [Fact]
    public void DownloadPathPolicy_FileName_Boundary_SessionDir_VerifyPart_AndStaleCleanup()
    {
        using var ws = new TestWorkspace("dl-policy");
        var progDir = ws.Full("app");
        var outsideDir = ws.Full("outside");
        var settingsPath = Path.Combine(progDir, "data", "indexer.json");
        Directory.CreateDirectory(Path.Combine(progDir, "library"));
        Directory.CreateDirectory(outsideDir);

        // Filename sanitization, reserved Windows name guard, and 180-char cap
        var sanitized = DownloadPathPolicy.BuildSafeFileName("Bad:/\\*?\"<>|Name", "1.0", "com.demo.app");
        Assert.EndsWith(".ipa", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain(':', sanitized);
        Assert.DoesNotContain('/', sanitized);

        var longName = DownloadPathPolicy.BuildSafeFileName(new string('A', 220), "1.0.0", "com.long.bundle");
        Assert.True(longName.Length <= DownloadPathPolicy.MaxFileNameLength);
        Assert.EndsWith(".ipa", longName, StringComparison.Ordinal);

        var reservedCon = DownloadPathPolicy.ClampIpaFileName("CON", "");
        Assert.Equal("_CON.ipa", reservedCon);
        var reservedNul = DownloadPathPolicy.ClampIpaFileName("NUL.ipa", "");
        Assert.Equal("_NUL.ipa.ipa", reservedNul);
        var reservedLpt = DownloadPathPolicy.ClampIpaFileName("LPT1", "");
        Assert.Equal("_LPT1.ipa", reservedLpt);
        var suffixedLpt = DownloadPathPolicy.ClampIpaFileName("LPT1", " (2)");
        Assert.Equal("LPT1 (2).ipa", suffixedLpt);

        // Collision suffix on existing .ipa AND existing .ipa.part, keeping <= 180 chars
        var libDir = Path.Combine(progDir, "library");
        var firstTarget = DownloadPathPolicy.AllocateUniqueTargetPath(libDir, "Game", "1.0", "com.game");
        File.WriteAllBytes(firstTarget, [1]);
        var secondTarget = DownloadPathPolicy.AllocateUniqueTargetPath(libDir, "Game", "1.0", "com.game");
        Assert.Contains(" (2).ipa", secondTarget, StringComparison.Ordinal);
        File.WriteAllBytes(secondTarget + ".part", [1]);
        var thirdTarget = DownloadPathPolicy.AllocateUniqueTargetPath(libDir, "Game", "1.0", "com.game");
        Assert.Contains(" (3).ipa", thirdTarget, StringComparison.Ordinal);

        // Write boundary: live re-read without caching
        Assert.True(DownloadPathPolicy.ValidateWriteDirectory(libDir, progDir, settingsPath).Ok);
        var blockedOutside = DownloadPathPolicy.ValidateWriteDirectory(outsideDir, progDir, settingsPath);
        Assert.False(blockedOutside.Ok);
        Assert.False(DownloadPathPolicy.ValidateWriteDirectory(@"\\server\share\dir", progDir, settingsPath).Ok);

        // Session dir falls back to program when outside & switch is off
        var targetSessionSettings = new DownloadSettings
        {
            Dir = outsideDir,
            SessionDirMode = DownloadSettings.SessionTarget,
        };
        var fallbackSess = DownloadPathPolicy.ResolveSessionFilePath(targetSessionSettings, progDir, settingsPath);
        Assert.Equal(Path.Combine(progDir, "data", "aria2.session"), fallbackSess);

        // Flip switch on -> boundary immediately opens without caching!
        IndexerSettings.SaveAllowOutsideProgramDirWrite(true, settingsPath);
        Assert.True(DownloadPathPolicy.ValidateWriteDirectory(outsideDir, progDir, settingsPath).Ok);
        var targetSess = DownloadPathPolicy.ResolveSessionFilePath(targetSessionSettings, progDir, settingsPath);
        Assert.Equal(Path.Combine(outsideDir, ".aria2", "aria2.session"), targetSess);

        // Drive root custom session dir falls back to program
        var rootSess = DownloadPathPolicy.ResolveSessionFilePath(
            new DownloadSettings { SessionDirMode = DownloadSettings.SessionCustom, SessionDirCustom = Path.GetPathRoot(progDir)! },
            progDir, settingsPath);
        Assert.Equal(Path.Combine(progDir, "data", "aria2.session"), rootSess);

        // MaxFileSizeMB pre-download check
        var sizeBlocked = DownloadPathPolicy.CheckPreDownload(
            libDir, 50L * 1024 * 1024, new DownloadSettings { MaxFileSizeMB = 10, SpaceMarginMB = 0 }, progDir, settingsPath);
        Assert.False(sizeBlocked.Ok);

        // Post-download part verification
        var payload = CreateMinimalZipPayload(8192);
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var validPart = Path.Combine(libDir, "valid.ipa.part");
        File.WriteAllBytes(validPart, payload);
        Assert.True(DownloadPathPolicy.VerifyPartFile(validPart, payload.Length, sha).Ok);
        Assert.False(DownloadPathPolicy.VerifyPartFile(validPart, payload.Length + 1, sha).Ok);
        Assert.False(DownloadPathPolicy.VerifyPartFile(validPart, payload.Length, new string('f', 64)).Ok);

        var badMagic = Path.Combine(libDir, "bad.ipa.part");
        File.WriteAllBytes(badMagic, Encoding.ASCII.GetBytes("NOT-A-ZIP-PAYLOAD"));
        Assert.False(DownloadPathPolicy.VerifyPartFile(badMagic, 0, null).Ok);

        var emptyPart = Path.Combine(libDir, "empty.ipa.part");
        File.WriteAllBytes(emptyPart, []);
        Assert.False(DownloadPathPolicy.VerifyPartFile(emptyPart, 0, null).Ok);

        // TryDeleteSingleFile rejects directories
        var (dirDeleted, _) = DownloadPathPolicy.TryDeleteSingleFile(outsideDir);
        Assert.False(dirDeleted);
        Assert.True(Directory.Exists(outsideDir));

        // CleanupExpiredPartFiles removes expired .part + .part.aria2 and keeps fresh ones
        var now = DateTime.UtcNow;
        var stalePart = Path.Combine(libDir, "stale.ipa.part");
        var staleAria2 = stalePart + ".aria2";
        var freshPart = Path.Combine(libDir, "fresh.ipa.part");
        File.WriteAllBytes(stalePart, [1, 2, 3]);
        File.WriteAllBytes(staleAria2, [4]);
        File.WriteAllBytes(freshPart, [5]);
        File.SetLastWriteTimeUtc(stalePart, now.AddHours(-30));
        File.SetLastWriteTimeUtc(freshPart, now.AddHours(-1));

        var cleaned = DownloadPathPolicy.CleanupExpiredPartFiles(libDir, partTtlHours: 24, progDir, settingsPath, utcNow: now);
        Assert.Equal(1, cleaned);
        Assert.False(File.Exists(stalePart));
        Assert.False(File.Exists(staleAria2));
        Assert.True(File.Exists(freshPart));
    }

    [Fact]
    public void IndexDatabase_GetSources_ReadsSha256AndNullEmptyCleanly()
    {
        using var ws = new TestWorkspace("index-sources-sha");
        var dbPath = ws.Full("index.db");
        var validSha = new string('a', 64);

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
                CREATE TABLE entries (
                    id TEXT PRIMARY KEY,
                    bundle_id TEXT NOT NULL,
                    bundle_lower TEXT NOT NULL,
                    title TEXT, version TEXT, min_os TEXT, min_os_num INTEGER,
                    size INTEGER, size_exact INTEGER, device_mask INTEGER,
                    icon TEXT, category TEXT, subgenre TEXT, developer TEXT
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
                INSERT INTO entries VALUES ('e1', 'com.test', 'com.test', 'Test', '1.0', '5.0', 50000, 1024, 1, 2, '', '', '', '');
                INSERT INTO sources (entry_id, source_id, url, page, size, sha256) VALUES ('e1', 'm1', 'http://127.0.0.1/1.ipa', '', 1024, '{validSha.ToUpperInvariant()}');
                INSERT INTO sources (entry_id, source_id, url, page, size, sha256) VALUES ('e1', 'm2', 'http://127.0.0.1/2.ipa', '', 1024, '');
                INSERT INTO sources (entry_id, source_id, url, page, size, sha256) VALUES ('e1', 'm3', 'http://127.0.0.1/3.ipa', '', 1024, NULL);
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var db = new IndexDatabase(dbPath);
        var sources = db.GetSources("e1");
        Assert.Equal(3, sources.Count);
        Assert.Equal(validSha, sources[0].Sha256);
        Assert.Null(sources[1].Sha256);
        Assert.Null(sources[2].Sha256);
    }

    [Fact]
    public void DownloadEngine_ClassifyAria2Error_MapsHttpStatusesAndDiskFull()
    {
        Assert.Equal(
            DownloadEngine.AttemptOutcomeKind.DeadLink,
            DownloadEngine.ClassifyAria2Error(new Aria2StatusSnapshot("g1", "error", 0, 0, 0, 0, "22", "The response status is not successful. status=404", null)).Kind);
        Assert.Equal(
            DownloadEngine.AttemptOutcomeKind.DeadLink,
            DownloadEngine.ClassifyAria2Error(new Aria2StatusSnapshot("g1", "error", 0, 0, 0, 0, "22", "status=410 Gone", null)).Kind);
        Assert.Equal(
            DownloadEngine.AttemptOutcomeKind.Unavailable,
            DownloadEngine.ClassifyAria2Error(new Aria2StatusSnapshot("g1", "error", 0, 0, 0, 0, "22", "The response status is not successful. status=403", null)).Kind);
        Assert.Equal(
            DownloadEngine.AttemptOutcomeKind.DiskFull,
            DownloadEngine.ClassifyAria2Error(new Aria2StatusSnapshot("g1", "error", 1000, 0, 0, 0, "9", "not enough disk space", null)).Kind);
        Assert.Equal(
            DownloadEngine.AttemptOutcomeKind.TransientError,
            DownloadEngine.ClassifyAria2Error(new Aria2StatusSnapshot("g1", "error", 0, 0, 0, 0, "22", "The response status is not successful. status=503", null)).Kind);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DownloadEngine_RealAria2_ValidZip_VerifiesAndMovesAtomically()
    {
        var verified = VerifyToolkitAria2();
        var payload = CreateMinimalZipPayload(16 * 1024);
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        using var ws = new TestWorkspace("aria2-valid");
        var settingsPath = PrepareTestSettings(ws.Root);
        await using var server = new LocalHttpServer(ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength64 = payload.Length;
            ctx.Response.OutputStream.Write(payload, 0, payload.Length);
            ctx.Response.Close();
        });

        var tcs = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var importedPath = "";
        await using var engine = new DownloadEngine(
            _ => [new SourceRow("local", server.Url("/good.ipa"), "", payload.Length, sha)],
            new InlineProgress(snap =>
            {
                if (snap.Status is DownloadJobStatus.Succeeded or DownloadJobStatus.Failed or DownloadJobStatus.Cancelled)
                    tcs.TrySetResult(snap);
            }),
            programDir: ws.Root,
            settingsPath: settingsPath,
            preVerifiedAria2: verified)
        {
            OnDownloadCompletedAsync = (info, _) =>
            {
                importedPath = info.FilePath;
                return Task.FromResult<string?>("imported-ok");
            },
        };

        var row = SampleIndexRow("valid-1", "Valid App", "com.valid.app");
        var job = new DownloadJobRow(row, DownloadQueueColumns.OrderedVisible(DownloadQueueColumns.Default));
        engine.EnqueueJob(job);

        var ct = TestContext.Current.CancellationToken;
        var final = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        Assert.Equal(DownloadJobStatus.Succeeded, final.Status);
        Assert.NotNull(final.TargetPath);
        Assert.True(File.Exists(final.TargetPath));
        Assert.Equal(payload, File.ReadAllBytes(final.TargetPath!));
        Assert.False(File.Exists(final.TargetPath + ".part"));
        Assert.False(File.Exists(final.TargetPath + ".part.aria2"));
        Assert.Equal(final.TargetPath, importedPath);
        Assert.Equal("imported-ok", final.Note);

        await engine.DisposeAsync();
        AssertZeroAria2ForPid();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DownloadEngine_RealAria2_CorruptedNonZip_DeletesPartAndFailsWithoutRetry()
    {
        var verified = VerifyToolkitAria2();
        var badPayload = Encoding.ASCII.GetBytes(new string('X', 16 * 1024));
        var hits = 0;

        using var ws = new TestWorkspace("aria2-corrupt");
        var settingsPath = PrepareTestSettings(ws.Root);
        await using var server = new LocalHttpServer(ctx =>
        {
            Interlocked.Increment(ref hits);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = badPayload.Length;
            ctx.Response.OutputStream.Write(badPayload, 0, badPayload.Length);
            ctx.Response.Close();
        });

        var tcs = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = new DownloadEngine(
            _ => [new SourceRow("local", server.Url("/corrupt.ipa"), "", badPayload.Length, null)],
            new InlineProgress(snap =>
            {
                if (snap.Status is DownloadJobStatus.Succeeded or DownloadJobStatus.Failed)
                    tcs.TrySetResult(snap);
            }),
            programDir: ws.Root,
            settingsPath: settingsPath,
            preVerifiedAria2: verified);

        var row = SampleIndexRow("corrupt-1", "Corrupt App", "com.corrupt.app");
        engine.EnqueueJob(new DownloadJobRow(row, DownloadQueueColumns.OrderedVisible(DownloadQueueColumns.Default)));

        var ct = TestContext.Current.CancellationToken;
        var final = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        Assert.Equal(DownloadJobStatus.Failed, final.Status);
        Assert.Equal(Strings.Policy_Error_InvalidZipHeader, final.Note);
        Assert.Equal(1, hits);
        Assert.False(File.Exists(final.TargetPath));
        Assert.False(File.Exists(final.TargetPath + ".part"));
        Assert.False(File.Exists(final.TargetPath + ".part.aria2"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DownloadEngine_RealAria2_Http404MirrorFailover_MarksDeadAndUsesSecondMirror()
    {
        var verified = VerifyToolkitAria2();
        var payload = CreateMinimalZipPayload(16 * 1024);
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var deadHits = 0;
        var goodHits = 0;

        using var ws = new TestWorkspace("aria2-failover");
        var settingsPath = PrepareTestSettings(ws.Root);
        await using var server = new LocalHttpServer(ctx =>
        {
            if (ctx.Request.Url?.AbsolutePath == "/dead.ipa")
            {
                Interlocked.Increment(ref deadHits);
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }
            Interlocked.Increment(ref goodHits);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = payload.Length;
            ctx.Response.OutputStream.Write(payload, 0, payload.Length);
            ctx.Response.Close();
        });

        var deadUrl = server.Url("/dead.ipa");
        var goodUrl = server.Url("/good.ipa");
        var tcs = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var engine = new DownloadEngine(
            _ =>
            [
                new SourceRow("m1", deadUrl, "", payload.Length, sha),
                new SourceRow("m2", goodUrl, "", payload.Length, sha),
            ],
            new InlineProgress(snap =>
            {
                if (snap.Status is DownloadJobStatus.Succeeded or DownloadJobStatus.Failed)
                    tcs.TrySetResult(snap);
            }),
            programDir: ws.Root,
            settingsPath: settingsPath,
            preVerifiedAria2: verified);

        var row = SampleIndexRow("failover-1", "Failover App", "com.failover.app");
        engine.EnqueueJob(new DownloadJobRow(row, DownloadQueueColumns.OrderedVisible(DownloadQueueColumns.Default)));

        var ct = TestContext.Current.CancellationToken;
        var final = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        Assert.Equal(DownloadJobStatus.Succeeded, final.Status);
        Assert.Equal(1, deadHits);
        Assert.Equal(1, goodHits);
        Assert.True(engine.IsDeadUrl(deadUrl));
        Assert.False(engine.IsDeadUrl(goodUrl));
        Assert.True(File.Exists(engine.DeadLinksFilePath));
        Assert.Contains(deadUrl, File.ReadAllText(engine.DeadLinksFilePath), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DownloadEngine_RealAria2_AllDead_FailsThenManualReEnqueueRetriesAndSucceeds()
    {
        var verified = VerifyToolkitAria2();
        var payload = CreateMinimalZipPayload(16 * 1024);
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var serve200 = false;

        using var ws = new TestWorkspace("aria2-retry-dead");
        var settingsPath = PrepareTestSettings(ws.Root);
        await using var server = new LocalHttpServer(ctx =>
        {
            if (!Volatile.Read(ref serve200))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = payload.Length;
            ctx.Response.OutputStream.Write(payload, 0, payload.Length);
            ctx.Response.Close();
        });

        var mirrorUrl = server.Url("/flaky.ipa");
        var attempt1Tcs = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempt2Tcs = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var phase = 1;

        await using var engine = new DownloadEngine(
            _ => [new SourceRow("m1", mirrorUrl, "", payload.Length, sha)],
            new InlineProgress(snap =>
            {
                if (Volatile.Read(ref phase) == 1 && snap.Status is DownloadJobStatus.Succeeded or DownloadJobStatus.Failed)
                    attempt1Tcs.TrySetResult(snap);
                else if (Volatile.Read(ref phase) == 2 && snap.Status is DownloadJobStatus.Succeeded or DownloadJobStatus.Failed)
                    attempt2Tcs.TrySetResult(snap);
            }),
            programDir: ws.Root,
            settingsPath: settingsPath,
            preVerifiedAria2: verified);

        var row = SampleIndexRow("dead-retry-1", "Retry App", "com.retry.app");
        var job = new DownloadJobRow(row, DownloadQueueColumns.OrderedVisible(DownloadQueueColumns.Default));
        engine.EnqueueJob(job);

        var ct = TestContext.Current.CancellationToken;
        var first = await attempt1Tcs.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        Assert.Equal(DownloadJobStatus.Failed, first.Status);
        Assert.Equal(Strings.Download_Error_DeadLink, first.Note);
        Assert.True(engine.IsDeadUrl(mirrorUrl));

        // Server recovers and user manually re-enqueues -> dead flag for this entry is cleared and download succeeds
        Volatile.Write(ref phase, 2);
        Volatile.Write(ref serve200, true);
        engine.EnqueueJob(job);

        var second = await attempt2Tcs.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        Assert.Equal(DownloadJobStatus.Succeeded, second.Status);
        Assert.False(engine.IsDeadUrl(mirrorUrl));
        Assert.True(File.Exists(second.TargetPath));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DownloadEngine_RealAria2_MidDownloadCancel_CancelsCleanlyAndStopsAria2()
    {
        var verified = VerifyToolkitAria2();
        var chunk = new byte[4096];
        chunk[0] = 0x50;
        chunk[1] = 0x4B;
        chunk[2] = 0x03;
        chunk[3] = 0x04;

        using var ws = new TestWorkspace("aria2-cancel");
        var settingsPath = PrepareTestSettings(ws.Root, keepPartFiles: false);
        using var streamCts = new CancellationTokenSource();
        await using var server = new LocalHttpServer(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = 1024L * 1024L;
            try
            {
                for (var i = 0; i < 256 && !streamCts.IsCancellationRequested; i++)
                {
                    await ctx.Response.OutputStream.WriteAsync(chunk, streamCts.Token);
                    await ctx.Response.OutputStream.FlushAsync(streamCts.Token);
                    await Task.Delay(80, streamCts.Token);
                }
            }
            catch
            {
                // 连接在取消后关闭
            }
            finally
            {
                try { ctx.Response.Close(); } catch { }
            }
        });

        var downloadingTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledTcs = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var engine = new DownloadEngine(
            _ => [new SourceRow("slow", server.Url("/slow.ipa"), "", 1024L * 1024L, null)],
            new InlineProgress(snap =>
            {
                if (snap.Status == DownloadJobStatus.Downloading && snap.Percent > 0) downloadingTcs.TrySetResult();
                if (snap.Status == DownloadJobStatus.Cancelled) cancelledTcs.TrySetResult(snap);
            }),
            programDir: ws.Root,
            settingsPath: settingsPath,
            preVerifiedAria2: verified);

        var row = SampleIndexRow("cancel-1", "Cancel App", "com.cancel.app");
        var job = new DownloadJobRow(row, DownloadQueueColumns.OrderedVisible(DownloadQueueColumns.Default));
        engine.EnqueueJob(job);

        var ct = TestContext.Current.CancellationToken;
        await downloadingTcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        await engine.CancelJobAsync(row.Id);
        streamCts.Cancel();

        var cancelled = await cancelledTcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        Assert.Equal(DownloadJobStatus.Cancelled, cancelled.Status);
        if (!string.IsNullOrWhiteSpace(cancelled.TargetPath))
        {
            Assert.False(File.Exists(cancelled.TargetPath + ".part"));
            Assert.False(File.Exists(cancelled.TargetPath + ".part.aria2"));
        }

        await engine.DisposeAsync();
        AssertZeroAria2ForPid();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Aria2Daemon_BuildArguments_ControlFileMode_And_StaleSessionDeleted()
    {
        var verified = VerifyToolkitAria2();
        using var ws = new TestWorkspace("aria2-session-args");
        var settingsPath = PrepareTestSettings(ws.Root);
        var sessionFile = Path.Combine(ws.Root, "data", "aria2.session");
        Directory.CreateDirectory(Path.GetDirectoryName(sessionFile)!);
        File.WriteAllText(sessionFile, "http://127.0.0.1:9/untracked.ipa\n");

        var sameAsPartArgs = Aria2Daemon.BuildArguments(
            new DownloadSettings { ControlFileMode = DownloadSettings.ControlSameAsPart },
            port: 16800,
            secret: "abc",
            sessionFile: sessionFile);
        Assert.DoesNotContain(sameAsPartArgs, a => a.StartsWith("--input-file=", StringComparison.Ordinal));
        Assert.DoesNotContain(sameAsPartArgs, a => a.StartsWith("--save-session=", StringComparison.Ordinal));
        Assert.Contains("--auto-save-interval=30", sameAsPartArgs);

        var sessionArgs = Aria2Daemon.BuildArguments(
            new DownloadSettings { ControlFileMode = DownloadSettings.ControlSession },
            port: 16800,
            secret: "abc",
            sessionFile: sessionFile);
        Assert.DoesNotContain(sessionArgs, a => a.StartsWith("--input-file=", StringComparison.Ordinal));
        Assert.Contains($"--save-session={sessionFile}", sessionArgs);
        Assert.Contains("--save-session-interval=30", sessionArgs);
        Assert.Contains("--auto-save-interval=30", sessionArgs);

        var settings = IndexerSettings.LoadDownloadSettings(settingsPath, ws.Root);
        await using (var daemon = await Aria2Daemon.StartAsync(verified, settings, ws.Root, settingsPath, TestContext.Current.CancellationToken))
        {
            Assert.False(File.Exists(sessionFile));
            Assert.False(daemon.HasExited);
        }

        AssertZeroAria2ForPid();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ProxyBypass_Conversion_RememberLastDir_And_ChangeGlobalOption_WorkCleanly()
    {
        Assert.Equal("localhost,127.0.0.1,::1", Aria2RpcClient.FormatAria2NoProxy("<local>"));
        Assert.Equal(
            "localhost,127.0.0.1,::1,*.internal.corp,10.0.0.0/8",
            Aria2RpcClient.FormatAria2NoProxy("<local>; *.internal.corp ; localhost ; 10.0.0.0/8"));
        Assert.Equal("", Aria2RpcClient.FormatAria2NoProxy("   "));

        var manualOpts = Aria2RpcClient.ResolveProxyOptions(new DownloadSettings
        {
            ProxyMode = DownloadSettings.ProxyManual,
            ProxyUrl = "http://127.0.0.1:8888",
            ProxyBypass = "<local>;corp.example",
        }, "https://example.com/app.ipa");
        Assert.Equal("http://127.0.0.1:8888", manualOpts["all-proxy"]);
        Assert.Equal("localhost,127.0.0.1,::1,corp.example", manualOpts["no-proxy"]);

        using var ws = new TestWorkspace("remember-last-dir");
        var settingsPath = PrepareTestSettings(ws.Root);
        var defaultLib = DownloadSettings.DefaultDir(ws.Root);
        var altDir1 = ws.Full("alt1");
        var altDir2 = ws.Full("alt2");

        Assert.True(IndexerSettings.RecordLastDownloadDir(altDir1, settingsPath, ws.Root));
        Assert.Equal(Path.GetFullPath(altDir1), IndexerSettings.LoadDownloadSettings(settingsPath, ws.Root).Dir);

        var cur = IndexerSettings.LoadDownloadSettings(settingsPath, ws.Root);
        IndexerSettings.SaveDownloadSettings(cur with { RememberLastDir = false }, settingsPath, ws.Root);
        Assert.False(IndexerSettings.RecordLastDownloadDir(altDir2, settingsPath, ws.Root));
        Assert.Equal(Path.GetFullPath(altDir1), IndexerSettings.LoadDownloadSettings(settingsPath, ws.Root).Dir);

        // Verify ChangeGlobalOptionAsync succeeds against real aria2c.exe
        var verified = VerifyToolkitAria2();
        IndexerSettings.SaveDownloadSettings(cur with { Dir = defaultLib, RememberLastDir = true }, settingsPath, ws.Root);
        await using var daemon = await Aria2Daemon.StartAsync(
            verified,
            IndexerSettings.LoadDownloadSettings(settingsPath, ws.Root),
            ws.Root,
            settingsPath,
            TestContext.Current.CancellationToken);
        var updatedOpts = DownloadEngine.BuildGlobalAria2Options(new DownloadSettings
        {
            Concurrency = 3,
            SpeedLimit = "2M",
            PerTaskSpeedLimit = "512K",
            Connections = 4,
            Splits = 4,
            MinSplitSize = "2M",
            RetryWait = 5,
            Timeout = 45,
            Continue = true,
            UserAgent = "TestAgent/1.0",
        });
        await daemon.Rpc.ChangeGlobalOptionAsync(updatedOpts, TestContext.Current.CancellationToken);
        await daemon.DisposeAsync();
        AssertZeroAria2ForPid();
    }

    [Fact]
    public void DownloadPathPolicy_RejectsTempAndProfileRoots_AllowsDriveRootCheck_AndBlocksRealJunction()
    {
        using var ws = new TestWorkspace("policy-hardening");
        var progDir = ws.Full("app");
        var settingsPath = Path.Combine(progDir, "data", "indexer.json");
        Directory.CreateDirectory(Path.Combine(progDir, "library"));
        IndexerSettings.SaveAllowOutsideProgramDirWrite(true, settingsPath);

        // Even with AllowOutsideProgramDirWrite=true, system temp and profile config roots remain forbidden (P2-23)
        Assert.False(DownloadPathPolicy.ValidateWriteDirectory(Path.GetTempPath(), progDir, settingsPath).Ok);
        Assert.False(DownloadPathPolicy.ValidateWriteDirectory(Path.Combine(Path.GetTempPath(), "nested"), progDir, settingsPath).Ok);

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
            Assert.False(DownloadPathPolicy.ValidateWriteDirectory(appData, progDir, settingsPath).Ok);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            Assert.False(DownloadPathPolicy.ValidateWriteDirectory(localAppData, progDir, settingsPath).Ok);

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
            Assert.False(DownloadPathPolicy.ValidateWriteDirectory(userProfile, progDir, settingsPath).Ok);

        // Drive root prefix check (P3-11)
        Assert.True(DownloadPathPolicy.IsUnderDirectory(@"C:\Apps\downloads", @"C:\"));
        Assert.False(DownloadPathPolicy.IsUnderDirectory(@"D:\Apps\downloads", @"C:\"));

        // Real NTFS junction reparse point block (P2-24)
        var targetDir = ws.Full("real-target");
        var junctionDir = ws.Full("junction-link");
        Directory.CreateDirectory(targetDir);
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junctionDir}\" \"{targetDir}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi)!;
            proc.WaitForExit(5000);
            Assert.Equal(0, proc.ExitCode);
            Assert.True(Directory.Exists(junctionDir));

            var directCheck = DownloadPathPolicy.ValidateWriteDirectory(junctionDir, progDir, settingsPath);
            Assert.False(directCheck.Ok);

            var nestedCheck = DownloadPathPolicy.ValidateWriteDirectory(Path.Combine(junctionDir, "sub"), progDir, settingsPath);
            Assert.False(nestedCheck.Ok);
        }
        finally
        {
            try
            {
                if (Directory.Exists(junctionDir))
                    Directory.Delete(junctionDir, recursive: false);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DownloadEngine_RealAria2_UnknownSourceSize_ExceedsMaxFileSize_AbortsAndDeletesPart()
    {
        var verified = VerifyToolkitAria2();
        var bigPayload = CreateMinimalZipPayload(2 * 1024 * 1024);

        using var ws = new TestWorkspace("aria2-unknown-size-limit");
        var settingsPath = Path.Combine(ws.Root, "data", "indexer.json");
        var dlDir = Path.Combine(ws.Root, "library");
        Directory.CreateDirectory(dlDir);
        IndexerSettings.SaveDownloadSettings(new DownloadSettings
        {
            Dir = dlDir,
            Connections = 1,
            Splits = 1,
            Concurrency = 1,
            RetryTimes = 0,
            RetryWait = 1,
            Timeout = 15,
            ProxyMode = DownloadSettings.ProxyNone,
            KeepPartFiles = false,
            SpaceMarginMB = 1,
            MaxFileSizeMB = 1,
        }, settingsPath, ws.Root);

        var testCt = TestContext.Current.CancellationToken;
        await using var server = new LocalHttpServer(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength64 = bigPayload.Length;
            try
            {
                await ctx.Response.OutputStream.WriteAsync(bigPayload, testCt);
                await ctx.Response.OutputStream.FlushAsync(testCt);
            }
            catch
            {
                // Client aborted when exceeding MaxFileSizeMB
            }
            finally
            {
                try { ctx.Response.Close(); } catch { }
            }
        });

        var tcs = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = new DownloadEngine(
            _ => [new SourceRow("local", server.Url("/oversize.ipa"), "", 0, null)],
            new InlineProgress(snap =>
            {
                if (snap.Status is DownloadJobStatus.Succeeded or DownloadJobStatus.Failed)
                    tcs.TrySetResult(snap);
            }),
            programDir: ws.Root,
            settingsPath: settingsPath,
            preVerifiedAria2: verified);

        var row = SampleIndexRow("oversize-1", "Oversize App", "com.oversize.app");
        engine.EnqueueJob(new DownloadJobRow(row, DownloadQueueColumns.OrderedVisible(DownloadQueueColumns.Default)));

        var final = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.Equal(DownloadJobStatus.Failed, final.Status);
        Assert.Contains("1 MB", final.Note ?? "", StringComparison.Ordinal);
        Assert.False(File.Exists(final.TargetPath));
        Assert.False(File.Exists(final.TargetPath + ".part"));
        Assert.False(File.Exists(final.TargetPath + ".part.aria2"));

        await engine.DisposeAsync();
        AssertZeroAria2ForPid();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DownloadEngine_RealAria2_StallReconnectWorksWithZeroRetryTimes_And_WaitingResetsTimer()
    {
        var verified = VerifyToolkitAria2();
        var payload = CreateMinimalZipPayload(16 * 1024);
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var hits = 0;

        using var ws = new TestWorkspace("aria2-stall-retry0");
        var settingsPath = Path.Combine(ws.Root, "data", "indexer.json");
        var dlDir = Path.Combine(ws.Root, "library");
        Directory.CreateDirectory(dlDir);
        IndexerSettings.SaveDownloadSettings(new DownloadSettings
        {
            Dir = dlDir,
            Connections = 1,
            Splits = 1,
            Concurrency = 1,
            RetryTimes = 0,
            RetryWait = 1,
            Timeout = 10,
            ProxyMode = DownloadSettings.ProxyNone,
            KeepPartFiles = true,
            SpaceMarginMB = 1,
        }, settingsPath, ws.Root);

        var testCt = TestContext.Current.CancellationToken;
        using var stallRelease = CancellationTokenSource.CreateLinkedTokenSource(testCt);
        await using var server = new LocalHttpServer(async ctx =>
        {
            var hit = Interlocked.Increment(ref hits);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength64 = payload.Length;
            if (hit == 1)
            {
                // Send first 512 bytes then stall indefinitely until test finishes
                try
                {
                    await ctx.Response.OutputStream.WriteAsync(payload.AsMemory(0, 512), stallRelease.Token);
                    await ctx.Response.OutputStream.FlushAsync(stallRelease.Token);
                    await Task.Delay(TimeSpan.FromSeconds(15), stallRelease.Token);
                }
                catch
                {
                    // Cancelled
                }
                finally
                {
                    try { ctx.Response.Abort(); } catch { }
                }
                return;
            }

            await ctx.Response.OutputStream.WriteAsync(payload, testCt);
            ctx.Response.Close();
        });

        var tcs = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = new DownloadEngine(
            _ => [new SourceRow("local", server.Url("/stall.ipa"), "", payload.Length, sha)],
            new InlineProgress(snap =>
            {
                if (snap.Status is DownloadJobStatus.Succeeded or DownloadJobStatus.Failed)
                    tcs.TrySetResult(snap);
            }),
            programDir: ws.Root,
            settingsPath: settingsPath,
            preVerifiedAria2: verified)
        {
            StallTimeoutOverride = TimeSpan.FromMilliseconds(450),
        };

        var row = SampleIndexRow("stall-1", "Stall App", "com.stall.app");
        engine.EnqueueJob(new DownloadJobRow(row, DownloadQueueColumns.OrderedVisible(DownloadQueueColumns.Default)));

        var final = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        stallRelease.Cancel();
        Assert.Equal(DownloadJobStatus.Succeeded, final.Status);
        Assert.True(hits >= 2, $"Expected stall reconnect hit >= 2, got {hits}");

        // Part B: Verify "waiting" status resets stall clock in MonitorDownloadGidAsync via local mock JSON-RPC
        var activePolls = 0;
        var waitingStart = Environment.TickCount64;
        await using var mockRpcServer = new LocalHttpServer(ctx =>
        {
            var elapsed = Environment.TickCount64 - waitingStart;
            string status;
            if (elapsed < 700)
            {
                status = "waiting";
            }
            else
            {
                var activePoll = Interlocked.Increment(ref activePolls);
                status = activePoll == 1 ? "active" : "complete";
            }
            var completed = status == "complete" ? "1024" : "0";
            var resp = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "1",
                result = new
                {
                    gid = "g-wait",
                    status,
                    totalLength = "1024",
                    completedLength = completed,
                    downloadSpeed = "0",
                    connections = "0",
                },
            });
            var bytes = Encoding.UTF8.GetBytes(resp);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        });

        using var rpc = new Aria2RpcClient(mockRpcServer.Port, "secret");
        engine.StallTimeoutOverride = TimeSpan.FromMilliseconds(350);
        var active = new DownloadEngine.ActiveJob("wait-1", "com.wait", "Wait", "1.0", "", 1, CancellationToken.None);
        var outcome = await engine.MonitorDownloadGidAsync(
            rpc,
            active,
            "g-wait",
            dlDir,
            dlSettingsForMock(),
            null,
            testCt);
        Assert.Equal(DownloadEngine.AttemptOutcomeKind.Completed, outcome.Kind);
        Assert.True(Volatile.Read(ref activePolls) >= 2);

        await engine.DisposeAsync();
        AssertZeroAria2ForPid();

        static DownloadSettings dlSettingsForMock() => new() { Timeout = 10, SpaceMarginMB = 0, MaxFileSizeMB = 0 };
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DownloadEngine_RealAria2_PerHostQuota_DoesNotStarveOtherHost()
    {
        var verified = VerifyToolkitAria2();
        var payload = CreateMinimalZipPayload(16 * 1024);
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        using var ws = new TestWorkspace("aria2-perhost-nonstarve");
        var settingsPath = Path.Combine(ws.Root, "data", "indexer.json");
        var dlDir = Path.Combine(ws.Root, "library");
        Directory.CreateDirectory(dlDir);
        IndexerSettings.SaveDownloadSettings(new DownloadSettings
        {
            Dir = dlDir,
            Connections = 1,
            Splits = 1,
            Concurrency = 3,
            RetryTimes = 0,
            RetryWait = 1,
            Timeout = 15,
            ProxyMode = DownloadSettings.ProxyNone,
            KeepPartFiles = false,
            SpaceMarginMB = 1,
        }, settingsPath, ws.Root);

        using var slowRelease = new CancellationTokenSource();
        var hostAConnections = 0;
        await using var serverHostA = new LocalHttpServer(async ctx =>
        {
            Interlocked.Increment(ref hostAConnections);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = payload.Length;
            try
            {
                await ctx.Response.OutputStream.WriteAsync(payload.AsMemory(0, 512), slowRelease.Token);
                await ctx.Response.OutputStream.FlushAsync(slowRelease.Token);
                await Task.Delay(TimeSpan.FromSeconds(15), slowRelease.Token);
                await ctx.Response.OutputStream.WriteAsync(payload.AsMemory(512), slowRelease.Token);
                ctx.Response.Close();
            }
            catch
            {
                try { ctx.Response.Abort(); } catch { }
            }
        }, host: "127.0.0.1");

        await using var serverHostB = new LocalHttpServer(ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = payload.Length;
            ctx.Response.OutputStream.Write(payload, 0, payload.Length);
            ctx.Response.Close();
        }, host: "localhost");

        var hostBDone = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = new DownloadEngine(
            id => id.StartsWith("a", StringComparison.Ordinal)
                ? [new SourceRow("hA", serverHostA.Url($"/{id}.ipa"), "", payload.Length, sha)]
                : [new SourceRow("hB", serverHostB.Url($"/{id}.ipa"), "", payload.Length, sha)],
            new InlineProgress(snap =>
            {
                if (snap.EntryId == "b1" && snap.Status is DownloadJobStatus.Succeeded or DownloadJobStatus.Failed)
                    hostBDone.TrySetResult(snap);
            }),
            programDir: ws.Root,
            settingsPath: settingsPath,
            preVerifiedAria2: verified);

        engine.EnqueueJob(new DownloadJobRow(SampleIndexRow("a1", "HostA 1", "com.a1"), DownloadQueueColumns.OrderedVisible(DownloadQueueColumns.Default)));
        engine.EnqueueJob(new DownloadJobRow(SampleIndexRow("a2", "HostA 2", "com.a2"), DownloadQueueColumns.OrderedVisible(DownloadQueueColumns.Default)));
        engine.EnqueueJob(new DownloadJobRow(SampleIndexRow("a3", "HostA 3", "com.a3"), DownloadQueueColumns.OrderedVisible(DownloadQueueColumns.Default)));
        engine.EnqueueJob(new DownloadJobRow(SampleIndexRow("b1", "HostB 1", "com.b1"), DownloadQueueColumns.OrderedVisible(DownloadQueueColumns.Default)));

        // Even while a1 & a2 occupy 2 slots on 127.0.0.1 and a3 is queued ahead of b1,
        // b1 on localhost must immediately take the 3rd global concurrency slot and complete!
        var bResult = await hostBDone.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal(DownloadJobStatus.Succeeded, bResult.Status);
        Assert.True(Volatile.Read(ref hostAConnections) <= DownloadEngine.MaxPerHostConcurrency);

        slowRelease.Cancel();
        await engine.ShutdownAsync(keepPartFiles: false);
        await engine.DisposeAsync();
        AssertZeroAria2ForPid();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DownloadEngine_RealAria2_SuspendRace_SerializesAndPausesImmediately()
    {
        var verified = VerifyToolkitAria2();
        var payload = CreateMinimalZipPayload(32 * 1024);
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        using var ws = new TestWorkspace("aria2-suspend-race");
        var settingsPath = PrepareTestSettings(ws.Root, keepPartFiles: false);
        var allowFastFinish = false;
        using var serverCts = new CancellationTokenSource();

        await using var server = new LocalHttpServer(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = payload.Length;
            try
            {
                var offset = 0;
                while (offset < payload.Length && !serverCts.IsCancellationRequested)
                {
                    var take = Volatile.Read(ref allowFastFinish) ? payload.Length - offset : Math.Min(1024, payload.Length - offset);
                    await ctx.Response.OutputStream.WriteAsync(payload.AsMemory(offset, take), serverCts.Token);
                    await ctx.Response.OutputStream.FlushAsync(serverCts.Token);
                    offset += take;
                    if (!Volatile.Read(ref allowFastFinish))
                        await Task.Delay(80, serverCts.Token);
                }
                ctx.Response.Close();
            }
            catch
            {
                try { ctx.Response.Abort(); } catch { }
            }
        });

        var downloadingTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pausedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var succeededTcs = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var engine = new DownloadEngine(
            _ => [new SourceRow("local", server.Url("/suspend.ipa"), "", payload.Length, sha)],
            new InlineProgress(snap =>
            {
                if (snap.Status == DownloadJobStatus.Downloading) downloadingTcs.TrySetResult();
                if (snap.Status == DownloadJobStatus.Paused) pausedTcs.TrySetResult();
                if (snap.Status is DownloadJobStatus.Succeeded or DownloadJobStatus.Failed)
                    succeededTcs.TrySetResult(snap);
            }),
            programDir: ws.Root,
            settingsPath: settingsPath,
            preVerifiedAria2: verified);

        var job = new DownloadJobRow(SampleIndexRow("susp-1", "Suspend App", "com.susp"), DownloadQueueColumns.OrderedVisible(DownloadQueueColumns.Default));
        engine.EnqueueJob(job);

        var ct = TestContext.Current.CancellationToken;
        await downloadingTcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);

        // Rapid concurrent suspend/resume/suspend toggles must serialize and end suspended
        var t1 = engine.SetSuspendedAsync(true);
        var t2 = engine.SetSuspendedAsync(false);
        var t3 = engine.SetSuspendedAsync(true);
        await Task.WhenAll(t1, t2, t3);
        await pausedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.False(succeededTcs.Task.IsCompleted);

        Volatile.Write(ref allowFastFinish, true);
        await engine.SetSuspendedAsync(false);
        var final = await succeededTcs.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        serverCts.Cancel();
        Assert.Equal(DownloadJobStatus.Succeeded, final.Status);

        await engine.DisposeAsync();
        AssertZeroAria2ForPid();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DownloadEngine_RealAria2_CancelThenImmediateReenqueue_DoesNotDrop()
    {
        var verified = VerifyToolkitAria2();
        var payload = CreateMinimalZipPayload(16 * 1024);
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var fastMode = false;
        var testCt = TestContext.Current.CancellationToken;
        using var firstStreamCts = CancellationTokenSource.CreateLinkedTokenSource(testCt);

        using var ws = new TestWorkspace("aria2-cancel-reenqueue");
        var settingsPath = PrepareTestSettings(ws.Root, keepPartFiles: false);
        await using var server = new LocalHttpServer(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = payload.Length;
            if (!Volatile.Read(ref fastMode))
            {
                try
                {
                    await ctx.Response.OutputStream.WriteAsync(payload.AsMemory(0, 512), firstStreamCts.Token);
                    await ctx.Response.OutputStream.FlushAsync(firstStreamCts.Token);
                    await Task.Delay(TimeSpan.FromSeconds(10), firstStreamCts.Token);
                }
                catch
                {
                    try { ctx.Response.Abort(); } catch { }
                }
                return;
            }

            await ctx.Response.OutputStream.WriteAsync(payload, testCt);
            ctx.Response.Close();
        });

        var downloadingTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var succeededTcs = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var engine = new DownloadEngine(
            _ => [new SourceRow("local", server.Url("/reenqueue.ipa"), "", payload.Length, sha)],
            new InlineProgress(snap =>
            {
                if (snap.Status == DownloadJobStatus.Downloading) downloadingTcs.TrySetResult();
                if (snap.Status == DownloadJobStatus.Succeeded) succeededTcs.TrySetResult(snap);
            }),
            programDir: ws.Root,
            settingsPath: settingsPath,
            preVerifiedAria2: verified);

        var row = SampleIndexRow("reenq-1", "Reenqueue App", "com.reenq.app");
        var job = new DownloadJobRow(row, DownloadQueueColumns.OrderedVisible(DownloadQueueColumns.Default));
        engine.EnqueueJob(job);

        var ct = TestContext.Current.CancellationToken;
        await downloadingTcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);

        // Cancel and IMMEDIATELY re-enqueue without waiting for RunActiveJobAsync cleanup
        Volatile.Write(ref fastMode, true);
        var cancelTask = engine.CancelJobAsync(row.Id);
        job.BumpGeneration();
        engine.EnqueueJob(job);
        firstStreamCts.Cancel();
        await cancelTask;

        var final = await succeededTcs.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        Assert.Equal(DownloadJobStatus.Succeeded, final.Status);
        Assert.True(File.Exists(final.TargetPath));

        await engine.DisposeAsync();
        AssertZeroAria2ForPid();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DownloadEngine_ShutdownAsync_WaitsForInFlightTasks_AndDeletesParts()
    {
        var verified = VerifyToolkitAria2();
        var payload = CreateMinimalZipPayload(16 * 1024);
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var hookEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlightHookCompleted = false;

        using var ws = new TestWorkspace("aria2-shutdown-wait");
        var settingsPath = PrepareTestSettings(ws.Root, keepPartFiles: false);
        await using var server = new LocalHttpServer(ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = payload.Length;
            ctx.Response.OutputStream.Write(payload, 0, payload.Length);
            ctx.Response.Close();
        });

        var ct = TestContext.Current.CancellationToken;
        await using var engine = new DownloadEngine(
            _ => [new SourceRow("local", server.Url("/inflight.ipa"), "", payload.Length, sha)],
            progress: null,
            programDir: ws.Root,
            settingsPath: settingsPath,
            preVerifiedAria2: verified)
        {
            OnDownloadCompletedAsync = async (_, _) =>
            {
                hookEntered.TrySetResult();
                await Task.Delay(350, ct);
                Volatile.Write(ref inFlightHookCompleted, true);
                return "done";
            },
        };

        engine.EnqueueJob(new DownloadJobRow(SampleIndexRow("shut-1", "Shut App", "com.shut"), DownloadQueueColumns.OrderedVisible(DownloadQueueColumns.Default)));

        await hookEntered.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        await engine.ShutdownAsync(keepPartFiles: false);
        Assert.True(Volatile.Read(ref inFlightHookCompleted), "ShutdownAsync returned before in-flight task completed.");

        await engine.DisposeAsync();
        AssertZeroAria2ForPid();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DownloadEngine_SetSessionDownloadDir_OverridesTargetWithoutOverwritingSettings()
    {
        var verified = VerifyToolkitAria2();
        var payload = CreateMinimalZipPayload(16 * 1024);
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        using var ws = new TestWorkspace("aria2-session-dir-override");
        var settingsPath = PrepareTestSettings(ws.Root, keepPartFiles: false);
        var configuredLib = Path.Combine(ws.Root, "library");
        var sessionLib = Path.Combine(ws.Root, "session-lib");
        Directory.CreateDirectory(sessionLib);

        await using var server = new LocalHttpServer(ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = payload.Length;
            ctx.Response.OutputStream.Write(payload, 0, payload.Length);
            ctx.Response.Close();
        });

        var tcs = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = new DownloadEngine(
            _ => [new SourceRow("local", server.Url("/override.ipa"), "", payload.Length, sha)],
            new InlineProgress(snap =>
            {
                if (snap.Status is DownloadJobStatus.Succeeded or DownloadJobStatus.Failed)
                    tcs.TrySetResult(snap);
            }),
            programDir: ws.Root,
            settingsPath: settingsPath,
            preVerifiedAria2: verified);

        engine.SetSessionDownloadDir(sessionLib);
        engine.EnqueueJob(new DownloadJobRow(SampleIndexRow("sess-1", "Session Dir App", "com.sessdir"), DownloadQueueColumns.OrderedVisible(DownloadQueueColumns.Default)));

        var final = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.Equal(DownloadJobStatus.Succeeded, final.Status);
        Assert.StartsWith(Path.GetFullPath(sessionLib), Path.GetFullPath(final.TargetPath!), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(final.TargetPath));
        Assert.Equal(Path.GetFullPath(configuredLib), IndexerSettings.LoadDownloadSettings(settingsPath, ws.Root).Dir);

        await engine.DisposeAsync();
        AssertZeroAria2ForPid();
    }

    private static string PrepareTestSettings(string rootDir, bool keepPartFiles = true)
    {
        var settingsPath = Path.Combine(rootDir, "data", "indexer.json");
        var dlDir = Path.Combine(rootDir, "library");
        Directory.CreateDirectory(dlDir);
        IndexerSettings.SaveDownloadSettings(new DownloadSettings
        {
            Dir = dlDir,
            Connections = 2,
            Splits = 2,
            Concurrency = 2,
            RetryTimes = 1,
            RetryWait = 1,
            Timeout = 15,
            ProxyMode = DownloadSettings.ProxyNone,
            SessionDirMode = DownloadSettings.SessionProgram,
            KeepPartFiles = keepPartFiles,
            SpaceMarginMB = 1,
        }, settingsPath, rootDir);
        return settingsPath;
    }

    private static void AssertZeroAria2ForPid()
    {
        var leftover = Process.GetProcessesByName("aria2c");
        try
        {
            Assert.Empty(leftover);
        }
        finally
        {
            foreach (var p in leftover) p.Dispose();
        }
    }

    [Fact]
    public void DownloadEngine_ResolveExpectedPartBytes_And_GetSources_RespectSizeExact()
    {
        using var ws = new TestWorkspace("index-size-exact");
        var dbPath = ws.Full("size-exact.db");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
                CREATE TABLE entries (
                    id TEXT PRIMARY KEY, bundle_id TEXT NOT NULL, bundle_lower TEXT NOT NULL,
                    title TEXT, version TEXT, min_os TEXT, min_os_num INTEGER,
                    size INTEGER, size_exact INTEGER, device_mask INTEGER,
                    icon TEXT, category TEXT, subgenre TEXT, developer TEXT
                );
                CREATE TABLE sources (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    entry_id TEXT NOT NULL, source_id TEXT, url TEXT NOT NULL,
                    page TEXT, size INTEGER, sha256 TEXT
                );
                INSERT INTO entries VALUES ('approx', 'com.a', 'com.a', 'Approx', '1.0', '6.0', 60000, 13312, 0, 2, '', '', '', '');
                INSERT INTO entries VALUES ('exact', 'com.b', 'com.b', 'Exact', '1.0', '6.0', 60000, 13312, 1, 2, '', '', '', '');
                INSERT INTO sources (entry_id, source_id, url, page, size, sha256) VALUES ('approx', 'stuffed18', 'https://example.invalid/a.ipa', '', 13312, NULL);
                INSERT INTO sources (entry_id, source_id, url, page, size, sha256) VALUES ('exact', 'mirror', 'https://example.invalid/b.ipa', '', 13312, NULL);
                """;
            cmd.ExecuteNonQuery();
        }

        var db = new IndexDatabase(dbPath);
        var approxSrc = Assert.Single(db.GetSources("approx"));
        var exactSrc = Assert.Single(db.GetSources("exact"));
        Assert.False(approxSrc.SizeExact);
        Assert.True(exactSrc.SizeExact);

        var partPath = ws.Full("payload.ipa.part");
        var payload = new byte[1024];
        payload[0] = (byte)'P';
        payload[1] = (byte)'K';
        payload[2] = 0x03;
        payload[3] = 0x04;
        File.WriteAllBytes(partPath, payload);

        // SizeExact=false uses observedTotalBytes (1024), so approximate 13312 in DB does not falsely fail
        var approxExpected = DownloadEngine.ResolveExpectedPartBytes(approxSrc, payload.Length);
        Assert.Equal(payload.Length, approxExpected);
        Assert.True(DownloadPathPolicy.VerifyPartFile(partPath, approxExpected, null).Ok);

        // SizeExact=true keeps DB exact Size (13312), so mismatched 1024-byte part still fails
        var exactExpected = DownloadEngine.ResolveExpectedPartBytes(exactSrc, payload.Length);
        Assert.Equal(13312, exactExpected);
        Assert.False(DownloadPathPolicy.VerifyPartFile(partPath, exactExpected, null).Ok);
    }

    private sealed class InlineProgress(Action<DownloadProgressSnapshot> handler) : IProgress<DownloadProgressSnapshot>
    {
        public void Report(DownloadProgressSnapshot value) => handler(value);
    }

    private sealed class LocalHttpServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly int _port;

        public LocalHttpServer(Action<HttpListenerContext> handler, string host = "127.0.0.1")
            : this(ctx =>
            {
                handler(ctx);
                return Task.CompletedTask;
            }, host)
        {
        }

        public LocalHttpServer(Func<HttpListenerContext, Task> asyncHandler, string host = "127.0.0.1")
        {
            _host = host;
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                _port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            _listener.Prefixes.Add($"http://{_host}:{_port}/");
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    HttpListenerContext ctx;
                    try
                    {
                        ctx = await _listener.GetContextAsync().WaitAsync(_cts.Token);
                    }
                    catch
                    {
                        break;
                    }
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await asyncHandler(ctx);
                        }
                        catch
                        {
                            try { ctx.Response.Abort(); } catch { }
                        }
                    });
                }
            });
        }

        private readonly string _host;
        public int Port => _port;
        public string Url(string path) => $"http://{_host}:{_port}{path}";

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            _cts.Dispose();
        }
    }
}
