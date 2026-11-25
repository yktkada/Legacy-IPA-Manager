using System.Diagnostics;
using IdeviceGui.Services;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 子进程看门狗（审查报告空洞 #1，硬约束 #3 的核心）：
/// ① 静默子进程 → 按"无输出"判超时并杀进程树；② 持续输出（远超 idle）→ 不误杀。
/// 口径来自 AGENTS §5.11 的真机实测，这里用本机 cmd/powershell 复现成 L1。
/// </summary>
public sealed class ProcessRunnerTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task 静默子进程_按无输出判超时_且杀干净()
    {
        var options = new ProcessRunOptions
        {
            FileName = "cmd.exe",
            Arguments = new[] { "/c", "ping -n 20 127.0.0.1 >nul" },   // 约 19 s 静默
            IdleTimeout = TimeSpan.FromMilliseconds(400),
            TotalTimeout = TimeSpan.FromSeconds(10),
            ExcludeFromKillAll = true,
        };

        var sw = Stopwatch.StartNew();
        var result = await ProcessRunner.RunAsync(options, CancellationToken.None);
        sw.Stop();

        Assert.True(result.TimedOut, $"静默子进程应判超时，实际 {result.StopReason}");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"判超时太慢：{sw.ElapsedMilliseconds} ms");
        Assert.Equal(0, ProcessRunner.ActiveProcessCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task 持续输出_远超idle也不误杀()
    {
        var options = new ProcessRunOptions
        {
            FileName = "powershell.exe",
            Arguments = new[]
            {
                "-NoProfile",
                "-Command",
                "1..8 | ForEach-Object { Write-Output ('tick ' + $_); Start-Sleep -Milliseconds 200 }",
            },
            IdleTimeout = TimeSpan.FromMilliseconds(800),   // 输出间隔 200 ms < 800 ms（留出高并发冷启动余量）
            TotalTimeout = TimeSpan.FromSeconds(20),
            ExcludeFromKillAll = true,
        };

        var result = await ProcessRunner.RunAsync(options, CancellationToken.None);

        Assert.False(result.TimedOut, $"持续输出不应被判超时，实际 {result.StopReason}（{result.TimeoutDetail}）");
        Assert.Equal(ProcessStopReason.Exited, result.StopReason);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(8, result.StdOutLines.Count);
        Assert.Equal(0, ProcessRunner.ActiveProcessCount);
    }

    [Fact]
    public async Task 快退出多行输出_等待异步管道EOF不丢尾部行()
    {
        var options = new ProcessRunOptions
        {
            FileName = "cmd.exe",
            Arguments = new[] { "/c", "for /L %i in (1,1,40) do @(echo line-%i& >&2 echo err-%i)" },
            IdleTimeout = TimeSpan.FromSeconds(5),
            TotalTimeout = TimeSpan.FromSeconds(10),
            ExcludeFromKillAll = true,
        };

        var result = await ProcessRunner.RunAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(ProcessStopReason.Exited, result.StopReason);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(40, result.StdOutLines.Count);
        Assert.Equal(40, result.StdErrLines.Count);
        Assert.Equal("line-40", result.StdOutLines[^1]);
        Assert.Equal("err-40", result.StdErrLines[^1]);
    }
}
