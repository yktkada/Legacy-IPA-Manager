using System.Threading;
using System.IO;
using IdeviceGui.Services.Data;
using IdeviceGui.Services.Imaging;
using IdeviceGui.Services.Scanning;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>M4-a：解析分批落库 + 分批投递 + 批次大小解析（测试计划 §4.1）。</summary>
public sealed class ScanBatchTests
{
    [Fact]
    public void 分批落库_120行_按批次提交且全部Ok()
    {
        using var harness = new ScanHarness();
        var batches = new List<int>();
        harness.ReplaceScanner(rows => { lock (batches) batches.Add(rows.Count); }, batchSize: 30);

        for (var i = 0; i < 120; i++) harness.Ws.AddIpa($"p{i:000}.ipa", $"com.example.p{i}");

        var result = harness.Scan();

        Assert.Equal(120, result.Parsed);
        Assert.Equal(4, result.SaveCount);               // 120 / 30
        Assert.Equal(4, batches.Count);
        Assert.All(batches, n => Assert.True(n <= 30, $"单批超限：{n}"));
        Assert.Equal(120, batches.Sum());

        var rows = harness.Rows();
        Assert.Equal(120, rows.Count);
        Assert.All(rows, r => Assert.Equal(ParseStates.Ok, r.ParseState));
    }

    [Fact]
    public void 不足一批_也提交一次()
    {
        using var harness = new ScanHarness();
        harness.ReplaceScanner(null, batchSize: 30);
        for (var i = 0; i < 7; i++) harness.Ws.AddIpa($"q{i}.ipa", $"com.example.q{i}");

        var result = harness.Scan();

        Assert.Equal(7, result.Parsed);
        Assert.Equal(1, result.SaveCount);
        Assert.All(harness.Rows(), r => Assert.Equal(ParseStates.Ok, r.ParseState));
    }

    [Theory]
    [InlineData("30", 30)]
    [InlineData(" 8 ", 8)]
    [InlineData("\"12\"", 12)]
    [InlineData("0", ScanOptions.DefaultBatchSize)]
    [InlineData("-5", ScanOptions.DefaultBatchSize)]
    [InlineData("abc", ScanOptions.DefaultBatchSize)]
    [InlineData("", ScanOptions.DefaultBatchSize)]
    [InlineData(null, ScanOptions.DefaultBatchSize)]
    public void 批次大小解析_非法值回退默认(string? raw, int expected)
        => Assert.Equal(expected, ScanOptions.ResolveBatchSize(raw));

    [Fact]
    public async Task 批次提交后取消_已提交的行不丢()
    {
        using var harness = new ScanHarness();
        using var cts = new CancellationTokenSource();
        var saves = 0;
        harness.ReplaceScanner(_ =>
        {
            // 在**提交者线程**上取消：此时解析阶段一定还在跑（下面用 200 行保证）
            if (Interlocked.Increment(ref saves) == 1) cts.Cancel();
        }, batchSize: 5);

        // 200 行（不是 40）：合成包解析很快，行数太少时"取消"可能晚于解析结束，
        // 用例会偶发地"没抛 OCE"（2026-10-31 实测 6 次里挂 1 次）。行数放大后取消必然落在解析中途。
        const int total = 200;
        for (var i = 0; i < total; i++) harness.Ws.AddIpa($"c{i:000}.ipa", $"com.example.c{i}");

        var root = harness.Root;
        var cancelled = false;
        try
        {
            await harness.Scanner.ScanAsync(root.Id, root.Path, null, cts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        var rows = harness.Rows();
        var ok = rows.Count(r => r.ParseState == ParseStates.Ok);
        var pending = rows.Count(r => r.ParseState == ParseStates.Pending);

        // 无论取消有没有"晚到"，数据必须自洽：每行要么已解析成功、要么还在待解析
        Assert.Equal(total, ok + pending);
        Assert.True(ok >= 5, $"已提交批次应保留（Ok={ok}）");
        if (cancelled)
            Assert.True(ok < total, $"取消中途不该全部解析完（Ok={ok}/{total}）");
    }

    // ── P1-3：删掉的包，其图标应从缓存里回收（不再被 ExistsFlag=0 的行引用）────
    [Fact]
    public void 删除包后重扫_其图标文件被清理()
    {
        using var harness = new ScanHarness();
        harness.Ws.AddIpa("a.ipa", "com.example.alpha");
        harness.Ws.AddIpa("b.ipa", "com.example.beta");

        var dataDir = Path.Combine(harness.Ws.Root, "data");
        var iconDir = Path.Combine(dataDir, "icons");
        Directory.CreateDirectory(iconDir);

        // 换成带图标缓存目录的扫描器（否则收尾清理块整段跳过）
        harness.ReplaceScannerInstance(new IpaScanner(harness.Db, new ScanOptions(), null, dataDir,
            new IconCacheOptions(), new IconRequest(32, 1.0)));

        // 第一轮扫描：只确认建行
        harness.Scan();

        // 手工布置：两行各指向一个 png（key 形如 <sha1>-<档位>）
        var rows = harness.Rows();
        Assert.Equal(2, rows.Count);
        var keyA = "aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111-32";
        var keyB = "bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222-32";
        rows[0].IconKey = keyA;
        rows[1].IconKey = keyB;
        PackageStore.Save(harness.Db, rows);

        File.WriteAllBytes(Path.Combine(iconDir, keyA + ".png"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(iconDir, keyB + ".png"), new byte[] { 4, 5, 6 });
        // 绕过 10 分钟保护期
        foreach (var f in Directory.GetFiles(iconDir, "*.png"))
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(-30));

        // 删掉 b.ipa（它对应 rows[1]），再扫一轮 → 该行 ExistsFlag=0，其图标应被回收
        File.Delete(harness.Ws.Full("b.ipa"));
        harness.Scan();

        var after = harness.Rows();
        var deleted = after.Single(r => !r.ExistsFlag);
        var kept = after.Single(r => r.ExistsFlag);

        Assert.Equal(keyA, kept.IconKey);                                  // 仍存在的包图标引用不动
        Assert.False(File.Exists(Path.Combine(iconDir, keyB + ".png")), "已删包的图标应被回收");
        Assert.True(File.Exists(Path.Combine(iconDir, keyA + ".png")), "仍存在包的图标不能被删");
        // 回收后该行的 IconKey 被断链清理置空（指向已不存在的文件）
        Assert.True(deleted.IconKey is null || deleted.IconKey == keyB);
    }

    // ── P2-2：批次大小有上限 ───────────────────────────────────────────────
    [Fact]
    public void 批次大小_超上限被夹到1000()
    {
        Assert.Equal(ScanOptions.MaxBatchSize, ScanOptions.ResolveBatchSize("2147483647"));
        Assert.Equal(1000, ScanOptions.ResolveBatchSize("1000000"));
        Assert.Equal(ScanOptions.DefaultBatchSize, ScanOptions.ResolveBatchSize("0"));
    }

    // ── 全量重扫（2026-10-31 用户要的按钮）：连 Ok 行也重新解析 ──────────────
    [Fact]
    public void 普通重扫_不重解析已成功的行()
    {
        using var harness = new ScanHarness();
        harness.Ws.AddIpa("r1.ipa", "com.example.r1");
        harness.Scan();                                   // 第一轮：解析成 Ok

        var before = harness.Row("r1.ipa").ParseVersion;
        var second = harness.Scan();                      // 第二轮：文件没变 → 复用，不解析

        Assert.Equal(0, second.Parsed);
        Assert.Equal(before, harness.Row("r1.ipa").ParseVersion);
    }

    [Fact]
    public void 全量重扫_把已成功的行也重新解析_深字段补齐且行数不变()
    {
        using var harness = new ScanHarness();
        harness.Ws.AddIpa("all1.ipa", "com.example.all1");
        harness.Ws.AddIpa("all2.ipa", "com.example.all2");
        harness.Scan();

        // 模拟"旧版本入库"的行：解析状态是 Ok，但深字段（体积）是空的
        var stale = harness.Rows();
        foreach (var row in stale)
        {
            row.AppSize = 0;
            row.UncompressedSize = 0;
        }
        PackageStore.Save(harness.Db, stale);
        var versionsBefore = harness.Rows().ToDictionary(r => r.FilePath, r => r.ParseVersion);

        var result = harness.Scan(forceReparse: true);

        Assert.Equal(2, result.Parsed);                   // 两行都重解析了
        var rows = harness.Rows();
        Assert.Equal(2, rows.Count);                      // 行数不变（不是新增）
        Assert.All(rows, r =>
        {
            Assert.Equal(ParseStates.Ok, r.ParseState);
            Assert.True(r.AppSize > 0, $"{r.FileName} 的 AppSize 应被补齐");
            Assert.Equal(versionsBefore[r.FilePath] + 1, r.ParseVersion);   // ParseVersion 照常 +1
        });
    }

    [Fact]
    public void 全量重扫之后_下一次普通扫描不重解析Ok行()
    {
        // 回归（进度 §5.2 第 3 条）：ForceReparse 曾经是共享可变属性，靠 finally 复位；
        // 现在改成按次传参——两次连续扫描必须互不影响。
        using var harness = new ScanHarness();
        harness.Ws.AddIpa("a.ipa", "com.example.a");
        harness.Ws.AddIpa("b.ipa", "com.example.b");
        harness.Scan();

        var forced = harness.Scan(forceReparse: true);
        Assert.Equal(2, forced.Parsed);                    // 全量：两行都重解析

        var normal = harness.Scan();                       // 紧接着一次普通扫描
        Assert.Equal(0, normal.Parsed);                    // 全部 Ok 且未变 → 一行都不解析
        Assert.Equal(2, normal.Unchanged);                 // 两行都没重新解析
    }

    [Fact]
    public void 全量重扫_只动在位行_已删除的行不重解析()
    {
        using var harness = new ScanHarness();
        harness.Ws.AddIpa("keep.ipa", "com.example.keep");
        harness.Ws.AddIpa("gone.ipa", "com.example.gone");
        harness.Scan();

        File.Delete(harness.Ws.Full("gone.ipa"));
        var result = harness.Scan(forceReparse: true);

        Assert.Equal(1, result.Parsed);                   // 只有在位的那一个
        var gone = harness.Rows().Single(r => !r.ExistsFlag);
        Assert.Equal(ParseStates.Ok, gone.ParseState);    // 不在位的行保持原样
    }
}
