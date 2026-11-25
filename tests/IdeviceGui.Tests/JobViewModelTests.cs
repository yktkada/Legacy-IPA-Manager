using IdeviceGui.Resources;
using IdeviceGui.Services;
using IdeviceGui.ViewModels;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>队列行映射（审查报告空洞 #4）：文件名 / 显示文本 / 能否取消 / 能否重试。</summary>
public sealed class JobViewModelTests
{
    private static QueueItem NewItem(string? path = @"C:\tmp\Demo.ipa")
        => new()
        {
            Udid = "0000-test",
            Kind = JobKind.Install,
            BundleId = "com.test.demo",
            FilePath = path,
            PackageKey = path,
        };

    [Fact]
    public void 文件名与显示文本()
    {
        var job = new JobViewModel(NewItem(), "Fake iPhone");
        Assert.Equal("Demo.ipa", job.FileName);
        Assert.Contains("Demo.ipa", job.Display);
        Assert.Contains(Strings.Queue_Status_Pending, job.Display, StringComparison.Ordinal);

        job.Status = QueueStatus.Installing;
        job.Percent = 42;
        Assert.Contains(Strings.Queue_Status_Installing, job.Display);
        Assert.Contains("42%", job.Display);
    }

    [Fact]
    public void 卸载任务没有文件路径时用BundleId()
    {
        var item = new QueueItem
        {
            Udid = "0000-test",
            Kind = JobKind.Uninstall,
            BundleId = "com.test.demo",
        };
        var job = new JobViewModel(item, "Fake iPhone");
        Assert.Equal("com.test.demo", job.FileName);
    }

    [Theory]
    [InlineData(QueueStatus.Pending, true, false)]
    [InlineData(QueueStatus.VerifyDevice, true, false)]
    [InlineData(QueueStatus.Precheck, true, false)]
    [InlineData(QueueStatus.Cleaning, true, false)]
    [InlineData(QueueStatus.SpaceCheck, true, false)]
    [InlineData(QueueStatus.Installing, true, false)]
    [InlineData(QueueStatus.Uninstalling, true, false)]
    [InlineData(QueueStatus.Readback, true, false)]
    [InlineData(QueueStatus.Paused, false, true)]
    [InlineData(QueueStatus.NeedsConfirm, false, true)]
    [InlineData(QueueStatus.Failed, false, true)]
    [InlineData(QueueStatus.Done, false, false)]
    [InlineData(QueueStatus.Cancelled, false, false)]
    internal void 能否取消与重试(QueueStatus status, bool canCancel, bool canRetry)
    {
        var job = new JobViewModel(NewItem(), "Fake iPhone") { Status = status };
        Assert.Equal(canCancel, job.CanCancel);
        Assert.Equal(canRetry, job.CanRetry);
    }

    [Fact]
    public void 卸载任务的运行段不能取消_安装任务可以()
    {
        var uninstall = new QueueItem
        {
            Udid = "0000-test",
            Kind = JobKind.Uninstall,
            BundleId = "com.test.demo",
        };
        var job = new JobViewModel(uninstall, "Fake iPhone") { Status = QueueStatus.Uninstalling };
        Assert.False(job.CanCancel);

        job.Status = QueueStatus.Pending;
        Assert.True(job.CanCancel);
    }
}
