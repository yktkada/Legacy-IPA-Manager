using IdeviceGui;
using IdeviceGui.Resources;
using IdeviceGui.Services;
using IdeviceGui.Services.Diagnostics;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>设备信息行字段配置（M4-b4 接口预留）：默认集 / 解析兜底 / 顺序与可见性 / 序列化往返。</summary>
public sealed class DeviceInfoFieldsTests
{
    [Fact]
    public void 默认配置_七项按名称型号版本容量颜色电池健康ECID排序()
    {
        var visible = DeviceInfoFields.OrderedVisible(DeviceInfoFields.Default);

        Assert.Equal(
            new[]
            {
                DeviceInfoField.Name,
                DeviceInfoField.Product,
                DeviceInfoField.Version,
                DeviceInfoField.Capacity,
                DeviceInfoField.Color,
                DeviceInfoField.BatteryHealth,
                DeviceInfoField.Ecid,
            },
            visible);
        Assert.All(DeviceInfoFields.Default, s => Assert.True(s.Visible));
    }

    [Fact]
    public void 坏JSON_回默认()
    {
        Assert.Equal(DeviceInfoFields.Default, DeviceInfoFields.Parse("{not json"));
        Assert.Equal(DeviceInfoFields.Default, DeviceInfoFields.Parse("null"));
        Assert.Equal(DeviceInfoFields.Default, DeviceInfoFields.Parse("{\"a\":1}"));
        Assert.Equal(DeviceInfoFields.Default, DeviceInfoFields.Parse(""));
        Assert.Equal(DeviceInfoFields.Default, DeviceInfoFields.Parse(null));
    }

    [Fact]
    public void 关掉的字段_不出现在可见顺序里()
    {
        var settings = DeviceInfoFields.Parse(
            "[{\"id\":\"name\",\"visible\":false,\"order\":0},{\"id\":\"product\",\"visible\":true,\"order\":1}]");

        var visible = DeviceInfoFields.OrderedVisible(settings);

        Assert.DoesNotContain(DeviceInfoField.Name, visible);
        Assert.Contains(DeviceInfoField.Product, visible);
        // 缺的字段按默认补齐（临时配置不会把新字段吃掉）
        Assert.Contains(DeviceInfoField.BatteryHealth, visible);
    }

    [Fact]
    public void order_小的在前_重排生效()
    {
        var settings = DeviceInfoFields.Parse(
            "[{\"id\":\"ecid\",\"order\":0},{\"id\":\"name\",\"order\":9}]");

        var visible = DeviceInfoFields.OrderedVisible(settings);

        Assert.Equal(DeviceInfoField.Ecid, visible[0]);
        Assert.Equal(DeviceInfoField.Name, visible[^1]);
    }

    [Fact]
    public void 未知id丢弃_重复id只保留第一次()
    {
        var settings = DeviceInfoFields.Parse(
            "[{\"id\":\"bogus\",\"order\":0},{\"id\":\"color\",\"visible\":false,\"order\":1}," +
            "{\"id\":\"color\",\"visible\":true,\"order\":2}]");

        Assert.Single(settings, s => s.Field == DeviceInfoField.Color);
        Assert.False(settings.First(s => s.Field == DeviceInfoField.Color).Visible);
        Assert.DoesNotContain(settings, s => !Enum.IsDefined(s.Field));
    }

    [Fact]
    public void 序列化后能原样解析回来()
    {
        var custom = new[]
        {
            new DeviceInfoFieldSetting(DeviceInfoField.Ecid, true, 0),
            new DeviceInfoFieldSetting(DeviceInfoField.Name, false, 1),
            new DeviceInfoFieldSetting(DeviceInfoField.Version, true, 2),
        };

        var json = DeviceInfoFields.Serialize(custom);
        var parsed = DeviceInfoFields.Parse(json);

        Assert.Equal(DeviceInfoField.Ecid, parsed[0].Field);
        Assert.Equal(0, parsed[0].Order);
        Assert.Equal(DeviceInfoField.Name, parsed[1].Field);
        Assert.Equal(1, parsed[1].Order);
        Assert.False(parsed[1].Visible);
    }
}

/// <summary>设备信息行文本（M4-b4）：字段拼装 / 空值不显示 / 容量与颜色格式。</summary>
[Collection("DeviceInfoFields")]
public sealed class DeviceInfoTests : IDisposable
{
    public DeviceInfoTests() => DeviceInfo.Fields = DeviceInfoFields.Default;

    public void Dispose() => DeviceInfo.Fields = DeviceInfoFields.Default;

    [Fact]
    public void 信息行_默认集按顺序拼并且跳过空段()
    {
        var device = MakeDevice();
        device.SetSpace(new DiskUsage(true, 58L * 1024 * 1024 * 1024, 19L * 1024 * 1024 * 1024));
        device.SetBattery(
            new BatteryUsage(100, true, false),
            new BatteryHealth(1430, 1323, 394),
            BatterySpec.Unknown);

        Assert.Equal(
            $"iPhone 5(iPhone5,2) · 8.1.2(12B440) · {Strings.Format(nameof(Strings.Device_Space_FreeFormat), "19.0", "58.0")} · #e1e4e3 · 92.5%",
            device.InfoLine);
    }

    [Fact]
    public void 电池健康取不到_整段不显示()
    {
        var device = MakeDevice();
        device.SetSpace(new DiskUsage(true, 58L * 1024 * 1024 * 1024, 19L * 1024 * 1024 * 1024));

        Assert.DoesNotContain("%", device.InfoLine);
        Assert.Equal(
            $"iPhone 5(iPhone5,2) · 8.1.2(12B440) · {Strings.Format(nameof(Strings.Device_Space_FreeFormat), "19.0", "58.0")} · #e1e4e3",
            device.InfoLine);
    }

    [Fact]
    public void 容量未知_不显示容量段()
    {
        var device = MakeDevice();

        Assert.DoesNotContain("GB", device.InfoLine);
    }

    [Fact]
    public void 命名色_显示原文并标记不画色块()
    {
        var device = MakeDevice();
        device.Color = "white";

        Assert.Equal("white", device.ColorText);
        Assert.False(device.HasColorSwatch);
    }

    [Fact]
    public void 十六进制色_显示原文并画色块()
    {
        var device = MakeDevice();
        device.Color = "#e1e4e3";

        Assert.Equal("#e1e4e3", device.ColorText);
        Assert.True(device.HasColorSwatch);
        Assert.NotNull(device.ColorBrush);
    }

    [Fact]
    public void ToolTip_带序列号UDID与电池明细()
    {
        var device = MakeDevice();
        device.Serial = "SAMPLESERIAL01";
        device.SetBattery(
            BatteryUsage.Unknown,
            new BatteryHealth(1430, 1323, 394),
            BatterySpec.Unknown);

        Assert.Contains("SAMPLESERIAL01", device.InfoToolTip);
        Assert.Contains(device.Udid, device.InfoToolTip);
        Assert.Contains(Strings.Format(nameof(Strings.Device_Battery_CyclesSuffixFormat), 394), device.InfoToolTip);
    }

    [Fact]
    public void 字段配置里关掉名称_信息行不再出现名称()
    {
        DeviceInfo.Fields = DeviceInfoFields.Parse("[{\"id\":\"name\",\"visible\":false}]");
        var device = MakeDevice();

        Assert.StartsWith("iPhone 5(iPhone5,2)", device.InfoLine);
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
