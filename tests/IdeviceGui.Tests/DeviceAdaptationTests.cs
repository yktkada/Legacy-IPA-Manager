using IdeviceGui.Services.Parsing;
using IdeviceGui.ViewModels.Library;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>M5-lite 设备族最高适配识别（2026-11-04 定稿）：纯逻辑映射 / 取最高 / 基线 / unknown。</summary>
public sealed class DeviceAdaptationTests
{
    private static string Evidence(
        (int W, int H)[]? launch = null, bool storyboard = false, int[]? icons = null)
        => LaunchEvidenceCodec.Serialize(launch ?? Array.Empty<(int, int)>(), storyboard, icons ?? Array.Empty<int>());

    [Fact]
    public void 缺设备族_显示unknown()
    {
        Assert.Equal("unknown", DeviceAdaptation.Describe(null, null));
        Assert.Equal("unknown", DeviceAdaptation.Describe("", Evidence()));
    }

    [Fact]
    public void iPhone_无证据_兜底OG3点5()
    {
        Assert.Equal("OG 3.5\"", DeviceAdaptation.Describe("iPhone", null));
        Assert.Equal("OG 3.5\"", DeviceAdaptation.Describe("iPhone", Evidence()));
    }

    [Theory]
    [InlineData(320, 480, "OG 3.5\"")]
    [InlineData(640, 960, "3.5\"")]
    [InlineData(320, 568, "4\"")]
    [InlineData(640, 1136, "4\"")]
    [InlineData(375, 667, "4.7\"/5.5\"")]
    [InlineData(414, 736, "4.7\"/5.5\"")]
    [InlineData(750, 1334, "4.7\"/5.5\"")]
    [InlineData(1242, 2208, "4.7\"/5.5\"")]
    public void iPhone_取最高档(int w, int h, string expected)
    {
        Assert.Equal(expected, DeviceAdaptation.Describe("iPhone", Evidence(new[] { (w, h) })));
    }

    [Fact]
    public void iPhone_启动图与图标矛盾_取更高档()
    {
        var json = Evidence(new[] { (320, 480) }, icons: new[] { 114 });
        Assert.Equal("3.5\"", DeviceAdaptation.Describe("iPhone", json));

        var higher = Evidence(new[] { (320, 568) }, icons: new[] { 57 });
        Assert.Equal("4\"", DeviceAdaptation.Describe("iPhone", higher));
    }

    [Fact]
    public void iPhone_storyboard_判4点7()
    {
        Assert.Equal("4.7\"/5.5\"", DeviceAdaptation.Describe("iPhone", Evidence(storyboard: true)));
    }

    [Fact]
    public void iPad_无证据兜底iPad_iPadR取最高()
    {
        Assert.Equal("iPad", DeviceAdaptation.Describe("iPad", null));
        Assert.Equal("iPad Retina", DeviceAdaptation.Describe("iPad", Evidence(new[] { (1536, 2048) })));
        Assert.Equal("iPad Retina", DeviceAdaptation.Describe("iPad", Evidence(icons: new[] { 144 })));
        Assert.Equal("iPad", DeviceAdaptation.Describe("iPad", Evidence(new[] { (768, 1024) })));
    }

    [Fact]
    public void Universal_合并表()
    {
        Assert.Equal("Universal Retina", DeviceAdaptation.Describe("Universal",
            Evidence(new[] { (375, 667), (1536, 2048) })));

        Assert.Equal("4\" + iPad Retina", DeviceAdaptation.Describe("Universal",
            Evidence(new[] { (320, 568), (1536, 2048) })));

        Assert.Equal("3.5\" + iPad", DeviceAdaptation.Describe("Universal",
            Evidence(new[] { (640, 960), (768, 1024) })));

        Assert.Equal("4.7\"/5.5\" + iPad", DeviceAdaptation.Describe("Universal",
            Evidence(new[] { (375, 667), (768, 1024) })));

        // 用户 2026-11-04 定：OG + iPad 年代不可能，但不特殊处理，直接拼接
        Assert.Equal("OG 3.5\" + iPad", DeviceAdaptation.Describe("Universal",
            Evidence(new[] { (320, 480), (768, 1024) })));

        Assert.Equal("OG 3.5\" + iPad Retina", DeviceAdaptation.Describe("Universal",
            Evidence(new[] { (320, 480), (1536, 2048) })));
    }

    [Fact]
    public void 真实对照包证据()
    {
        // NFS12：320×480 + 640×960 + 图标 57/114 → 3.5"（资源支持 Retina；引擎特例不追）
        Assert.Equal("3.5\"", DeviceAdaptation.Describe("iPhone",
            Evidence(new[] { (320, 480), (640, 960) }, icons: new[] { 57, 114 })));

        // 军棋：640×1136（4"）+ iPad 1536×2048 + 图标 120/144 → 4" + iPad R
        Assert.Equal("4\" + iPad Retina", DeviceAdaptation.Describe("Universal",
            Evidence(new[] { (640, 1136), (1536, 2048), (768, 1024), (320, 480), (640, 960) },
                icons: new[] { 57, 72, 120, 144 })));

        // BMX：640×1136 + iPad 2048×1536 → 4" + iPad R
        Assert.Equal("4\" + iPad Retina", DeviceAdaptation.Describe("Universal",
            Evidence(new[] { (640, 1136), (2048, 1536), (1024, 768), (320, 480), (640, 960) },
                icons: new[] { 57, 72, 76, 114, 120, 144, 152 })));

        // Mini Metro：375×667 + 414×736 + iPad 1536×2048 → Universal R
        Assert.Equal("Universal Retina", DeviceAdaptation.Describe("Universal",
            Evidence(new[] { (414, 736), (375, 667), (1536, 2048), (768, 1024), (320, 480), (320, 568) },
                icons: new[] { 57, 72, 76, 114, 120, 144, 152, 167, 180 })));
    }

    [Fact]
    public void 证据JSON_往返()
    {
        var json = Evidence(new[] { (320, 480), (640, 960) }, storyboard: true, icons: new[] { 57, 114 });
        var dto = LaunchEvidenceCodec.Parse(json);

        Assert.NotNull(dto);
        Assert.True(dto!.S);
        Assert.Contains(dto.L, s => s is [320, 480]);
        Assert.Contains(114, dto.I);
        Assert.Null(LaunchEvidenceCodec.Parse("not-json"));
    }
    [Fact]
    public void Retina文案开关_关闭时用R()
    {
        Assert.Equal("iPad R", DeviceAdaptation.Describe("iPad",
            Evidence(new[] { (1536, 2048) }), useRetinaText: false));
        Assert.Equal("Universal R", DeviceAdaptation.Describe("Universal",
            Evidence(new[] { (375, 667), (1536, 2048) }), useRetinaText: false));
        Assert.Equal("4\" + iPad R", DeviceAdaptation.Describe("Universal",
            Evidence(new[] { (320, 568), (1536, 2048) }), useRetinaText: false));
    }

}
