using IdeviceGui.Services;
using IdeviceGui.ViewModels.Library;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 行状态派生属性（2026-11-02 用户定）：XAML 的 CellStyle 里**直接绑 enum 的 DataTrigger 实测不触发**
/// （踩坑 C35），所以颜色改用这些 bool 派生属性；另外 ✓ 的绿底也靠 <see cref="LibraryRow.IsInstalledTicked"/>。
/// </summary>
public sealed class LibraryRowStateTests
{
    private static LibraryRow File(string bundleId = "com.a") => new()
    {
        Id = 1,
        FilePath = @"C:\fake\a.ipa",
        FileName = "a.ipa",
        BundleId = bundleId,
    };

    private static LibraryRow Group() => new()
    {
        Id = -1,
        FilePath = "",
        FileName = "",
        IsGroup = true,
        GroupBundleId = "com.a",
    };

    [Fact]
    public void 文件行_不做组行染色()
    {
        var row = File();
        row.Marker = LibraryMarker.Queued;

        Assert.False(row.IsGroupQueued);
        Assert.False(row.IsGroupFailed);
        Assert.False(row.IsGroupInstalledMarked);
    }

    [Fact]
    public void 组行_按Marker给出对应bool()
    {
        var row = Group();
        row.Marker = LibraryMarker.Queued;
        Assert.True(row.IsGroupQueued);
        Assert.False(row.IsGroupFailed);
        Assert.False(row.IsGroupInstalledMarked);

        row.Marker = LibraryMarker.Failed;
        Assert.False(row.IsGroupQueued);
        Assert.True(row.IsGroupFailed);

        row.Marker = LibraryMarker.Installed;
        Assert.True(row.IsGroupInstalledMarked);

        row.Marker = LibraryMarker.None;
        Assert.False(row.IsGroupQueued || row.IsGroupFailed || row.IsGroupInstalledMarked);
    }

    [Fact]
    public void 打勾_IsInstalledTicked为真_取消勾为假()
    {
        var row = File();
        Assert.False(row.IsInstalledTicked);

        row.InstalledState = LibraryInstalledState.Yes;
        Assert.True(row.IsInstalledTicked);

        row.InstalledState = LibraryInstalledState.None;
        Assert.False(row.IsInstalledTicked);
    }

    [Fact]
    public void 组行打勾同样生效_绿底作用于组行勾列()
    {
        var row = Group();
        row.InstalledState = LibraryInstalledState.Yes;
        Assert.True(row.IsInstalledTicked);
    }

    [Fact]
    public void 状态变化要发通知_否则CellStyle不刷新()
    {
        var row = Group();
        var names = new List<string?>();
        row.PropertyChanged += (_, e) => names.Add(e.PropertyName);

        row.Marker = LibraryMarker.Queued;
        row.InstalledState = LibraryInstalledState.Yes;

        Assert.Contains(nameof(LibraryRow.IsGroupQueued), names);
        Assert.Contains(nameof(LibraryRow.IsGroupFailed), names);
        Assert.Contains(nameof(LibraryRow.IsGroupInstalledMarked), names);
        Assert.Contains(nameof(LibraryRow.IsInstalledTicked), names);
    }

    [Theory]
    [InlineData(QueueStatus.Done, true)]
    [InlineData(QueueStatus.Failed, true)]
    [InlineData(QueueStatus.Cancelled, true)]
    [InlineData(QueueStatus.Pending, false)]
    [InlineData(QueueStatus.Installing, false)]
    [InlineData(QueueStatus.Uninstalling, false)]
    [InlineData(QueueStatus.Readback, false)]
    [InlineData(QueueStatus.Paused, false)]
    [InlineData(QueueStatus.NeedsConfirm, false)]
    [InlineData(QueueStatus.VerifyDevice, false)]
    [InlineData(QueueStatus.Precheck, false)]
    [InlineData(QueueStatus.Cleaning, false)]
    [InlineData(QueueStatus.SpaceCheck, false)]
    internal void 已结束判定_覆盖全部状态_与刷新清空一致(QueueStatus status, bool finished)
    {
        // 界面「清空已结束」与服务端 RemoveFinished 共用这一个判定（2026-11-02 用户定：刷新=合并清空）
        Assert.Equal(finished, status.IsFinished());
        Assert.Equal(finished, IdeviceGui.ViewModels.DeviceQueueViewModel.IsFinishedStatus(status));
    }
}
