using System.IO;
using IdeviceGui.Resources;
using IdeviceGui.Services.Diagnostics;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 电池信息解析（设备信息区改造的地基）。fixtures/battery 是 2026-10-31 从 4 台真机抓的**原文**：
/// GasGauge（iOS 4.1 / 6.1.3 / 7.1.1 / 8.4.1）、battery 域、以及 iPod touch 5 的 AppleARMPMUCharger。
/// </summary>
public sealed class BatteryInfoReaderTests
{
    // ── fixture 读取 ────────────────────────────────────────────────────────

    private static string FixturesRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "fixtures");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"找不到 fixtures 目录（从 {AppContext.BaseDirectory} 向上查找）。");
    }

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(FixturesRoot(), "battery", name));

    // ── GasGauge：健康度 ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("iphone4-ios4.1-gasgauge.xml", 1420, 1188, 898, "83.7", false)]
    [InlineData("ipad2-ios6.1.3-gasgauge.xml", 6583, 6256, 106, "95.0", false)]
    [InlineData("iphone4s-ios7.1.1-gasgauge.xml", 1430, 1323, 394, "92.5", false)]
    [InlineData("ipodtouch5-ios8.4.1-gasgauge.xml", 1080, 1100, 0, "101.9", true)]
    public void GasGauge_真机原文_解析出设计容量满充容量循环次数_并按比例判不可信(
        string file, int design, int fullCharge, int cycles, string pct, bool unreliable)
    {
        var health = BatteryInfoReader.ParseGasGauge(ReadFixture(file));
        var expectedDisplay = unreliable
            ? Strings.Format(nameof(Strings.Device_Battery_UnreliablePercentFormat), pct)
            : $"{pct}%";

        Assert.Equal(design, health.DesignMah);
        Assert.Equal(fullCharge, health.FullChargeMah);
        Assert.Equal(cycles, health.CycleCount);
        Assert.Equal(expectedDisplay, health.Display);
        Assert.Equal(unreliable, health.Unreliable);
    }

    [Fact]
    public void GasGauge_恰好等于一百百分比_判不可信()
    {
        var health = new BatteryHealth(1000, 1000, 12);

        Assert.Equal(Strings.Format(nameof(Strings.Device_Battery_UnreliablePercentFormat), "100.0"), health.Display);
        Assert.True(health.Unreliable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ERROR: Unable to retrieve GasGauge from device.")]
    [InlineData("<?xml version=\"1.0\"?><plist version=\"1.0\"><dict></dict></plist>")]
    [InlineData("<?xml version=\"1.0\"?><plist version=\"1.0\"><dict><key>GasGauge</key><dict><key>DesignCapacity</key><integer>0</integer><key>FullChargeCapacity</key><integer>0</integer></dict></dict></plist>")]
    public void GasGauge_空或坏数据_一律返回未知且显示破折号(string raw)
    {
        var health = BatteryInfoReader.ParseGasGauge(raw);

        Assert.False(health.HasValue);
        Assert.Null(health.Ratio);
        Assert.Equal("—", health.Display);
        Assert.False(health.Unreliable);
    }

    [Fact]
    public void GasGauge_ToolTip_带容量与循环次数_不可信时追加提示()
    {
        var normal = new BatteryHealth(1430, 1323, 394);
        var bad = new BatteryHealth(1080, 1100, 0);

        var expectedNormal = Strings.Format(
            nameof(Strings.Device_Battery_HealthTooltipFormat),
            "92.5",
            1323,
            1430,
            Strings.Format(nameof(Strings.Device_Battery_CyclesSuffixFormat), 394),
            "");
        Assert.Equal(expectedNormal, normal.ToolTip);
        Assert.Contains("101.9%", bad.ToolTip, StringComparison.Ordinal);
        Assert.Contains(Strings.Device_Battery_UnreliableWarningSuffix, bad.ToolTip, StringComparison.Ordinal);
    }

    // ── battery 域：实时电量 ────────────────────────────────────────────────

    [Theory]
    [InlineData("iphone4-ios4.1-battery.txt", 100, true, false)]
    [InlineData("ipad2-ios6.1.3-battery.txt", 100, false, false)]
    [InlineData("iphone4s-ios7.1.1-battery.txt", 100, false, true)]
    [InlineData("ipodtouch5-ios8.4.1-battery.txt", 90, true, false)]
    public void Battery域_真机原文_解析出电量与充电状态(string file, int percent, bool charging, bool fully)
    {
        var usage = BatteryInfoReader.ParseUsage(ReadFixture(file));

        Assert.Equal(percent, usage.Percent);
        Assert.Equal(charging, usage.Charging);
        Assert.Equal(fully, usage.FullyCharged);
        Assert.True(usage.HasValue);
    }

    [Fact]
    public void Battery域_边缘输入_显示文案正确()
    {
        Assert.Equal("—", BatteryInfoReader.ParseUsage("").Display);
        Assert.Equal("—", BatteryInfoReader.ParseUsage("ERROR: no device").Display);
        Assert.Equal("42%", BatteryInfoReader.ParseUsage("BatteryCurrentCapacity: 42").Display);
        Assert.Equal(
            Strings.Format(nameof(Strings.Device_Battery_ChargingFormat), 42),
            BatteryInfoReader.ParseUsage("BatteryCurrentCapacity: 42\nBatteryIsCharging: true").Display);
        Assert.Equal(
            Strings.Format(nameof(Strings.Device_Battery_FullyChargedFormat), 100),
            BatteryInfoReader.ParseUsage("BatteryCurrentCapacity: 100\nFullyCharged: true").Display);
    }

    // ── AppleARMPMUCharger：生产商 ─────────────────────────────────────────

    [Fact]
    public void Charger节点_真机原文_解析出生产商序列号温度电压()
    {
        var spec = BatteryInfoReader.ParseChargerEntry(ReadFixture("ipodtouch5-ios8.4.1-charger.xml"));

        Assert.True(spec.HasValue);
        Assert.Equal("Y", spec.Manufacturer);
        Assert.Equal("SAMPLEBATTERYSERIAL01", spec.Serial);
        Assert.Equal(27.39, spec.TemperatureC!.Value, 3);   // fixture 抓取时 2739（0.01 ℃），会随采样漂移
        Assert.Equal(4.010, spec.VoltageV!.Value, 3);       // fixture 抓取时 4010 mV
        Assert.Equal("27.4 ℃", spec.TemperatureDisplay);
        Assert.Equal("4.010 V", spec.VoltageDisplay);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ERROR: Unable to retrieve IORegistry from device.")]
    [InlineData("<?xml version=\"1.0\"?><plist version=\"1.0\"><dict></dict></plist>")]
    public void Charger节点_空或坏数据_返回未知且显示破折号(string raw)
    {
        var spec = BatteryInfoReader.ParseChargerEntry(raw);

        Assert.False(spec.HasValue);
        Assert.Equal("—", spec.TemperatureDisplay);
        Assert.Equal("—", spec.VoltageDisplay);
    }
}
