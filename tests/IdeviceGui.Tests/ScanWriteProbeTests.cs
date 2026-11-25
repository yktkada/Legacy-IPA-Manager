using System.IO;
using IdeviceGui.Services.Data;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 审核报告 P1-1 / P2-7：扫描前写探测。
/// 可写时不留垃圾行；不可写时**跳过扫描且 Error/Summary 说明是跳过**（不能被当成"扫描完成：文件 0"）。
/// </summary>
public sealed class ScanWriteProbeTests
{
    [Fact]
    public void 写探测_可写时不残留且扫描照常()
    {
        using var harness = new ScanHarness();
        harness.Ws.AddIpa("probe-a.ipa", "com.example.probea");

        var result = harness.Scan();

        Assert.Null(result.Error);
        Assert.Equal(1, result.TotalFiles);
        Assert.Equal(1, result.Parsed);
        Assert.StartsWith("扫描完成", result.Summary);
        // 探测写的那一行必须已经回滚掉（否则每轮扫描都会给 Settings 塞一行）
        Assert.Null(SettingsStore.GetString(harness.Db, "Scan.WriteProbe"));
    }

    [Fact]
    public async Task 写探测_库不可用_跳过扫描且Error与Summary都说明跳过()
    {
        using var harness = new ScanHarness();
        harness.Ws.AddIpa("probe-b.ipa", "com.example.probeb");

        harness.Db.Dispose();   // 等价于"库根本写不进去"（不再是可写状态）
        var root = harness.Root;
        var result = await harness.Scanner.ScanAsync(root.Id, root.Path, null, TestContext.Current.CancellationToken);

        Assert.NotNull(result.Error);
        Assert.Contains("未能写入", result.Error);
        Assert.Equal(0, result.TotalFiles);      // 没有白扫
        Assert.Equal(0, result.Parsed);
        Assert.StartsWith("跳过扫描", result.Summary);
    }

    [Fact]
    public async Task 扫描根不可访问_Error有值且Summary说明跳过()
    {
        using var harness = new ScanHarness();
        var root = harness.Root;
        var missing = Path.Combine(harness.Ws.Root, "不存在的子目录");

        var result = await harness.Scanner.ScanAsync(root.Id, missing, null, TestContext.Current.CancellationToken);

        Assert.True(result.RootMissing);
        Assert.NotNull(result.Error);
        Assert.Contains("不可访问", result.Error);
        Assert.StartsWith("跳过扫描", result.Summary);
    }
}
