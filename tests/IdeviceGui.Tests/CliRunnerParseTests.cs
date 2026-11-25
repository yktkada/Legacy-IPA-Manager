using IdeviceGui;
using IdeviceGui.Resources;
using IdeviceGui.Services;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// CLI 纯函数与已安装集合的查询语义。不需要设备、不需要子进程。
/// </summary>
public sealed class CliRunnerParseTests
{
    [Theory]
    [InlineData("Transfer: 45%", 45)]
    [InlineData("Transfer:100% (12345 bytes)", 100)]
    [InlineData("  Transfer:  7 %", 7)]
    [InlineData("Transfer: 33.5%", 33.5)]
    [InlineData("Transfer: 0%", 0)]
    [InlineData("Copying 'x.ipa' to device...\n  Transfer:  75%  ( 28.99 MiB/s)", 75)]
    public void TryParseTransferPercent_合法行能解析(string line, double expected)
    {
        Assert.True(CliRunner.TryParseTransferPercent(line, out var percent), "应解析成功：" + line);
        Assert.Equal(expected, percent, 3);
    }

    [Theory]
    [InlineData("Transferring...")]
    [InlineData("Transfer: abc%")]
    [InlineData("Transfer: 101%")]
    [InlineData("Transfer: 45")]
    [InlineData("Installing 'com.example.app'")]
    [InlineData("Transferred 1.30 MiB in 0.05s")]
    [InlineData("")]
    public void TryParseTransferPercent_非法行被拒绝(string line)
    {
        Assert.False(CliRunner.TryParseTransferPercent(line, out _), "不该解析成功：" + line);
    }
}

public sealed class InstalledAppSetTests
{
    private static readonly InstalledApp[] Apps =
    {
        new("com.example.app", "2.2", true),
        new("LineArt", "2.2", true),
        new("com.example.unknown", "", false),
    };

    [Fact]
    public void Find_BundleId不区分大小写()
    {
        Assert.NotNull(InstalledAppSet.Find(Apps, "LINEART"));
        Assert.NotNull(InstalledAppSet.Find(Apps, "lineart"));
    }

    [Fact]
    public void Find_找不到返回null()
    {
        Assert.Null(InstalledAppSet.Find(Apps, "com.not.here"));
    }

    [Fact]
    public void Find_集合为null或BundleId为空时返回null()
    {
        Assert.Null(InstalledAppSet.Find(null, "com.example.app"));
        Assert.Null(InstalledAppSet.Find(Apps, ""));
        Assert.Null(InstalledAppSet.Find(Apps, "   "));
    }

    [Fact]
    public void VersionsMatch_版本一致返回true()
    {
        var app = new InstalledApp("com.example.app", "2.2", true);
        Assert.True(InstalledAppSet.VersionsMatch(app, "2.2"));
        Assert.True(InstalledAppSet.VersionsMatch(app, " 2.2 "));
    }

    [Fact]
    public void VersionsMatch_版本不一致返回false()
    {
        var app = new InstalledApp("com.example.app", "2.2", true);
        Assert.False(InstalledAppSet.VersionsMatch(app, "2.3"));
    }

    [Fact]
    public void VersionsMatch_任一侧未知返回null_不判不一致()
    {
        var unknown = new InstalledApp("com.example.app", "", false);
        Assert.Null(InstalledAppSet.VersionsMatch(unknown, "2.2"));

        var known = new InstalledApp("com.example.app", "2.2", true);
        Assert.Null(InstalledAppSet.VersionsMatch(known, ""));
        Assert.Null(InstalledAppSet.VersionsMatch(known, null));
    }
}

public sealed class QueueStatusTextTests
{
    [Fact]
    public void 每个代号都有文案()
    {
        foreach (var status in Enum.GetValues<QueueStatus>())
        {
            var text = status.ToText();
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.NotEqual(Strings.Common_Unknown, text);
        }
    }

    [Fact]
    public void 文案与07文档一致()
    {
        Assert.Equal(Strings.Queue_Status_Pending, QueueStatus.Pending.ToText());
        Assert.Equal(Strings.Queue_Status_VerifyDevice, QueueStatus.VerifyDevice.ToText());
        Assert.Equal(Strings.Queue_Status_Precheck, QueueStatus.Precheck.ToText());
        Assert.Equal(Strings.Queue_Status_Cleaning, QueueStatus.Cleaning.ToText());
        Assert.Equal(Strings.Queue_Status_SpaceCheck, QueueStatus.SpaceCheck.ToText());
        Assert.Equal(Strings.Queue_Status_Installing, QueueStatus.Installing.ToText());
        Assert.Equal(Strings.Queue_Status_Uninstalling, QueueStatus.Uninstalling.ToText());
        Assert.Equal(Strings.Queue_Status_Readback, QueueStatus.Readback.ToText());
        Assert.Equal(Strings.Queue_Status_Paused, QueueStatus.Paused.ToText());
        Assert.Equal(Strings.Queue_Status_Failed, QueueStatus.Failed.ToText());
        Assert.Equal(Strings.Queue_Status_Done, QueueStatus.Done.ToText());
    }
}
