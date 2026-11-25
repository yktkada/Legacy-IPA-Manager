using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IdeviceGui.Services.Data;
using IdeviceGui.Services.Ipc;
using IdeviceGui.Services.Scanning;
using IdeviceIndex;
using Xunit;
using IndexStrings = IdeviceIndex.Resources.Strings;

namespace IdeviceGui.Tests;

[Collection("Aria2Process")]
public sealed class ImportPipeIpcTests
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
                if (File.Exists(Path.Combine(candidate, "aria2c.exe")))
                    return candidate;
            }
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }

    private static string UniquePipeName() => $"IdeviceGui.Import.Test.{Guid.NewGuid():N}";

    [Fact]
    public async Task Hello_RoundTrip_ReturnsProtocolLibraryAndEnabledRoots()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ws = new TestWorkspace("ipc-hello");
        var enabledRoot = Directory.CreateDirectory(ws.Full("roots/enabled")).FullName;
        var disabledRoot = Directory.CreateDirectory(ws.Full("roots/disabled")).FullName;
        var libraryDir = Directory.CreateDirectory(ws.Full("library")).FullName;

        using var db = AppDatabase.Open(ws.Full("data/library.db"));
        var (r1, _) = ScanRootStore.Add(db, enabledRoot);
        var (r2, _) = ScanRootStore.Add(db, disabledRoot);
        Assert.NotNull(r1);
        Assert.NotNull(r2);
        ScanRootStore.SetEnabled(db, r2!.Id, false);

        var pipeName = UniquePipeName();
        var handler = new StubPipeHandler(
            () => new PipeHelloInfo(
                "1.0.1",
                ImportPipeServer.ProtocolVersion,
                libraryDir,
                ScanRootStore.List(db).Where(r => r.Enabled).Select(r => r.Path).ToList()),
            _ => Task.FromResult(new PipeRefreshInfo(true, 0, "ok")),
            (_, _) => Task.FromResult(new ImportFileResult(false, "invalid_request")));

        await using var server = new ImportPipeServer(handler, pipeName);
        server.Start();

        var client = new MainAppPipeClient(pipeName);
        var hello = await client.HelloAsync(ct: ct);

        Assert.True(hello.Ok, hello.Error);
        Assert.Equal(1, hello.Protocol);
        Assert.Equal("1.0.1", hello.AppVersion);
        Assert.Equal(libraryDir, hello.LibraryPath);
        Assert.Single(hello.Roots);
        Assert.Equal(ScanRootStore.Normalize(enabledRoot), ScanRootStore.Normalize(hello.Roots[0]));
    }

    [Fact]
    public async Task ProtocolMismatch_UnknownOp_AndOversizedFrame_KeepServerHealthy()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = UniquePipeName();
        var handler = new StubPipeHandler(
            () => new PipeHelloInfo("1.0.1", 1, @"D:\lib", [@"D:\lib"]),
            _ => Task.FromResult(new PipeRefreshInfo(true, 0, "ok")),
            (_, _) => Task.FromResult(new ImportFileResult(false, "file_missing")));

        await using var server = new ImportPipeServer(handler, pipeName);
        server.Start();

        // 1) protocol_mismatch (v = 99)
        var mismatchResp = await SendRawFrameAsync(pipeName, Encoding.UTF8.GetBytes("""{"v":99,"op":"hello"}"""), ct);
        Assert.NotNull(mismatchResp);
        Assert.False(mismatchResp!["ok"]!.GetValue<bool>());
        Assert.Equal("protocol_mismatch", mismatchResp["error"]!.GetValue<string>());

        // 2) unknown op
        var unknownResp = await SendRawFrameAsync(pipeName, Encoding.UTF8.GetBytes("""{"v":1,"op":"exec"}"""), ct);
        Assert.NotNull(unknownResp);
        Assert.False(unknownResp!["ok"]!.GetValue<bool>());
        Assert.Equal("invalid_request", unknownResp["error"]!.GetValue<string>());

        // 3) oversized frame (> 64 KB) rejected cleanly without breaking server
        using (var rawPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await rawPipe.ConnectAsync(2000, ct);
            var oversizeHeader = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(oversizeHeader, ImportPipeServer.MaxFrameBytes + 1024);
            await rawPipe.WriteAsync(oversizeHeader, ct);
            await rawPipe.FlushAsync(ct);
            // Server rejects header > 64 KB and responds with invalid_request or closes cleanly
            var respHdr = new byte[4];
            var read = await rawPipe.ReadAsync(respHdr, ct);
            if (read == 4)
            {
                var len = BinaryPrimitives.ReadInt32LittleEndian(respHdr);
                var body = new byte[len];
                await ReadExactAsync(rawPipe, body, ct);
                var errNode = JsonNode.Parse(body)!.AsObject();
                Assert.False(errNode["ok"]!.GetValue<bool>());
                Assert.Equal("invalid_request", errNode["error"]!.GetValue<string>());
            }
        }

        // 4) Subsequent valid hello still succeeds cleanly
        var client = new MainAppPipeClient(pipeName);
        var hello = await client.HelloAsync(ct: ct);
        Assert.True(hello.Ok, hello.Error);
        Assert.Equal(1, hello.Protocol);
    }

    [Fact]
    public async Task Hello_WhenNoServerListening_ReturnsMainNotRunning()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new MainAppPipeClient(UniquePipeName());
        var hello = await client.HelloAsync(connectTimeout: TimeSpan.FromMilliseconds(200), ct: ct);

        Assert.False(hello.Ok);
        Assert.Equal("main_not_running", hello.Error);
    }

    [Fact]
    public async Task Import_RealScannerAndPipe_RoundTrip_UpdateInPlace_AndSecurityRejections()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ws = new TestWorkspace("ipc-import");
        var rootDir = Directory.CreateDirectory(ws.Full("root")).FullName;
        var siblingDir = Directory.CreateDirectory(ws.Full("root2")).FullName;
        var outsideDir = Directory.CreateDirectory(ws.Full("outside")).FullName;

        using var db = AppDatabase.Open(ws.Full("data/library.db"));
        Assert.Equal(2, db.UserVersion);
        ScanRootStore.Add(db, rootDir);

        var parsedCallbacks = new List<IReadOnlyList<PackageRow>>();
        var scanner = new IpaScanner(db, onRowsParsed: batch => parsedCallbacks.Add(batch));

        var pipeName = UniquePipeName();
        var handler = new StubPipeHandler(
            () => new PipeHelloInfo("1.0.1", 1, rootDir, [rootDir]),
            _ => Task.FromResult(new PipeRefreshInfo(true, 0, "ok")),
            (path, token) => scanner.ImportFileAsync(path, token));

        await using var server = new ImportPipeServer(handler, pipeName);
        server.Start();
        var client = new MainAppPipeClient(pipeName);

        // 1) Valid IPA inside enabled scan root
        var ipaPath = ws.AddIpa("root/Demo 1.0 (com.demo.app).ipa", "com.demo.app", version: "1.0", displayName: "Demo App");
        var imp1 = await client.ImportAsync(ipaPath, ct);
        Assert.True(imp1.Ok, imp1.Error);
        Assert.True(imp1.PackageId > 0);
        Assert.Equal("com.demo.app", imp1.BundleId);
        Assert.Equal("Demo App", imp1.Name);
        Assert.Equal("1.0", imp1.Version);
        Assert.Single(parsedCallbacks);

        var dbRows = PackageStore.LoadAll(db);
        Assert.Single(dbRows);
        Assert.Equal(ParseStates.Ok, dbRows[0].ParseState);
        Assert.Equal(2, db.UserVersion);

        // 2) Re-importing same file updates in place (same PackageId, no duplicate row)
        ws.RewriteIpaSameBundle("root/Demo 1.0 (com.demo.app).ipa", "com.demo.app", padBytes: 64, version: "2.0");
        var imp2 = await client.ImportAsync(ipaPath, ct);
        Assert.True(imp2.Ok, imp2.Error);
        Assert.Equal(imp1.PackageId, imp2.PackageId);
        Assert.Equal("2.0", imp2.Version);
        Assert.Single(PackageStore.LoadAll(db));
        Assert.Equal(2, parsedCallbacks.Count);

        // 3) Outside root & sibling prefix trap (root2 vs root) -> path_outside_root
        var siblingIpa = ws.AddIpa("root2/Trap 1.0 (com.trap).ipa", "com.trap");
        var outsideIpa = ws.AddIpa("outside/Out 1.0 (com.out).ipa", "com.out");
        Assert.False(MainAppPipeClient.IsPathInsideAnyRoot(siblingIpa, [rootDir]));
        Assert.True(MainAppPipeClient.IsPathInsideAnyRoot(ipaPath, [rootDir]));

        var siblingRes = await client.ImportAsync(siblingIpa, ct);
        Assert.False(siblingRes.Ok);
        Assert.Equal("path_outside_root", siblingRes.Error);

        var outsideRes = await client.ImportAsync(outsideIpa, ct);
        Assert.False(outsideRes.Ok);
        Assert.Equal("path_outside_root", outsideRes.Error);

        // 4) Non-.ipa inside scan root -> path_outside_root
        var txtPath = ws.AddFile("root/notes.txt", 32);
        var txtRes = await client.ImportAsync(txtPath, ct);
        Assert.False(txtRes.Ok);
        Assert.Equal("path_outside_root", txtRes.Error);

        // 5) Missing .ipa inside scan root -> file_missing
        var missingRes = await client.ImportAsync(Path.Combine(rootDir, "missing.ipa"), ct);
        Assert.False(missingRes.Ok);
        Assert.Equal("file_missing", missingRes.Error);

        // 6) Corrupt non-ZIP .ipa inside scan root -> parse_failed
        var corruptPath = ws.AddFile("root/broken.ipa", 256, 0x58);
        var corruptRes = await client.ImportAsync(corruptPath, ct);
        Assert.False(corruptRes.Ok);
        Assert.Equal("parse_failed", corruptRes.Error);
    }

    [Fact]
    public async Task Refresh_RoundTrip_InvokesHandlerAndReturnsSummary()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = UniquePipeName();
        var refreshCalled = 0;

        var handler = new StubPipeHandler(
            () => new PipeHelloInfo("1.0.1", 1, @"D:\lib", [@"D:\lib"]),
            _ =>
            {
                Interlocked.Increment(ref refreshCalled);
                return Task.FromResult(new PipeRefreshInfo(true, 3, "扫描完成：新增 3"));
            },
            (_, _) => Task.FromResult(new ImportFileResult(false, "file_missing")));

        await using var server = new ImportPipeServer(handler, pipeName);
        server.Start();

        var client = new MainAppPipeClient(pipeName);
        var res = await client.RefreshAsync(ct);

        Assert.True(res.Ok, res.Error);
        Assert.Equal(1, refreshCalled);
        Assert.Equal(3, res.Added);
        Assert.Equal("扫描完成：新增 3", res.Summary);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ResolvePostDownloadImportNote_CoversStandaloneNonRootAndRefreshFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ws = new TestWorkspace("ipc-notes");
        var progDir = ws.Full("idx");
        var defaultLib = DownloadSettings.DefaultDir(progDir);
        var infoInLib = new DownloadCompletedInfo("e1", "com.a", "App A", "1.0", Path.Combine(defaultLib, "a.ipa"), defaultLib);
        var infoOutside = new DownloadCompletedInfo("e2", "com.b", "App B", "1.0", ws.Full("other/b.ipa"), ws.Full("other"));

        // AutoImport = false -> returns null immediately
        var noAutoClient = new MainAppPipeClient(UniquePipeName());
        Assert.Null(await IdeviceIndex.MainWindow.ResolvePostDownloadImportNoteAsync(
            noAutoClient, infoInLib, new DownloadSettings { AutoImport = false }, [], progDir, ct));

        // Standalone (main app not running): default library -> standalone fallback note; other dir -> non-root note
        var offlineClient = new MainAppPipeClient(UniquePipeName());
        Assert.Equal(
            IndexStrings.Ipc_Note_StandaloneLibraryFallback,
            await IdeviceIndex.MainWindow.ResolvePostDownloadImportNoteAsync(
                offlineClient, infoInLib, new DownloadSettings(), [], progDir, ct));
        Assert.Equal(
            IndexStrings.Ipc_Note_NotInScanRoot,
            await IdeviceIndex.MainWindow.ResolvePostDownloadImportNoteAsync(
                offlineClient, infoOutside, new DownloadSettings(), [], progDir, ct));

        // Online main app where import fails & AutoRefreshFallback = true -> triggers refresh fallback
        var pipeName = UniquePipeName();
        var rootDir = Directory.CreateDirectory(ws.Full("scan-root")).FullName;
        var rootIpa = Path.Combine(rootDir, "c.ipa");
        var infoInRoot = new DownloadCompletedInfo("e3", "com.c", "App C", "1.0", rootIpa, rootDir);
        var refreshHits = 0;

        var handler = new StubPipeHandler(
            () => new PipeHelloInfo("1.0.1", 1, defaultLib, [rootDir]),
            _ =>
            {
                Interlocked.Increment(ref refreshHits);
                return Task.FromResult(new PipeRefreshInfo(true, 1, "ok"));
            },
            (_, _) => Task.FromResult(new ImportFileResult(false, "parse_failed")));

        await using var server = new ImportPipeServer(handler, pipeName);
        server.Start();

        var onlineClient = new MainAppPipeClient(pipeName);
        var nonRootNote = await IdeviceIndex.MainWindow.ResolvePostDownloadImportNoteAsync(
            onlineClient, infoOutside, new DownloadSettings(), [], progDir, ct);
        Assert.Equal(IndexStrings.Ipc_Note_NotInScanRoot, nonRootNote);

        var refreshedNote = await IdeviceIndex.MainWindow.ResolvePostDownloadImportNoteAsync(
            onlineClient, infoInRoot, new DownloadSettings { AutoImport = true, AutoRefreshFallback = true }, [], progDir, ct);
        Assert.Equal(IndexStrings.Ipc_Note_RefreshedFallback, refreshedNote);
        Assert.Equal(1, refreshHits);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task EndToEnd_LocalHttp_Aria2_DownloadEngine_PipeImport_PersistsIntoLibraryDb()
    {
        var ct = TestContext.Current.CancellationToken;
        var verified = Aria2Verifier.Verify(ToolkitDir);
        Assert.True(verified.Ok, verified.Message);

        using var ws = new TestWorkspace("ipc-e2e");
        var sourceIpaPath = ws.AddIpa("seed/tiny.ipa", "com.e2e.sample", version: "3.2.1", build: "321", displayName: "E2E Sample", padBytes: 4096);
        var payload = File.ReadAllBytes(sourceIpaPath);
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        var indexProgDir = Directory.CreateDirectory(ws.Full("index-app")).FullName;
        var scanRootDir = Directory.CreateDirectory(Path.Combine(indexProgDir, "library")).FullName;
        var settingsPath = Path.Combine(indexProgDir, "data", "indexer.json");
        var dlSettings = new DownloadSettings
        {
            Dir = scanRootDir,
            Connections = 2,
            Splits = 2,
            Concurrency = 1,
            RetryTimes = 1,
            RetryWait = 1,
            Timeout = 15,
            ProxyMode = DownloadSettings.ProxyNone,
            SessionDirMode = DownloadSettings.SessionProgram,
            KeepPartFiles = false,
            SpaceMarginMB = 1,
            AutoImport = true,
            AutoRefreshFallback = true,
        };
        IndexerSettings.SaveDownloadSettings(dlSettings, settingsPath, indexProgDir);

        using var db = AppDatabase.Open(ws.Full("main-data/library.db"));
        ScanRootStore.Add(db, scanRootDir);
        var parsedRows = new List<PackageRow>();
        var scanner = new IpaScanner(db, onRowsParsed: batch => parsedRows.AddRange(batch));

        var pipeName = UniquePipeName();
        var handler = new StubPipeHandler(
            () => new PipeHelloInfo("1.0.1", 1, scanRootDir, ScanRootStore.List(db).Where(r => r.Enabled).Select(r => r.Path).ToList()),
            _ => Task.FromResult(new PipeRefreshInfo(true, 0, "ok")),
            (path, token) => scanner.ImportFileAsync(path, token));

        await using var server = new ImportPipeServer(handler, pipeName);
        server.Start();

        await using var http = new LocalHttpServer(ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength64 = payload.Length;
            ctx.Response.OutputStream.Write(payload, 0, payload.Length);
            ctx.Response.Close();
        });

        var tcs = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeClient = new MainAppPipeClient(pipeName);

        await using var engine = new DownloadEngine(
            _ => [new SourceRow("local", http.Url("/e2e.ipa"), "", payload.Length, sha)],
            new InlineProgress(snap =>
            {
                if (snap.Status is DownloadJobStatus.Succeeded or DownloadJobStatus.Failed or DownloadJobStatus.Cancelled)
                    tcs.TrySetResult(snap);
            }),
            programDir: indexProgDir,
            settingsPath: settingsPath,
            preVerifiedAria2: verified)
        {
            OnDownloadCompletedAsync = (info, token) =>
                IdeviceIndex.MainWindow.ResolvePostDownloadImportNoteAsync(
                    pipeClient, info, dlSettings, [], indexProgDir, token),
        };

        var indexRow = new IndexRow
        {
            Id = "e2e-1",
            BundleId = "com.e2e.sample",
            Title = "E2E Sample",
            Version = "3.2.1",
            MinOs = "6.0",
            Size = payload.Length,
            SizeExact = true,
            DeviceMask = 2,
            SourceCount = 1,
        };
        engine.EnqueueJob(new DownloadJobRow(indexRow, DownloadQueueColumns.OrderedVisible(DownloadQueueColumns.Default)));

        var final = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        Assert.Equal(DownloadJobStatus.Succeeded, final.Status);
        Assert.Equal(IndexStrings.Format(nameof(IndexStrings.Ipc_Note_AutoImportedFormat), "E2E Sample"), final.Note);

        var rows = PackageStore.LoadAll(db);
        var imported = Assert.Single(rows);
        Assert.Equal(ParseStates.Ok, imported.ParseState);
        Assert.Equal("com.e2e.sample", imported.BundleId);
        Assert.Equal("3.2.1", imported.Version);
        Assert.Equal("E2E Sample", imported.DisplayName);
        Assert.Single(parsedRows);
        Assert.Equal(2, db.UserVersion);

        await engine.DisposeAsync();
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

    private static async Task<JsonObject?> SendRawFrameAsync(string pipeName, byte[] utf8Json, CancellationToken ct)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(2000, ct);

        var reqFrame = new byte[4 + utf8Json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(reqFrame.AsSpan(0, 4), utf8Json.Length);
        Buffer.BlockCopy(utf8Json, 0, reqFrame, 4, utf8Json.Length);
        await client.WriteAsync(reqFrame, ct);
        await client.FlushAsync(ct);

        var hdr = new byte[4];
        if (!await ReadExactAsync(client, hdr, ct)) return null;
        var len = BinaryPrimitives.ReadInt32LittleEndian(hdr);
        if (len <= 0 || len > ImportPipeServer.MaxFrameBytes) return null;

        var body = new byte[len];
        if (!await ReadExactAsync(client, body, ct)) return null;
        return JsonNode.Parse(body) as JsonObject;
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    private sealed class StubPipeHandler(
        Func<PipeHelloInfo> hello,
        Func<CancellationToken, Task<PipeRefreshInfo>> refresh,
        Func<string, CancellationToken, Task<ImportFileResult>> import) : IImportPipeHandler
    {
        public PipeHelloInfo GetHelloInfo() => hello();
        public Task<PipeRefreshInfo> RefreshAsync(CancellationToken ct) => refresh(ct);
        public Task<ImportFileResult> ImportFileAsync(string path, CancellationToken ct) => import(path, ct);
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

        public LocalHttpServer(Action<HttpListenerContext> handler)
        {
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                _port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
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
                    _ = Task.Run(() =>
                    {
                        try { handler(ctx); }
                        catch { try { ctx.Response.Abort(); } catch { } }
                    });
                }
            });
        }

        public string Url(string path) => $"http://127.0.0.1:{_port}{path}";

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
