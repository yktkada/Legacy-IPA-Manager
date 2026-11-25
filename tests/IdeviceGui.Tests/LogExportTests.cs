using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using IdeviceGui.Services;
using IdeviceGui.ViewModels;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 日志导出（2026-11-01 用户要求）：全量缓冲 → txt，默认程序目录，UTF-8 带 BOM。
/// </summary>
public sealed class LogExportTests
{
    [Fact]
    public void Build_头部三行与分隔线()
    {
        var lines = new[] { new LogLine("正文", LogKind.End, time: new DateTime(2026, 11, 1, 13, 10, 26)) };
        var text = LogExport.Build(lines, new DateTime(2026, 11, 1, 14, 0, 0));

        var rows = text.TrimEnd().Split('\n').Select(r => r.TrimEnd('\r')).ToArray();
        Assert.Equal(LogExport.FileHeader, rows[0]);
        Assert.Equal("导出时间：2026-11-01 14:00:00", rows[1]);
        Assert.Equal("行数：1 / 2000", rows[2]);
        Assert.Equal(new string('-', 60), rows[3]);
    }

    [Fact]
    public void Build_每行格式为_时间_类别_设备_全文()
    {
        var lines = new[]
        {
            new LogLine("开始安装 MemoryInfo 1.1.ipa", LogKind.Start,
                time: new DateTime(2026, 11, 1, 13, 10, 26, 253),
                udid: "u", deviceName: "iPhone 5", productType: "iPhone5,2"),
            new LogLine("扫描完成：文件 15", LogKind.End, time: new DateTime(2026, 11, 1, 13, 10, 27)),
        };
        var text = LogExport.Build(lines, DateTime.Now);

        Assert.Contains("13:10:26.253 [Start] [iPhone 5(iPhone5,2)] 开始安装 MemoryInfo 1.1.ipa", text);
        Assert.Contains("13:10:27.000 [End] 扫描完成：文件 15", text);   // 全局行无设备段
    }

    [Fact]
    public void Build_长行全文不截断()
    {
        var longText = new string('长', 500);
        var text = LogExport.Build(new[] { new LogLine(longText, LogKind.Error) }, DateTime.Now);
        Assert.Contains(longText, text);
    }

    [Fact]
    public void Build_空列表_行数为0且不抛()
    {
        var text = LogExport.Build(Array.Empty<LogLine>(), DateTime.Now);
        Assert.Contains("行数：0", text);
    }

    [Fact]
    public void DefaultFileName_格式为_日志_时间戳_txt()
    {
        Assert.Equal("日志-20261101-134500.txt", LogExport.DefaultFileName(new DateTime(2026, 11, 1, 13, 45, 0)));
    }

    [Fact]
    public async Task ExportLogAsync_写UTF8带BOM_且包含全量日志()
    {
        var vm = new MainViewModel(new FakeCliRunner(), new FakeAfcHelper());
        vm.AppendLogForTest("第一行", LogKind.End);
        vm.AppendLogForTest("第二行", LogKind.Error);

        var dir = Path.Combine(AppPaths.ProgramDirectory, "_test-tmp", "export-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "log.txt");
        try
        {
            await vm.ExportLogAsync(path);

            Assert.True(File.Exists(path));
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());   // UTF-8 BOM

            var text = File.ReadAllText(path, Encoding.UTF8);
            Assert.Contains(LogExport.FileHeader, text);
            Assert.Contains("行数：2", text);
            Assert.Contains("第一行", text);
            Assert.Contains("[Error]", text);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 清理失败不影响断言 */ }
        }
    }
}
