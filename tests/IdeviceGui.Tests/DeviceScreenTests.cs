using IdeviceGui.Services.Data;
using IdeviceGui.ViewModels.Library;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>设备档位识别（2026-11-05）：HardwareModel 严格区分大小写，ProductType 兜底。</summary>
public sealed class DeviceScreenTests
{
    [Fact]
    public void HardwareModel_严格大小写()
    {
        Assert.Equal(ScreenTier.Retina35, DeviceScreen.FromHardwareModel("N90AP"));
        Assert.Null(DeviceScreen.FromHardwareModel("n90ap"));
        Assert.Equal(ScreenTier.Screen4, DeviceScreen.FromHardwareModel("N42AP"));
        Assert.Equal(ScreenTier.Screen4, DeviceScreen.FromHardwareModel("N78AP"));
        Assert.Equal(ScreenTier.Ipad, DeviceScreen.FromHardwareModel("K93AP"));
    }

    [Theory]
    [InlineData("iPhone2,1", ScreenTier.Og35)]
    [InlineData("iPhone3,1", ScreenTier.Retina35)]
    [InlineData("iPhone4,1", ScreenTier.Retina35)]
    [InlineData("iPhone5,2", ScreenTier.Screen4)]
    [InlineData("iPhone6,1", ScreenTier.Screen4)]
    [InlineData("iPhone7,2", ScreenTier.Screen47)]
    [InlineData("iPhone8,1", ScreenTier.Screen47)]
    [InlineData("iPhone8,4", ScreenTier.Screen4)]   // iPhone SE 第 1 代：4 英寸
    [InlineData("iPad2,1", ScreenTier.Ipad)]
    [InlineData("iPad3,1", ScreenTier.IpadRetina)]
    [InlineData("iPod5,1", ScreenTier.Screen4)]
    public void ProductType_映射(string product, ScreenTier expected)
        => Assert.Equal(expected, DeviceScreen.FromProductType(product));

    [Fact]
    public void Resolve_Hardware优先_不一致时报告()
    {
        var (tier, mismatch) = DeviceScreen.Resolve("N78AP", "iPad2,1");
        Assert.Equal(ScreenTier.Screen4, tier);
        Assert.True(mismatch);

        var (tier2, mismatch2) = DeviceScreen.Resolve("N90AP", "iPhone4,1");
        Assert.Equal(ScreenTier.Retina35, tier2);
        Assert.False(mismatch2);

        var (tier3, mismatch3) = DeviceScreen.Resolve("n90ap", "iPhone5,2");
        Assert.Equal(ScreenTier.Screen4, tier3);   // 大小写不匹配 → 回落 ProductType
        Assert.False(mismatch3);
    }
    [Theory]
    [InlineData("N78AP", "iPad2,1", "iPod touch 5(iPad2,1)")]
    [InlineData("N90AP", "iPhone3,1", "iPhone 4(iPhone3,1)")]
    [InlineData("N94AP", "iPhone4,1", "iPhone 4S(iPhone4,1)")]
    [InlineData("N42AP", "iPhone5,2", "iPhone 5(iPhone5,2)")]
    [InlineData("K93AP", "iPad2,1", "iPad 2(iPad2,1)")]
    [InlineData(null, "iPhone7,2", "iPhone 6(iPhone7,2)")]
    [InlineData("n78ap", "iPad2,1", "iPad 2 Wi-Fi(iPad2,1)")]
    public void 顶栏型号文案_营销名加ProductType(string? hardware, string product, string expected)
        => Assert.Equal(expected, DeviceScreen.ModelLabel(hardware, product));

    [Fact]
    public void ProductType全表_包含182条且SE第1代显示名正确()
    {
        Assert.Equal(182, DeviceProductTypes.Names.Count);
        Assert.Equal("iPhone SE (第 1 代)(iPhone8,4)", DeviceScreen.ModelLabel(null, "iPhone8,4"));
        Assert.Equal("iPhone 6(iPhone7,2)", DeviceScreen.ModelLabel(null, "iPhone7,2"));
    }

}
