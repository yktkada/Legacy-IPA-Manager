using IdeviceGui.Services;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 版本前置校验（2026-10-31 用户要求）：入队与任务开始前用 IPA 的 `MinimumOSVersion` 比设备 iOS，
/// 不满足就拒绝/判失败、**不上传**。这里是纯函数单测。
/// </summary>
public sealed class VersionRulesTests
{
    [Theory]
    [InlineData("4.3", 4, 3, 0)]
    [InlineData("7.1.1", 7, 1, 1)]
    [InlineData("8", 8, 0, 0)]
    [InlineData(" 8.4.1 ", 8, 4, 1)]
    [InlineData("4.3.3", 4, 3, 3)]
    [InlineData("4.2.10", 4, 2, 10)]     // 用户 2026-11-01：多位数补丁段
    [InlineData("04.03.003", 4, 3, 3)]   // 前导零按数值处理
    public void 解析iOS版本(string text, int major, int minor, int patch)
    {
        Assert.True(IosVersion.TryParse(text, out var v));
        Assert.Equal(new IosVersion(major, minor, patch), v);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("4.x")]
    [InlineData("-1.2")]
    [InlineData("12.5.7.1")]   // 复审 B5：4 段直接拒绝，不静默丢第 4 段
    [InlineData("4.3.")]
    [InlineData("4..3")]
    [InlineData("1.2.3.4.5")]
    public void 解析失败_返回false(string? text)
    {
        Assert.False(IosVersion.TryParse(text, out _));
    }

    [Theory]
    [InlineData("7.1", "7.1.0", 0)]
    [InlineData("7.1.1", "7.1", 1)]
    [InlineData("8.0", "7.1.1", 1)]
    [InlineData("4.3", "8.4.1", -1)]
    [InlineData("4.2.10", "4.2.9", 1)]        // 用户 2026-11-01：按数值比，不是字符串比（字符串会判 4.2.10 < 4.2.9）
    [InlineData("4.2.9", "4.2.10", -1)]
    [InlineData("4.3.3", "4.3", 1)]
    [InlineData("10.0", "9.9", 1)]
    public void 比较iOS版本(string left, string right, int expectedSign)
    {
        IosVersion.TryParse(left, out var a);
        IosVersion.TryParse(right, out var b);
        Assert.Equal(expectedSign, Math.Sign(a.CompareTo(b)));
    }

    [Theory]
    [InlineData("4.3", "7.1.1", null)]        // 包要求低于设备 → 放行
    [InlineData("7.1.1", "7.1.1", null)]      // 相等 → 放行
    [InlineData("8.0", "7.1.1", "需要 iOS 8.0")]   // 包要求高 → 拦
    [InlineData("9.0", "8.4.1", "需要 iOS 9.0")]
    [InlineData("4.2.10", "4.2.9", "需要 iOS 4.2.10")]   // 包要求 4.2.10 > 设备 4.2.9 → 拦（数值比较）
    [InlineData("4.2.9", "4.2.10", null)]                 // 包要求 4.2.9 < 设备 4.2.10 → 放行
    [InlineData("4.3.3", "4.3.2", "需要 iOS 4.3.3")]     // 三位数：4.3.3 > 4.3.2 → 拦
    [InlineData("10.0", "9.3.5", "需要 iOS 10.0")]
    public void 最低系统检查_高于设备就返回原因(string minOs, string device, string? expectedPrefix)
    {
        var reason = VersionRules.CheckMinimumOs(minOs, device);

        if (expectedPrefix is null) Assert.Null(reason);
        else
        {
            Assert.NotNull(reason);
            Assert.StartsWith(expectedPrefix, reason, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(null, "7.1.1")]      // 包没声明 MinOs → 不拦
    [InlineData("", "7.1.1")]
    [InlineData("8.0", null)]        // 设备版本读不到 → 不拦（交给设备自己判）
    [InlineData("8.0", "")]
    [InlineData("abc", "7.1.1")]     // 坏值 → 不拦
    public void 缺数据或坏值_一律放行(string? minOs, string? device)
    {
        Assert.Null(VersionRules.CheckMinimumOs(minOs, device));
    }
}
