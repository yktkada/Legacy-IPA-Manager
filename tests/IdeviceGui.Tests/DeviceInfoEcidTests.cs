using IdeviceGui;
using IdeviceGui.Resources;
using IdeviceGui.Services;
using IdeviceGui.Services.Diagnostics;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// ECID 显示（2026-11-01 用户定）：顶栏十六进制（16 位大写、无前缀、左侧补零），菜单里 hex 与十进制各一行且带值。
/// </summary>
[Collection("DeviceInfoFields")]
public sealed class DeviceInfoEcidTests : IDisposable
{
    public DeviceInfoEcidTests() => DeviceInfo.Fields = DeviceInfoFields.Default;

    public void Dispose() => DeviceInfo.Fields = DeviceInfoFields.Default;

    [Fact]
    public void ECID十六进制_十进制转十六位大写补零()
    {
        var device = MakeDevice();
        device.Ecid = "4294967297";

        Assert.Equal("0000000100000001", device.EcidHex);
        Assert.Equal(Strings.Format(nameof(Strings.Device_Menu_EcidHexFormat), "0000000100000001"), device.EcidHexMenuText);
        Assert.Equal(Strings.Format(nameof(Strings.Device_Menu_EcidDecFormat), "4294967297"), device.EcidDecimalMenuText);
    }

    [Fact]
    public void ECID为空或非法_十六进制给空串()
    {
        var device = MakeDevice();
        Assert.Equal("", device.EcidHex);

        device.Ecid = "not-a-number";
        Assert.Equal("", device.EcidHex);
        Assert.Equal(Strings.Format(nameof(Strings.Device_Menu_EcidHexFormat), "—"), device.EcidHexMenuText);
    }

    [Fact]
    public void 信息行_ECID显示为十六进制()
    {
        var device = MakeDevice();
        device.Ecid = "4294967298";

        Assert.EndsWith("ECID 0000000100000002", device.InfoLine);
    }

    [Fact]
    public void 菜单文本_带值()
    {
        var device = MakeDevice();
        device.Serial = "SAMPLESERIAL01";
        device.ModelNumber = "ME044";
        device.HardwareModel = "N42AP";

        Assert.Equal(Strings.Format(nameof(Strings.Device_Menu_SerialFormat), "SAMPLESERIAL01"), device.SerialMenuText);
        Assert.Equal(Strings.Format(nameof(Strings.Device_Menu_UdidFormat), device.Udid), device.UdidMenuText);
        Assert.Equal(Strings.Format(nameof(Strings.Device_Menu_ModelNumberFormat), "ME044"), device.ModelNumberMenuText);
        Assert.Equal(Strings.Format(nameof(Strings.Device_Menu_HardwareModelFormat), "N42AP"), device.HardwareModelMenuText);
    }

    [Fact]
    public void 健康度菜单行_已含循环次数()
    {
        var device = MakeDevice();
        device.SetBattery(BatteryUsage.Unknown, new BatteryHealth(1430, 1323, 394), BatterySpec.Unknown);

        Assert.Contains(Strings.Format(nameof(Strings.Device_Battery_CyclesSuffixFormat), 394), device.HealthMenuText);
    }

    private static DeviceInfo MakeDevice() => new()
    {
        Udid = "0123456789abcdef0123456789abcdef01234567",
        Name = "iPhone 5",
        Product = "iPhone5,2",
        Version = "8.1.2",
        Build = "12B440",
        CpuArch = "armv7",
        Color = "#e1e4e3",
    };
}
