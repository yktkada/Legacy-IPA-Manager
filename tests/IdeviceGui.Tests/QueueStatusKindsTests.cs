using IdeviceGui.Services;
using IdeviceGui.ViewModels;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// T1（2026-11-03）：队列状态分类唯一权威。
/// 覆盖全部 14 个状态 × 5 个谓词（在飞 / 已结束 / 可取消（安装）/ 可取消（卸载）/ 黄标记 / 可重试），
/// 以后新增状态时这里会因枚举数量断言失败而被迫表态。
/// </summary>
public sealed class QueueStatusKindsTests
{
    public static IEnumerable<object[]> AllStatuses => new[]
    {
        // status, inFlight, finished, cancellable(install), cancellable(uninstall), queued, retryable
        new object[] { QueueStatus.Pending, false, false, true, true, true, false },
        new object[] { QueueStatus.VerifyDevice, true, false, true, true, true, false },
        new object[] { QueueStatus.Precheck, true, false, true, true, true, false },
        new object[] { QueueStatus.Cleaning, true, false, true, true, true, false },
        new object[] { QueueStatus.SpaceCheck, true, false, true, true, true, false },
        new object[] { QueueStatus.Installing, true, false, true, false, true, false },
        new object[] { QueueStatus.Uninstalling, true, false, true, false, true, false },
        new object[] { QueueStatus.Readback, true, false, true, false, true, false },
        new object[] { QueueStatus.Paused, false, false, false, false, true, true },
        new object[] { QueueStatus.Failed, false, true, false, false, false, true },
        new object[] { QueueStatus.Cancelled, false, true, false, false, false, false },
        new object[] { QueueStatus.NeedsConfirm, false, false, false, false, true, true },
        new object[] { QueueStatus.Done, false, true, false, false, false, false },
        new object[] { QueueStatus.Suspended, false, false, true, true, true, false },   // 挂起可取消，不触发中断恢复
    };

    [Fact]
    public void 状态枚举恰好14个_新增状态必须在这里表态()
    {
        Assert.Equal(14, Enum.GetValues<QueueStatus>().Length);
        Assert.Equal(14, AllStatuses.Count());
    }

    [Theory]
    [MemberData(nameof(AllStatuses))]
    internal void 全状态分类表(
        QueueStatus status,
        bool inFlight,
        bool finished,
        bool cancellableInstall,
        bool cancellableUninstall,
        bool queued,
        bool retryable)
    {
        Assert.Equal(inFlight, status.IsInFlight());
        Assert.Equal(finished, status.IsFinished());
        Assert.Equal(cancellableInstall, status.IsCancellable(JobKind.Install));
        Assert.Equal(cancellableUninstall, status.IsCancellable(JobKind.Uninstall));
        Assert.Equal(queued, status.IsQueuedMarker());
        Assert.Equal(retryable, status.IsRetryable());
    }

    [Fact]
    public void 在飞与已结束互斥_且都不包含Paused和NeedsConfirm()
    {
        foreach (var status in Enum.GetValues<QueueStatus>())
        {
            Assert.False(status.IsInFlight() && status.IsFinished(), $"{status} 同时在飞且已结束");
        }

        Assert.False(QueueStatus.Paused.IsInFlight());
        Assert.False(QueueStatus.Paused.IsFinished());
        Assert.False(QueueStatus.NeedsConfirm.IsInFlight());
        Assert.False(QueueStatus.NeedsConfirm.IsFinished());
    }

    [Fact]
    public void 卸载任务的运行段不可取消_安装任务可以()
    {
        // 旧手抄列表（只看状态、不看 Kind）对卸载任务会误显示"可取消"；
        // QueueService.Cancel 对卸载任务的 Uninstalling/Readback 返回 NotCancellable。
        Assert.False(QueueStatus.Uninstalling.IsCancellable(JobKind.Uninstall));
        Assert.False(QueueStatus.Readback.IsCancellable(JobKind.Uninstall));
        Assert.True(QueueStatus.Uninstalling.IsCancellable(JobKind.Install));
        Assert.True(QueueStatus.Readback.IsCancellable(JobKind.Install));
    }
}
