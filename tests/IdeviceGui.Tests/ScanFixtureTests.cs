using System.IO;
using IdeviceGui.Services.Data;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 扫描 fixture：新增 / 重命名 / 替换 / 删除 在**文件系统层面**可重复复现，
/// 以及 B7 起升级的**行级断言**（扫描器对账后的 `Id` / `ParseVersion` / `ExistsFlag`）。
/// 边角场景见 <see cref="ScanReconcileTests"/>、恢复映射见 <see cref="RecoveryMappingTests"/>。
/// </summary>
public sealed class ScanFixtureTests
{
    [Fact]
    public void 新增_目录里多一个ipa()
    {
        using var ws = new TestWorkspace();
        Assert.Empty(ws.IpaFiles());

        ws.AddFile("a.ipa", 1024);

        Assert.Single(ws.IpaFiles());
        Assert.True(ws.Exists("a.ipa"));
    }

    [Fact]
    public void 新增_扫描后出一行_Pending转Ok()
    {
        using var harness = new ScanHarness();
        harness.Ws.AddIpa("a.ipa", "com.example.alpha", version: "1.2", build: "12");

        var result = harness.Scan();

        var row = harness.Row("a.ipa");
        Assert.Equal(1, result.Added);
        Assert.Equal(ParseStates.Ok, row.ParseState);
        Assert.Equal(1, row.ParseVersion);
        Assert.True(row.ExistsFlag);
        Assert.Equal("com.example.alpha", row.BundleId);
        Assert.Equal("1.2", row.Version);
        Assert.Equal("12", row.Build);
        Assert.Equal("6.0", row.MinOs);
        Assert.Equal(IdentityKind.FileId, row.IdentityKind);
        Assert.NotNull(row.VolumeSerial);
    }

    [Fact]
    public void 重命名_同卷内改名后内容不变且文件身份不变()
    {
        using var ws = new TestWorkspace();
        var original = ws.AddFile("old.ipa", 2048);
        var beforeLength = new FileInfo(original).Length;
        var before = ws.IdentityOf("old.ipa");

        ws.Rename("old.ipa", "new.ipa");

        Assert.False(ws.Exists("old.ipa"));
        Assert.True(ws.Exists("new.ipa"));
        Assert.Equal(beforeLength, new FileInfo(ws.Full("new.ipa")).Length);
        Assert.Single(ws.IpaFiles());
        Assert.Equal(before, ws.IdentityOf("new.ipa"));   // 同卷改名：文件身份不变
    }

    [Fact]
    public void 重命名_扫描后Id与ParseVersion都不变_路径更新()
    {
        using var harness = new ScanHarness();
        harness.Ws.AddIpa("old.ipa", "com.example.alpha");
        harness.Scan();
        var before = harness.Row("old.ipa");

        harness.Ws.Rename("old.ipa", "new.ipa");
        harness.Scan();

        var rows = harness.Rows();
        Assert.Single(rows);
        Assert.Equal(before.Id, rows[0].Id);
        Assert.Equal(1, rows[0].ParseVersion);
        Assert.Equal("com.example.alpha", rows[0].BundleId);
        Assert.Equal(Path.GetFullPath(harness.Ws.Full("new.ipa")), Path.GetFullPath(rows[0].FilePath));
    }

    [Fact]
    public void 替换_同路径原地覆盖后大小与mtime都变了()
    {
        using var ws = new TestWorkspace();
        var path = ws.AddFile("same.ipa", 1024);
        var oldInfo = new FileInfo(path);
        var oldLength = oldInfo.Length;
        var oldWrite = oldInfo.LastWriteTimeUtc;
        var beforeIdentity = ws.IdentityOf("same.ipa");

        ws.Replace("same.ipa", 4096);

        var newInfo = new FileInfo(path);
        Assert.NotEqual(oldLength, newInfo.Length);
        Assert.True(newInfo.LastWriteTimeUtc > oldWrite, "替换后 mtime 应该前进");
        Assert.Equal(beforeIdentity, ws.IdentityOf("same.ipa"));   // 原地重写：文件身份不变
    }

    [Fact]
    public void 替换_同路径换成另一个包_保留Id且ParseVersion加1()
    {
        using var harness = new ScanHarness();
        harness.Ws.AddIpa("same.ipa", "com.example.alpha", version: "1.0");
        harness.Scan();
        var before = harness.Row("same.ipa");

        // 原地写另一家的包（FileId 不变、大小变、BundleId 变 → 按替换处理，docs/03 §3.2.1 第 2 行）
        harness.Ws.RewriteIpaSameBundle("same.ipa", "com.example.beta", padBytes: 512, version: "9.9");
        harness.Scan();

        var after = harness.Row("same.ipa");
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.ParseVersion + 1, after.ParseVersion);
        Assert.Equal("com.example.beta", after.BundleId);
        Assert.Equal("9.9", after.Version);
        Assert.Equal(ParseStates.Ok, after.ParseState);
    }

    [Fact]
    public void 删除_文件消失但目录还在()
    {
        using var ws = new TestWorkspace();
        ws.AddFile("gone.ipa", 512);

        ws.Delete("gone.ipa");

        Assert.False(ws.Exists("gone.ipa"));
        Assert.Empty(ws.IpaFiles());
        Assert.True(Directory.Exists(ws.Root));
    }

    [Fact]
    public void 删除_行保留且ExistsFlag置零_ParseVersion不变()
    {
        using var harness = new ScanHarness();
        harness.Ws.AddIpa("gone.ipa", "com.example.alpha");
        harness.Scan();
        var before = harness.Row("gone.ipa");

        harness.Ws.Delete("gone.ipa");
        var result = harness.Scan();

        var rows = harness.Rows();
        Assert.Single(rows);                 // 行保留（队列可能还引用它）
        Assert.False(rows[0].ExistsFlag);
        Assert.Equal(before.Id, rows[0].Id);
        Assert.Equal(before.ParseVersion, rows[0].ParseVersion);
        Assert.Equal(1, result.Missing);
    }

    [Fact]
    public void 子目录递归_非ipa文件不计入()
    {
        using var ws = new TestWorkspace();
        ws.AddFile("top.ipa", 100);
        ws.AddFile("sub/nested.ipa", 100);
        ws.AddFile("sub/readme.txt", 10);

        Assert.Equal(2, ws.IpaFiles().Length);
    }

    [Fact]
    public void 每个工作区互相隔离()
    {
        using var a = new TestWorkspace();
        using var b = new TestWorkspace();
        a.AddFile("only-in-a.ipa", 10);

        Assert.True(a.Exists("only-in-a.ipa"));
        Assert.False(b.Exists("only-in-a.ipa"));
        Assert.NotEqual(a.Root, b.Root);
    }
}
