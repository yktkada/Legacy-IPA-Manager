using IdeviceGui;
using IdeviceGui.Resources;
using IdeviceGui.Services;
using IdeviceGui.ViewModels;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 安装段进度（2026-11-03 用户定）：`ideviceinstaller` 的 `Install: <阶段> (NN%)` 行 → `InstallPercent`；
/// 队列行显示 `[安装中 NN%]`（docs/07 §3.10.1）。
/// </summary>
public sealed class InstallProgressParseTests
{
    /// <summary>用户 2026-11-03 给的真机日志样本（iPhone 4 / iOS 4.1 一次安装）��</summary>
    public static TheoryData<string, double> GoldenLines => new()
    {
        { "Install: CreatingStagingDirectory (5%)", 5 },
        { "Install: ExtractingPackage (15%)", 15 },
        { "Install: InspectingPackage (20%)", 20 },
        { "Install: TakingInstallLock (20%)", 20 },
        { "Install: PreflightingApplication (30%)", 30 },
        { "Install: VerifyingApplication (40%)", 40 },
        { "Install: CreatingContainer (50%)", 50 },
        { "Install: InstallingApplication (60%)", 60 },
        { "Install: PostflightingApplication (70%)", 70 },
        { "Install: SandboxingApplication (80%)", 80 },
        { "Install: GeneratingApplicationMap (90%)", 90 },
    };

    [Theory]
    [MemberData(nameof(GoldenLines))]
    public void 解析真机日志样本(string line, double expected)
    {
        Assert.True(CliRunner.TryParseInstallPercent(line, out var percent));
        Assert.Equal(expected, percent, 3);
    }

    [Fact]
    public void 前后空白与多余空格也能解析()
    {
        Assert.True(CliRunner.TryParseInstallPercent("  Install:  InstallingApplication (60%)  ", out var p));
        Assert.Equal(60d, p, 3);
    }

    [Theory]
    [InlineData("Transfer: 40%")]
    [InlineData("Installing 'com.a'")]
    [InlineData("Install: InstallingApplication")]
    [InlineData("Install: InstallingApplication (abc%)")]
    [InlineData("Install: InstallingApplication 60%")]
    [InlineData("Archive: something (50%)")]
    [InlineData("")]
    [InlineData(null)]
    public void 不是安装进度行_返回false(string? line)
    {
        Assert.False(CliRunner.TryParseInstallPercent(line!, out var percent));
        Assert.Equal(0d, percent, 3);
    }

    [Fact]
    public void 队列行_安装中显示百分比()
    {
        var item = new QueueItem
        {
            Udid = "udid",
            Kind = JobKind.Install,
            BundleId = "com.a",
            FilePath = @"C:\tmp\a.ipa",
            PackageKey = @"C:\tmp\a.ipa",
            Status = QueueStatus.Installing,
        };
        var job = new JobViewModel(item, "iPhone 4") { InstallPercent = 50 };
        job.Status = QueueStatus.Installing;

        Assert.Contains($"[{Strings.Queue_Status_Installing} 50%]", job.Display, StringComparison.Ordinal);
        Assert.Contains($"[{Strings.Queue_Status_Installing} 50%]", job.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void 队列行_传输阶段用传输百分比()
    {
        var item = new QueueItem
        {
            Udid = "udid",
            Kind = JobKind.Install,
            BundleId = "com.a",
            FilePath = @"C:\tmp\a.ipa",
            PackageKey = @"C:\tmp\a.ipa",
            Status = QueueStatus.Installing,
        };
        var job = new JobViewModel(item, "iPhone 4") { Percent = 40 };
        job.Status = QueueStatus.Installing;

        Assert.Contains($"[{Strings.Queue_Status_Installing} 40%]", job.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void 队列行_完成时不带百分比()
    {
        var item = new QueueItem
        {
            Udid = "udid",
            Kind = JobKind.Install,
            BundleId = "com.a",
            FilePath = @"C:\tmp\a.ipa",
            PackageKey = @"C:\tmp\a.ipa",
            Status = QueueStatus.Done,
        };
        var job = new JobViewModel(item, "iPhone 4") { InstallPercent = 90, Percent = 100 };
        job.Status = QueueStatus.Done;

        Assert.Contains($"[{Strings.Queue_Status_Done}]", job.Display, StringComparison.Ordinal);
        Assert.DoesNotContain("%", job.Display, StringComparison.Ordinal);
    }
}
