using IdeviceGui.Services.Data;
using IdeviceGui.ViewModels.Library;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// "与当前设备兼容"（2026-11-02 用户定）：只检测**设备族 + 最低 iOS**，不做完整 M5 判定。
/// </summary>
public sealed class DeviceCompatTests
{
    private static LibraryRow Row(string family, string minOs)
    {
        var row = new LibraryRow { Id = 1, FilePath = @"C:\tmp\x.ipa", FileName = "x.ipa" };
        row.UpdateFrom(new PackageRow
        {
            Id = 1,
            FilePath = @"C:\tmp\x.ipa",
            FileName = "x.ipa",
            DeviceFamily = family,
            MinOs = minOs,
            ParseState = ParseStates.Ok,
            ExistsFlag = true,
        });
        return row;
    }

    [Theory]
    [InlineData("Universal", true)]
    [InlineData("", true)]   // unknown = 早期 iPhone OG 3.5，按 iPhone 兼容
    [InlineData("unknown", true)]
    [InlineData("iPhone", true)]
    [InlineData("iPad", false)]
    public void iPhone设备_设备族判定(string family, bool compatible)
        => Assert.Equal(compatible, DeviceCompat.Pass(Row(family, "4.0"), CompatFilterMode.Installable, "iPhone5,2", "8.1.2", "N42AP"));

    [Theory]
    [InlineData("Universal", true)]
    [InlineData("", false)]
    [InlineData("unknown", false)]
    [InlineData("iPad", true)]
    [InlineData("iPhone", false)]
    [InlineData("2", false)]   // 显示为 unknown → iPad 不放行
    [InlineData("1", false)]
    [InlineData("1,2", false)] // 显示为 unknown → iPad 不放行
    public void iPad设备_设备族判定(string family, bool compatible)
        => Assert.Equal(compatible, DeviceCompat.Pass(Row(family, "4.0"), CompatFilterMode.Installable, "iPad2,1", "6.1.3", "K93AP"));

    [Theory]
    [InlineData("4.0", "4.1", true)]
    [InlineData("5.0", "4.1", false)]
    [InlineData("8.1.2", "8.1.2", true)]
    [InlineData("6.0.1", "7.1.1", true)]
    [InlineData("", "4.1", true)]
    public void 最低iOS判定(string minOs, string deviceVersion, bool compatible)
        => Assert.Equal(compatible, DeviceCompat.Pass(Row("Universal", minOs), CompatFilterMode.Installable, "iPhone3,1", deviceVersion, "N90AP"));

    [Fact]
    public void 显示unknown的包_iPhone可安装_iPad不可安装()
    {
        var phone = Row("iPhone", "4.0");
        phone.DeviceFamilyText = "unknown";
        Assert.True(DeviceCompat.Pass(phone, CompatFilterMode.Installable, "iPhone5,2", "8.1.2", "N42AP"));

        var pad = Row("iPad", "4.0");
        pad.DeviceFamilyText = "unknown";
        Assert.False(DeviceCompat.Pass(pad, CompatFilterMode.Installable, "iPad2,1", "6.1.3", "K93AP"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void unknown设备族也必须检查最低iOS(int modeValue)
    {
        var mode = (CompatFilterMode)modeValue;
        var row = Row("iPhone", "4.3");
        row.DeviceFamilyText = "unknown";

        // iOS 4.0 设备不能把要求 4.3 的 unknown 包当作可安装/已适配。
        Assert.False(DeviceCompat.Pass(row, mode, "iPhone3,1", "4.0", "N90AP"));

        // 满足 MinOs 后：可安装放行，已适配仍因无法证明档位而不放行。
        Assert.True(DeviceCompat.Pass(row, CompatFilterMode.Installable, "iPhone3,1", "4.3", "N90AP"));
        Assert.False(DeviceCompat.Pass(row, CompatFilterMode.Adapted, "iPhone3,1", "4.3", "N90AP"));
    }

    [Fact]
    public void 已知设备族要求iOS4_3在iOS4_0上不可安装()
    {
        var row = Row("iPhone", "4.3");
        row.DeviceFamilyText = "iPhone";

        Assert.False(DeviceCompat.Pass(row, CompatFilterMode.Installable, "iPhone3,1", "4.0", "N90AP"));
        Assert.False(DeviceCompat.Pass(row, CompatFilterMode.Adapted, "iPhone3,1", "4.0", "N90AP"));
    }

    [Fact]
    public void 缺设备信息时放行()
        => Assert.True(DeviceCompat.Pass(Row("iPad", "9.0"), CompatFilterMode.Installable, null, null, null));

    [Fact]
    public void 版本段比较_按数值不按字符串()
    {
        Assert.True(DeviceCompat.TryParse("4.10", out var a));
        Assert.True(DeviceCompat.TryParse("4.9.1", out var b));
        Assert.True(DeviceCompat.Compare(a, b) > 0);
    }
}
