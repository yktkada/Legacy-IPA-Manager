using System.IO;
using IdeviceGui.Services;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>启动参数解析（M3 临时加的 <c>--root</c>；M4 会换成正式的多扫描根）。</summary>
public sealed class StartupOptionsTests
{
    [Fact]
    public void 不给参数时用默认扫描根()
    {
        Assert.Null(StartupOptions.Parse(null).ScanRoot);
        Assert.Null(StartupOptions.Parse(Array.Empty<string>()).ScanRoot);
    }

    [Fact]
    public void 支持空格形式并且统一成绝对路径()
    {
        var options = StartupOptions.Parse(new[] { "--root", @"C:\Samples\test scripts" });
        Assert.Equal(@"C:\Samples\test scripts", options.ScanRoot);
    }

    [Fact]
    public void 支持等号形式与引号()
    {
        var options = StartupOptions.Parse(new[] { "--root=D:\\Samples\\ipas" });
        Assert.Equal(@"D:\Samples\ipas", options.ScanRoot);

        var quoted = StartupOptions.Parse(new[] { "--root", "\"D:\\Samples\\ipas\"" });
        Assert.Equal(@"D:\Samples\ipas", quoted.ScanRoot);
    }

    [Fact]
    public void 缺值或未知参数不抛异常()
    {
        Assert.Null(StartupOptions.Parse(new[] { "--root" }).ScanRoot);
        Assert.Null(StartupOptions.Parse(new[] { "--root", "--debug" }).ScanRoot);   // 不能把开关当路径
        Assert.Null(StartupOptions.Parse(new[] { "--unknown", "x" }).ScanRoot);
    }

    [Fact]
    public void 相对路径按当前工作目录解析成绝对路径()
    {
        var options = StartupOptions.Parse(new[] { "--root", "." });
        Assert.NotNull(options.ScanRoot);
        Assert.True(Path.IsPathRooted(options.ScanRoot!));
    }
}
