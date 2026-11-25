using IdeviceGui.ViewModels.Library;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 绿色（队列安装成功）的记录口径（2026-11-02 用户定）：
/// 只存内存、**按设备分开记**、身份 = BundleId + 版本、**刷新只清当前设备**。
/// </summary>
public sealed class DeviceSuccessMarksTests
{
    [Fact]
    public void 同一设备同一包_只记一次()
    {
        var marks = new DeviceSuccessMarks();
        marks.Add("udid1", "com.a", "1.0");
        marks.Add("udid1", "com.a", "1.0");

        Assert.True(marks.Contains("udid1", "com.a", "1.0"));
        Assert.Equal(1, marks.Count("udid1"));
    }

    [Fact]
    public void 按设备分开记_别的设备看不到()
    {
        var marks = new DeviceSuccessMarks();
        marks.Add("udid1", "com.a", "1.0");

        Assert.False(marks.Contains("udid2", "com.a", "1.0"));
        Assert.Equal(0, marks.Count("udid2"));
    }

    [Fact]
    public void 版本不同算不同包_但版本未知时按BundleId兜底()
    {
        var marks = new DeviceSuccessMarks();
        marks.Add("udid1", "com.a", "1.0");

        Assert.False(marks.Contains("udid1", "com.a", "1.1"));
        // 审查 A3（2026-11-03 修）：任一侧版本未知 → 按 BundleId 兜底（否则重解析出事后绿会莫名消失）
        Assert.True(marks.Contains("udid1", "com.a", null));
    }

    [Fact]
    public void BundleId大小写不敏感_UDID也不敏感()
    {
        var marks = new DeviceSuccessMarks();
        marks.Add("UDID1", "COM.A", "1.0");

        Assert.True(marks.Contains("udid1", "com.a", "1.0"));
    }

    [Fact]
    public void 清一台设备不影响别的设备()
    {
        var marks = new DeviceSuccessMarks();
        marks.Add("udid1", "com.a", "1.0");
        marks.Add("udid2", "com.a", "1.0");

        marks.Clear("udid1");

        Assert.Equal(0, marks.Count("udid1"));
        Assert.Equal(1, marks.Count("udid2"));   // 刷新只清当前设备（2026-11-02 用户定）
    }

    [Fact]
    public void 空UDID或空BundleId_不记()
    {
        var marks = new DeviceSuccessMarks();
        marks.Add(null, "com.a", "1.0");
        marks.Add("udid1", "", "1.0");

        Assert.Equal(0, marks.Count("udid1"));
        Assert.False(marks.Contains(null, "com.a", "1.0"));
    }
}
