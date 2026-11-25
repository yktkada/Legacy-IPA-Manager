using System;
using System.IO;
using System.Linq;
using IdeviceGui.Services;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// b3b（2026-11-01）：LogLine 结构化契约 —— 格式单一来源、设备段、截断只在渲染层、
/// Kind 在最前、Error/End 颜色触发器（XAML 静态校验）。
/// </summary>
public sealed class LogLineTests
{
    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    [Fact]
    public void 全局行_格式为方括号类别_时间_正文_且无设备段()
    {
        var line = new LogLine("扫描完成：文件 15", LogKind.End, time: new DateTime(2026, 11, 1, 13, 10, 26));

        Assert.Equal("[End] 13:10:26 扫描完成：文件 15", line.FormatForUi());
    }

    [Fact]
    public void 设备行_格式含设备名与ProductType()
    {
        var line = new LogLine("开始安装 MemoryInfo 1.1.ipa", LogKind.Start,
            time: new DateTime(2026, 11, 1, 13, 10, 26),
            udid: "0123456789abcdef0123456789abcdef01234567",
            deviceName: "iPhone 5", productType: "iPhone5,2");

        Assert.Equal("[Start] 13:10:26 [iPhone 5(iPhone5,2)] 开始安装 MemoryInfo 1.1.ipa", line.FormatForUi());
    }

    [Fact]
    public void 只有ProductType没有名称时_仍显示设备段()
    {
        var line = new LogLine("x", LogKind.Detail, time: new DateTime(2026, 11, 1, 0, 0, 1),
            udid: "u", deviceName: null, productType: "iPhone3,1");

        Assert.Contains("[未知设备(iPhone3,1)]", line.FormatForUi());
    }

    [Fact]
    public void 长设备名_界面截断12字_文件与ToolTip不截断()
    {
        var line = new LogLine("正文", LogKind.End, time: new DateTime(2026, 11, 1, 0, 0, 1),
            udid: "u", deviceName: "一台名字非常非常长的测试设备", productType: "iPad2,1");

        Assert.Contains("…", line.FormatForUi());
        Assert.DoesNotContain("…", line.FormatForFile());
        Assert.DoesNotContain("…", line.ToolTipText);
        Assert.Contains("一台名字非常非常长的测试设备(iPad2,1)", line.ToolTipText);
    }

    [Fact]
    public void 文件格式_类别在前_不带时间_时间由FileLog加()
    {
        var line = new LogLine("正文", LogKind.Error, time: new DateTime(2026, 11, 1, 13, 10, 26, 253),
            udid: "u", deviceName: "iPhone 5", productType: "iPhone5,2");

        Assert.Equal("[Error] [iPhone 5(iPhone5,2)] 正文", line.FormatForFile());
    }

    [Fact]
    public void Text存全文_不被截断()
    {
        var text = new string('长', 500);
        var line = new LogLine(text, LogKind.Detail);
        Assert.Equal(text, line.Text);
    }

    [Fact]
    public void Seq自增_相同文本两行不相等()
    {
        var a = new LogLine("同一行", LogKind.End);
        var b = new LogLine("同一行", LogKind.End);

        Assert.NotEqual(a.Seq, b.Seq);
        Assert.NotEqual(a, b);   // 引用相等（class），ScrollIntoView 不会被相等性骗
    }

    [Fact]
    public void IsError与IsEnd供XAML触发器使用()
    {
        Assert.True(new LogLine("e", LogKind.Error).IsError);
        Assert.False(new LogLine("e", LogKind.End).IsError);
        Assert.True(new LogLine("e", LogKind.End).IsEnd);
        Assert.False(new LogLine("e", LogKind.Start).IsEnd);
    }

    [Fact]
    public void ToString等于界面格式_UIA名称不丢()
    {
        var line = new LogLine("正文", LogKind.End, time: new DateTime(2026, 11, 1, 1, 2, 3));
        Assert.Equal(line.FormatForUi(), line.ToString());
    }

    [Fact]
    public void XAML_日志列表有Error红End绿触发器_且开虚拟化_且有ToolTip()
    {
        var xamlPath = Path.Combine(RepoRoot(), "IdeviceGui", "prototype", "IdeviceGui", "Views", "MainWindow.xaml");
        Assert.True(File.Exists(xamlPath), "找不到源码 XAML：" + xamlPath);
        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains("Binding=\"{Binding IsError}\"", xaml);
        Assert.Contains("Binding=\"{Binding IsEnd}\"", xaml);
        Assert.Contains("#FFC62828", xaml);   // Error 红
        Assert.Contains("#FF2E7D32", xaml);   // End 绿
        Assert.Contains("VirtualizingStackPanel.IsVirtualizing=\"True\"", xaml);
        Assert.Contains("ToolTipText", xaml);
        Assert.Contains("TextTrimming", xaml);
    }
}
