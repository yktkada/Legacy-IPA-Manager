using System.IO;
using System.Text;
using IdeviceGui.Services;
using IdeviceGui.Services.Data;
using IdeviceGui.Services.Scanning;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>文件日志（docs/08 §3.1）：只写程序目录、5 MB × 3 轮转、打开失败不抛。</summary>
public sealed class FileLogTests
{
    [Fact]
    public void 写文件_每行带时间前缀()
    {
        using var ws = new TestWorkspace("log");
        using var log = new FileLog(ws.Full("logs"), enabled: true);

        log.Write("扫描开始");
        log.Write("扫描完成");
        log.Dispose();

        var lines = File.ReadAllLines(ws.Full("logs/app.log"), Encoding.UTF8);
        Assert.Equal(2, lines.Length);
        Assert.Contains("扫描开始", lines[0]);
        Assert.Matches(@"^\d{2}:\d{2}:\d{2}\.\d{3} ", lines[0]);
        Assert.Null(log.OpenError);
    }

    [Fact]
    public void 关闭时_不建文件不写()
    {
        using var ws = new TestWorkspace("log");
        using var log = new FileLog(ws.Full("logs"), enabled: false);

        log.Write("不该出现");
        log.Dispose();

        Assert.False(File.Exists(ws.Full("logs/app.log")));
    }

    [Fact]
    public void 目录建不出来_只记录OpenError_不抛()
    {
        using var ws = new TestWorkspace("log");
        var blocked = ws.AddFile("blocked", 10);   // 让 "目录" 其实是一个文件

        using var log = new FileLog(blocked, enabled: true);

        Assert.NotNull(log.OpenError);
        log.Write("写不进去也不该抛");                // 不抛即通过本用例
    }

    [Fact]
    public void 超过5MB_轮转为app1log()
    {
        using var ws = new TestWorkspace("log");
        using var log = new FileLog(ws.Full("logs"), enabled: true);

        var payload = new string('x', 1000);
        for (var index = 0; index < 6000; index++) log.Write($"{index} {payload}");
        log.Dispose();

        Assert.True(File.Exists(ws.Full("logs/app.1.log")));
        Assert.True(new FileInfo(ws.Full("logs/app.log")).Length <= 5 * 1024 * 1024);
    }
}

/// <summary>扫描用到的基础能力：文件身份、UTC 文本格式。</summary>
public sealed class ScanningCoreTests
{
    [Fact]
    public void 文件身份_改名不变_复制不同()
    {
        using var ws = new TestWorkspace("id");
        ws.AddFile("a.bin", 100);

        var before = ws.IdentityOf("a.bin");
        Assert.NotNull(before);

        ws.Rename("a.bin", "b.bin");
        Assert.Equal(before, ws.IdentityOf("b.bin"));

        ws.Copy("b.bin", "c.bin");
        Assert.NotEqual(before, ws.IdentityOf("c.bin"));
    }


    [Fact]
    public void UtcText_格式固定可解析_空值不抛()
    {
        var text = UtcText.Now();
        Assert.Equal(28, text.Length);
        Assert.EndsWith("Z", text);
        Assert.NotNull(UtcText.TryParse(text));
        Assert.Null(UtcText.TryParse(null));
        Assert.Null(UtcText.TryParse("2026-10-29 12:00:00"));
    }
}
