using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using IdeviceGui.Services.Data;
using IdeviceGui.Services.Groups;
using IdeviceGui.Services.Imaging;
using IdeviceGui.Services.Ipc;
using IdeviceGui.Services.Parsing;
using IdeviceGui.Services.Scanning;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>后端审查修复回归用例（P1-2 / P2-2 / P2-6 / P1-7 / P2-7 / P2-10 / P3-1）。</summary>
public sealed class CoreAuditFixTests
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    private static void TryCreateHardLinkOrSkip(string newLinkPath, string existingFilePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(newLinkPath)!);
        if (!CreateHardLinkW(newLinkPath, existingFilePath, IntPtr.Zero))
        {
            Assert.Skip($"当前卷或策略不支持创建硬链接（err={Marshal.GetLastWin32Error()}）");
        }
    }

    // ── P1-2：跨根移动先扫目标根 + 大小变化重解析 ─────────────────────────────

    [Fact]
    public async Task 跨根移动_先扫目标根且内容变化_迁移旧行并重新解析不撞索引()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dbWs = new TestWorkspace("p12-move-db");
        using var rootA = new TestWorkspace("p12-rootA");
        using var rootB = new TestWorkspace("p12-rootB");
        using var db = AppDatabase.Open(dbWs.Full("library.db"));
        var (a, _) = ScanRootStore.Add(db, rootA.Root);
        var (b, _) = ScanRootStore.Add(db, rootB.Root);
        Assert.NotNull(a);
        Assert.NotNull(b);

        var srcPath = rootA.AddIpa("app.ipa", "com.example.old", version: "1.0", padBytes: 64);
        var scanner = new IpaScanner(db);
        await scanner.ScanAsync(a!.Id, a.Path, null, ct);
        var before = PackageStore.LoadByRoot(db, a.Id).Single();
        Assert.Equal("com.example.old", before.BundleId);
        Assert.Equal("1.0", before.Version);

        // 把文件移到 rootB（先不扫 rootA，rootA 在库里仍是 ExistsFlag=1），且原地覆写内容（FileId 不变，大小/mtime 变）
        var dstPath = rootB.Full("moved.ipa");
        File.Move(srcPath, dstPath);
        rootB.AddIpa("moved.ipa", "com.example.new", version: "2.0", padBytes: 256);
        File.SetLastWriteTimeUtc(dstPath, DateTime.UtcNow.AddSeconds(3));

        // 关键：先扫目标根 B（此时根 A 尚未扫描，旧行仍为 ExistsFlag=1）
        var scanB = await scanner.ScanAsync(b!.Id, b.Path, null, ct);
        Assert.Equal(1, scanB.Moved);

        var inB = Assert.Single(PackageStore.LoadByRoot(db, b.Id));
        Assert.Equal(before.Id, inB.Id);
        Assert.True(inB.ExistsFlag);
        Assert.Equal("com.example.new", inB.BundleId);
        Assert.Equal("2.0", inB.Version);
        Assert.Empty(PackageStore.LoadByRoot(db, a.Id));
    }

    [Fact]
    public async Task 跨根ImportFileAsync_先导目标根_迁移旧行不撞唯一索引()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dbWs = new TestWorkspace("p12-imp-db");
        using var rootA = new TestWorkspace("p12-imp-A");
        using var rootB = new TestWorkspace("p12-imp-B");
        using var db = AppDatabase.Open(dbWs.Full("library.db"));
        var (a, _) = ScanRootStore.Add(db, rootA.Root);
        var (b, _) = ScanRootStore.Add(db, rootB.Root);
        Assert.NotNull(a);
        Assert.NotNull(b);

        var srcPath = rootA.AddIpa("pkg.ipa", "com.example.cross", version: "1.0");
        var scanner = new IpaScanner(db);
        await scanner.ScanAsync(a!.Id, a.Path, null, ct);
        var before = PackageStore.LoadByRoot(db, a.Id).Single();

        var dstPath = rootB.Full("pkg.ipa");
        File.Move(srcPath, dstPath);

        var res = await scanner.ImportFileAsync(dstPath, ct);
        Assert.True(res.Ok, res.Error);
        Assert.Equal(before.Id, res.PackageId);

        var all = PackageStore.LoadAll(db);
        Assert.Single(all);
        Assert.Equal(b!.Id, all[0].ScanRootId);
        Assert.True(all[0].ExistsFlag);
    }

    [Fact]
    public async Task 同根与跨根硬链接_扫描与单文件导入去重不撞索引()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dbWs = new TestWorkspace("p12-hl-db");
        using var rootA = new TestWorkspace("p12-hl-A");
        using var rootB = new TestWorkspace("p12-hl-B");
        using var db = AppDatabase.Open(dbWs.Full("library.db"));
        var (a, _) = ScanRootStore.Add(db, rootA.Root);
        var (b, _) = ScanRootStore.Add(db, rootB.Root);
        Assert.NotNull(a);
        Assert.NotNull(b);

        var primary = rootA.AddIpa("primary.ipa", "com.example.hl", version: "1.0");
        var sameRootLink = rootA.Full("sub/link.ipa");
        var crossRootLink = rootB.Full("cross-link.ipa");
        TryCreateHardLinkOrSkip(sameRootLink, primary);
        TryCreateHardLinkOrSkip(crossRootLink, primary);

        var logs = new List<string>();
        var scanner = new IpaScanner(db, log: logs.Add);

        var scanA = await scanner.ScanAsync(a!.Id, a.Path, null, ct);
        Assert.Equal(1, scanA.Added);
        Assert.Single(PackageStore.LoadByRoot(db, a.Id));
        Assert.Contains(logs, l => l.Contains("同根硬链接"));

        var scanB = await scanner.ScanAsync(b!.Id, b.Path, null, ct);
        Assert.Equal(0, scanB.Added);
        Assert.Empty(PackageStore.LoadByRoot(db, b.Id));
        Assert.Contains(logs, l => l.Contains("跨根硬链接"));

        // ImportFileAsync 命中跨根硬链接也不插重复行
        var imp = await scanner.ImportFileAsync(crossRootLink, ct);
        Assert.True(imp.Ok, imp.Error);
        Assert.Single(PackageStore.LoadAll(db));
        Assert.Contains(logs, l => l.Contains("硬链接重复文件"));
    }

    // ── P2-10 后端：ImportFileCore 的 parse_failed 行也发上屏回调 ────────────

    [Fact]
    public async Task ImportFileAsync_坏包parse_failed也会触发onRowsParsed回调()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dbWs = new TestWorkspace("p210-db");
        using var root = new TestWorkspace("p210-root");
        using var db = AppDatabase.Open(dbWs.Full("library.db"));
        ScanRootStore.Add(db, root.Root);

        var callbacks = new List<IReadOnlyList<PackageRow>>();
        var scanner = new IpaScanner(db, onRowsParsed: batch => callbacks.Add(batch));

        var corrupt = root.AddFile("bad.ipa", 128, 0x7A);
        var res = await scanner.ImportFileAsync(corrupt, ct);

        Assert.False(res.Ok);
        Assert.Equal("parse_failed", res.Error);
        var batch = Assert.Single(callbacks);
        var row = Assert.Single(batch);
        Assert.Equal(ParseStates.Failed, row.ParseState);
        Assert.Equal(corrupt, row.FilePath);
    }

    // ── P2-2：驱动器根目录 E:\ 路径规范化 ───────────────────────────────────

    [Fact]
    public void 驱动器根目录规范化_保留尾随反斜杠且前缀判断正确()
    {
        Assert.Equal(@"E:\", ScanRootStore.Normalize(@"E:\"));
        Assert.Equal(@"E:\Apps", ScanRootStore.Normalize(@"E:\Apps\"));
        Assert.True(ScanRootStore.IsAncestorOrSelf(@"E:\", @"E:\Apps\demo.ipa"));
        Assert.True(ScanRootStore.IsAncestorOrSelf(@"E:\", @"E:\"));
        Assert.False(ScanRootStore.IsAncestorOrSelf(@"E:\root", @"E:\root2\demo.ipa"));

        Assert.Equal(@"e:\", ScanRootNumbers.Normalize(@"E:\"));
        Assert.Equal(@"e:\apps", ScanRootNumbers.Normalize(@"E:\Apps\"));

        Assert.Equal(@"E:\", GroupStore.NormalizePath(@"E:\"));
        Assert.Equal(@"E:\Apps", GroupStore.NormalizePath(@"E:\Apps\"));
    }

    // ── P2-6 / P1-7：ImportPipeServer.HasInFlightRequests 与无竞态登记 ────────

    [Fact]
    public async Task ImportPipeServer_HasInFlightRequests反映在途请求且完成后不泄漏()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = $"IdeviceGui.AuditTest.{Guid.NewGuid():N}";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handler = new DelayedImportHandler(
            () => new PipeHelloInfo("1.0.1", 1, @"D:\lib", [@"D:\lib"]),
            async token =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token).ConfigureAwait(false);
                return new PipeRefreshInfo(true, 0, "ok");
            });

        await using var server = new ImportPipeServer(handler, pipeName);
        server.Start();
        Assert.False(server.HasInFlightRequests);

        var requestTask = SendRefreshFrameAsync(pipeName, ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.True(server.HasInFlightRequests);

        release.SetResult();
        await requestTask;

        // 给 HandleClientTrackedAsync 的 finally 一点时间移出字典
        await TestWait.UntilAsync(() => !server.HasInFlightRequests, () => "请求完成后 HasInFlightRequests 应回到 false", 3000);

        // 连续快速短请求也不泄漏 _inFlight
        for (var i = 0; i < 10; i++)
        {
            await SendHelloFrameAsync(pipeName, ct);
        }
        await TestWait.UntilAsync(() => !server.HasInFlightRequests, () => "连续短请求后不应残留 HasInFlightRequests", 3000);
    }

    // ── P2-7：ScanRootStore.Add 并发事务防护 ─────────────────────────────────

    [Fact]
    public async Task ScanRootStoreAdd_并发添加同目录与重叠子目录不报错且只入库一次()
    {
        using var ws = new TestWorkspace("p27-roots");
        using var db = AppDatabase.Open(ws.Full("library.db"));
        var rootDir = Directory.CreateDirectory(ws.Full("root")).FullName;
        var childDir = Directory.CreateDirectory(ws.Full("root/child")).FullName;

        var tasks = Enumerable.Range(0, 6).Select(i => Task.Run(() =>
            ScanRootStore.Add(db, i % 2 == 0 ? rootDir : childDir))).ToArray();
        var results = await Task.WhenAll(tasks);

        var rows = ScanRootStore.List(db);
        Assert.Single(rows);
        Assert.Contains(results, r => r.Row is not null && r.Error is null);
    }

    // ── P3-1：轻量 IconKey 清理与单次打开 zip 提图标 ───────────────────────────

    [Fact]
    public async Task 轻量IconKey查询与清理_正确返回在位键并置空断链()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ws = new TestWorkspace("p31-icon");
        using var db = AppDatabase.Open(ws.Full("library.db"));
        var (root, _) = ScanRootStore.Add(db, ws.Root);
        Assert.NotNull(root);

        // 造一个带图标的 IPA 并用带图标缓存的 IpaScanner 扫描（单次打开 zip）
        var ipaPath = ws.Full("WithIcon.ipa");
        using (var zip = ZipFile.Open(ipaPath, ZipArchiveMode.Create))
        {
            var plist = """
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0"><dict>
                <key>CFBundleIdentifier</key><string>com.example.icon</string>
                <key>CFBundleVersion</key><string>1</string>
                <key>CFBundleIconFiles</key><array><string>Icon.png</string></array>
                </dict></plist>
                """;
            var pe = zip.CreateEntry("Payload/Icon.app/Info.plist", CompressionLevel.NoCompression);
            using (var s = pe.Open()) s.Write(Encoding.UTF8.GetBytes(plist));

            var rgba = new byte[72 * 72 * 4];
            for (var i = 0; i < rgba.Length; i += 4) { rgba[i] = 10; rgba[i + 1] = 120; rgba[i + 2] = 240; rgba[i + 3] = 255; }
            var png = PngCodec.Encode(new PngCodec.DecodedPng(72, 72, rgba));
            var ie = zip.CreateEntry("Payload/Icon.app/Icon.png", CompressionLevel.NoCompression);
            using (var s = ie.Open()) s.Write(png);
        }

        var dataDir = ws.Full("data");
        var scanner = new IpaScanner(db, new ScanOptions(), null, dataDir, new IconCacheOptions(), new IconRequest(32, 1.0));
        await scanner.ScanAsync(root!.Id, root.Path, null, ct);

        var row = PackageStore.LoadByRoot(db, root.Id).Single();
        Assert.NotNull(row.IconKey);
        var liveKeys = PackageStore.LoadLiveIconKeys(db);
        Assert.Contains(row.IconKey!, liveKeys);

        var cleared = PackageStore.ClearMissingIconKeys(db, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(1, cleared);
        Assert.Null(PackageStore.LoadById(db, row.Id)!.IconKey);
    }

    private sealed class DelayedImportHandler : IImportPipeHandler
    {
        private readonly Func<PipeHelloInfo> _hello;
        private readonly Func<CancellationToken, Task<PipeRefreshInfo>> _refresh;

        public DelayedImportHandler(Func<PipeHelloInfo> hello, Func<CancellationToken, Task<PipeRefreshInfo>> refresh)
        {
            _hello = hello;
            _refresh = refresh;
        }

        public PipeHelloInfo GetHelloInfo() => _hello();
        public Task<PipeRefreshInfo> RefreshAsync(CancellationToken ct) => _refresh(ct);
        public Task<ImportFileResult> ImportFileAsync(string path, CancellationToken ct) =>
            Task.FromResult(new ImportFileResult(false, "file_missing"));
    }

    private static Task<JsonNode?> SendRefreshFrameAsync(string pipeName, CancellationToken ct) =>
        SendFrameAsync(pipeName, """{"v":1,"op":"refresh"}""", ct);

    private static Task<JsonNode?> SendHelloFrameAsync(string pipeName, CancellationToken ct) =>
        SendFrameAsync(pipeName, """{"v":1,"op":"hello"}""", ct);

    private static async Task<JsonNode?> SendFrameAsync(string pipeName, string json, CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(3000, ct).ConfigureAwait(false);
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), payload.Length);
        Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
        await pipe.WriteAsync(frame, ct).ConfigureAwait(false);
        await pipe.FlushAsync(ct).ConfigureAwait(false);

        var lenBuf = new byte[4];
        var read = 0;
        while (read < 4)
        {
            var n = await pipe.ReadAsync(lenBuf.AsMemory(read, 4 - read), ct).ConfigureAwait(false);
            if (n == 0) return null;
            read += n;
        }

        var respLen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
        var body = new byte[respLen];
        read = 0;
        while (read < respLen)
        {
            var n = await pipe.ReadAsync(body.AsMemory(read, respLen - read), ct).ConfigureAwait(false);
            if (n == 0) return null;
            read += n;
        }

        return JsonNode.Parse(body);
    }
}
