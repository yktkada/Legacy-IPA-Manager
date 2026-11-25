using IdeviceGui.Services;
using IdeviceGui.ViewModels;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 日志筛选（b3a，2026-10-31 用户拍板 + 复审 P0-1 采纳）：
/// **显示与否只看 <see cref="LogKind"/>**（不再用关键词猜文案）；简洁模式显示 Start/End/Error，
/// 隐藏 Detail（但照常记录）；解密 IPA 的两条固定告警即使简洁也不显示；工具原始告警/错误照常显示。
/// </summary>
public sealed class LogFilterTests
{
    private static LogLine Detail(string text) => LogLine.Detail(text);
    private static LogLine Start(string text) => LogLine.Start(text);
    private static LogLine End(string text) => LogLine.End(text);
    private static LogLine Error(string text) => LogLine.Error(text);

    // ── 类别规则 ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("[1a4cb027] 开始安装 DOOM.ipa")]
    [InlineData("[1a4cb027] 开始卸载 com.example.app")]
    [InlineData("扫描开始（15 个文件）")]
    public void 简洁模式_开始行显示(string text)
    {
        Assert.True(LogFilter.ShouldShow(Start(text), LogMode.Simple), text);
    }

    [Theory]
    [InlineData("[1a4cb027] 完成 DOOM")]
    [InlineData("[1a4cb027] 失败 DOOM：退出码 1（继续下一个）")]
    [InlineData("[1a4cb027] 已暂停：设备离线（点“继续”重试）")]
    [InlineData("[1a4cb027] 需确认 com.example.app：版本不一致")]
    [InlineData("设备刷新完成：4 台在线 / 共 4 台")]
    [InlineData("[1a4cb027] 已入队 DOOM.ipa")]
    [InlineData("[1a4cb027] 已在队列中，本次点击忽略")]
    [InlineData("[1a4cb027] 清理完成，删除 2 项")]
    public void 简洁模式_结束行显示(string text)
    {
        Assert.True(LogFilter.ShouldShow(End(text), LogMode.Simple), text);
    }

    [Theory]
    [InlineData("错误：数据库不可写，跳过本次扫描：SQLite Error 5")]
    [InlineData("[1a4cb027] 队列异常：设备掉线")]
    [InlineData("刷新设备失败：IOException: device gone")]
    public void 简洁模式_错误行显示(string text)
    {
        Assert.True(LogFilter.ShouldShow(Error(text), LogMode.Simple), text);
    }

    [Theory]
    [InlineData("扫描分批：每 30 行落库并上屏（设置键 Scan.BatchSize）")]
    [InlineData("[1a4cb027] 回读未启用，本次仅按退出码 + ERROR 行判定")]
    [InlineData("[1a4cb027] 空间检查：压缩 12.3 MB + 解压 30.1 MB = 42.4 MB，可用 19.0 GiB")]
    [InlineData("扫描枚举阶段完成：15 项")]
    [InlineData("图标解码：新写 0 / 已缓存 0 / 走兜底 0 / 无候选包 0 / 失败 0（无）")]
    [InlineData("关闭超时：扫描仍未收尾，跳过库与文件日志的显式释放")]
    [InlineData("")]
    public void 简洁模式_过程行隐藏(string text)
    {
        Assert.False(LogFilter.ShouldShow(Detail(text), LogMode.Simple), text);
    }

    [Theory]
    [InlineData("WARNING: could not open file")]
    [InlineData("ERROR: zip_open: failed")]
    public void 简洁模式_工具原始告警与错误仍显示_即使标成过程行(string text)
    {
        Assert.True(LogFilter.ShouldShow(Detail(text), LogMode.Simple), text);
    }

    [Fact]
    public void 详细模式_全部显示_包括过程行与两条固定告警()
    {
        Assert.True(LogFilter.ShouldShow(Detail("扫描分批：每 30 行落库"), LogMode.Detail));
        Assert.True(LogFilter.ShouldShow(
            Detail("WARNING: could not locate iTunesMetadata.plist in archive!"), LogMode.Detail));
    }

    // ── 解密 IPA 的两条固定告警 ─────────────────────────────────────────────

    [Theory]
    [InlineData("WARNING: could not locate iTunesMetadata.plist in archive!")]
    [InlineData("WARNING: could not locate Payload/MemoryInfo.app/SC_Info/MemoryInfo.sinf in archive!")]
    [InlineData("+  1572 ms  LOG  WARNING: could not locate iTunesMetadata.plist in archive!")]
    [InlineData("ERROR: could not locate Payload/X.app/SC_Info/X.sinf in archive!")]
    public void 解密IPA的两条固定告警_识别为噪音且简洁模式不显示(string line)
    {
        Assert.True(LogFilter.IsBenignNoise(line));
        Assert.False(LogFilter.ShouldShow(Detail(line), LogMode.Simple));
        Assert.False(LogFilter.ShouldShow(Error(line), LogMode.Simple));
    }

    [Theory]
    [InlineData("WARNING: could not open file")]
    [InlineData("WARNING: 未签名包缺少 SC_Info（正常输出）")]
    [InlineData("开始安装 DOOM.ipa")]
    [InlineData("")]
    public void 其它告警_不算无意义噪音(string line)
    {
        Assert.False(LogFilter.IsBenignNoise(line));
    }

    // ── 记忆（Settings 值）─────────────────────────────────────────────────

    [Theory]
    [InlineData(null, "Simple")]
    [InlineData("", "Simple")]
    [InlineData("   ", "Simple")]
    [InlineData("simple", "Simple")]
    [InlineData("garbage", "Simple")]
    [InlineData("detail", "Detail")]
    [InlineData(" Detail ", "Detail")]
    [InlineData("\"detail\"", "Detail")]
    public void 设置值解析_坏值一律回简洁(string? raw, string expected)
    {
        // LogMode 是 internal（界面内部概念），测试用名字比较，避免把枚举提升成公开 API。
        Assert.Equal(expected, LogFilter.ParseMode(raw).ToString());
    }

    [Fact]
    public void 设置值序列化往返()
    {
        Assert.Equal("simple", LogFilter.ToSettingValue(LogMode.Simple));
        Assert.Equal("detail", LogFilter.ToSettingValue(LogMode.Detail));
        Assert.Equal(LogMode.Simple, LogFilter.ParseMode(LogFilter.ToSettingValue(LogMode.Simple)));
        Assert.Equal(LogMode.Detail, LogFilter.ParseMode(LogFilter.ToSettingValue(LogMode.Detail)));
    }
}
