using System.IO;
using IdeviceGui.Services.Data;
using IdeviceGui.Services.Scanning;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>对账边角：替换 / 仅更新 / 身份翻转 / 重叠根 / 根不可访问 / 坏包 / 批量。</summary>
public sealed class ScanReconcileTests
{
    [Fact]
    public void 同大小不同BundleId_原位覆盖_算替换()
    {
        using var harness = new ScanHarness();
        harness.Ws.AddIpaExactSize("same.ipa", "com.example.alpha", 16384);
        harness.Scan();
        var before = harness.Row("same.ipa");
        Assert.Equal(16384, before.FileSize);

        // 同样 4096 字节、不同 bundleId、原地写 → FileId 不变、大小不变、BundleId 变 → 替换
        harness.Ws.AddIpaExactSize("same.ipa", "com.example.beta", 4096, version: "2.0");
        harness.Scan();

        var after = harness.Row("same.ipa");
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.ParseVersion + 1, after.ParseVersion);
        Assert.Equal("com.example.beta", after.BundleId);
    }

    [Fact]
    public void 同BundleId_大小变_不算替换_ParseVersion不变()
    {
        using var harness = new ScanHarness();
        harness.Ws.AddIpa("same.ipa", "com.example.alpha", version: "1.0", padBytes: 100);
        harness.Scan();
        var before = harness.Row("same.ipa");

        // 同路径、同 bundleId、内容变大（FileId 不变）→ 只更新大小/mtime，不重解析（docs/03 §3.2.1 第 2 行后半）
        harness.Ws.RewriteIpaSameBundle("same.ipa", "com.example.alpha", padBytes: 300, version: "2.0");
        harness.Scan();

        var after = harness.Row("same.ipa");
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.ParseVersion, after.ParseVersion);
        Assert.True(after.FileSize > before.FileSize);
        Assert.Equal("1.0", after.Version);        // 没重解析，解析字段保持旧值
        Assert.Equal(ParseStates.Ok, after.ParseState);
    }

    [Fact]
    public void FileId变_移动覆盖同路径_算替换()
    {
        using var harness = new ScanHarness();
        harness.Ws.AddIpa("a.ipa", "com.example.alpha");
        harness.Scan();
        var before = harness.Row("a.ipa");
        var identityBefore = harness.Ws.IdentityOf("a.ipa");

        harness.Ws.AddIpa("b.ipa", "com.example.beta", version: "3.0");
        harness.Ws.ReplaceByMove("b.ipa", "a.ipa");
        harness.Scan();

        var after = harness.Row("a.ipa");
        Assert.NotEqual(identityBefore, harness.Ws.IdentityOf("a.ipa"));
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.ParseVersion + 1, after.ParseVersion);
        Assert.Equal("com.example.beta", after.BundleId);
        Assert.Single(harness.Rows());
    }

    [Fact]
    public void 身份翻转_旧行标不存在_新插一行()
    {
        using var harness = new ScanHarness();
        harness.Ws.AddIpa("a.ipa", "com.example.alpha");
        harness.Scan();
        var fileRow = harness.Row("a.ipa");

        // 手工把该行改成 PathFallback 身份（模拟"网络盘挂载方式变了"），文件本身仍有 FileId → 身份种类不同
        fileRow.IdentityKind = IdentityKind.PathFallback;
        fileRow.VolumeSerial = null;
        fileRow.FileIdHigh = null;
        fileRow.FileIdLow = null;
        fileRow.NormalizedPath = ScanPaths.Normalize(harness.Ws.Full("a.ipa"));
        PackageStore.Save(harness.Db, new[] { fileRow });

        harness.Scan();

        var rows = harness.Rows();
        Assert.Equal(2, rows.Count);                                  // 删除 + 新增，不原地翻转（docs/09 §3.1）
        Assert.Single(rows, r => !r.ExistsFlag);
        Assert.Single(rows, r => r.ExistsFlag);
        Assert.NotEqual(rows[0].Id, rows[1].Id);
    }

    [Fact]
    public void 重叠根_父目录与子目录都拒绝()
    {
        using var ws = new TestWorkspace("roots");
        using var db = AppDatabase.Open(ws.Full("library.db"));
        var (root, error) = ScanRootStore.Add(db, ws.Root);
        Assert.Null(error);
        Assert.NotNull(root);

        var child = Path.Combine(ws.Root, "child");
        Directory.CreateDirectory(child);
        var (childRoot, childError) = ScanRootStore.Add(db, child);
        Assert.Null(childRoot);
        Assert.Contains("重叠", childError);

        var (sameRoot, sameError) = ScanRootStore.Add(db, ws.Root);   // 幂等：已存在直接返回
        Assert.Null(sameError);
        Assert.Equal(root!.Id, sameRoot!.Id);
    }

    [Fact]
    public async Task 根不可访问_跳过对账_不把已有行标成不存在()
    {
        using var dbWs = new TestWorkspace("db");
        using var filesWs = new TestWorkspace("files");
        using var db = AppDatabase.Open(dbWs.Full("library.db"));
        var (root, error) = ScanRootStore.Add(db, filesWs.Root);
        Assert.NotNull(root);
        Assert.Null(error);

        filesWs.AddIpa("a.ipa", "com.example.alpha");
        var scanner = new IpaScanner(db);
        await scanner.ScanAsync(root!.Id, root.Path, null, TestContext.Current.CancellationToken);
        Assert.Single(PackageStore.LoadByRoot(db, root.Id));

        Directory.Delete(filesWs.Root, recursive: true);
        var result = await scanner.ScanAsync(root.Id, root.Path, null, TestContext.Current.CancellationToken);

        Assert.True(result.RootMissing);
        var row = PackageStore.LoadByRoot(db, root.Id).Single();
        Assert.True(row.ExistsFlag);       // 不可访问 ≠ 文件消失（docs/03 §3.1）
        Assert.Equal(0, result.Missing);
    }

    [Fact]
    public void 坏包_解析失败记Failed_不抛异常()
    {
        using var harness = new ScanHarness();
        harness.Ws.AddFile("bad.ipa", 1024);   // 不是 zip

        var result = harness.Scan();

        var row = harness.Row("bad.ipa");
        Assert.Equal(ParseStates.Failed, row.ParseState);
        Assert.NotNull(row.ParseError);
        Assert.Equal(1, row.ParseVersion);
        Assert.Equal(1, result.ParseFailed);
        Assert.Equal(0, result.ParseOk);
    }

    [Fact]
    public void 非ipa文件忽略_子目录递归()
    {
        using var harness = new ScanHarness();
        harness.Ws.AddIpa("top.ipa", "com.example.top");
        harness.Ws.AddIpa("sub/nested.ipa", "com.example.nested");
        harness.Ws.AddFile("sub/readme.txt", 10);

        var result = harness.Scan();

        Assert.Equal(2, result.TotalFiles);
        Assert.Equal(2, harness.Rows().Count);
    }

    [Fact]
    public void 批量一千个文件_行数正确_记录耗时()
    {
        using var harness = new ScanHarness();
        harness.Ws.AddFiles(1000);

        var result = harness.Scan();

        Assert.Equal(1000, result.TotalFiles);
        Assert.Equal(1000, harness.Rows().Count);
        Assert.Equal(1000, result.Added);
        // 原始数据：耗时只记录不判定（规则 R2）
        Assert.True(result.ListPhaseMs >= 0);
        Assert.True(result.ParsePhaseMs >= 0);
    }

    [Fact]
    public void 符号链接目录_跳过不进去()
    {
        using var harness = new ScanHarness();
        harness.Ws.AddIpa("real/inside.ipa", "com.example.real");

        try
        {
            Directory.CreateSymbolicLink(harness.Ws.Full("loop"), harness.Ws.Full("real"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("当前环境不允许创建符号链接：" + ex.GetType().Name);
            return;
        }

        var result = harness.Scan();

        Assert.Equal(1, result.TotalFiles);   // 链接目录被跳过（AttributesToSkip = ReparsePoint），不会重复枚举
    }

    [Fact]
    public async Task 跨扫描根移动_迁移旧行_不撞全局唯一索引()
    {
        using var dbWs = new TestWorkspace("db");
        using var rootA = new TestWorkspace("rootA");
        using var rootB = new TestWorkspace("rootB");
        using var db = AppDatabase.Open(dbWs.Full("library.db"));
        var (a, aError) = ScanRootStore.Add(db, rootA.Root);
        var (b, bError) = ScanRootStore.Add(db, rootB.Root);
        Assert.Null(aError);
        Assert.Null(bError);

        rootA.AddIpa("move.ipa", "com.example.move");
        var scanner = new IpaScanner(db);
        await scanner.ScanAsync(a!.Id, a.Path, null, TestContext.Current.CancellationToken);
        var before = PackageStore.LoadByRoot(db, a.Id).Single();

        // 同卷移动：FileId 不变，旧根 A 先标 ExistsFlag=0；随后根 B 必须迁移旧行而不是 INSERT。
        File.Move(rootA.Full("move.ipa"), rootB.Full("move.ipa"));
        await scanner.ScanAsync(a.Id, a.Path, null, TestContext.Current.CancellationToken);
        Assert.False(PackageStore.LoadByRoot(db, a.Id).Single().ExistsFlag);

        var result = await scanner.ScanAsync(b!.Id, b.Path, null, TestContext.Current.CancellationToken);

        var moved = Assert.Single(PackageStore.LoadByRoot(db, b.Id));
        Assert.Equal(before.Id, moved.Id);
        Assert.True(moved.ExistsFlag);
        Assert.Empty(PackageStore.LoadByRoot(db, a.Id));
        Assert.Equal(1, result.Moved);
    }
}
