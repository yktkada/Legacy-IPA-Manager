using IdeviceGui.Services;
using IdeviceGui.ViewModels.Library;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 资料库行标记色（2026-11-01 用户定：黄 &gt; 红 &gt; 无；2026-11-02 加**绿 = 队列安装成功**、
/// 组行改为成员聚合）。
/// </summary>
public sealed class LibraryMarkerTests
{
    [Theory]
    [InlineData(QueueStatus.Pending)]
    [InlineData(QueueStatus.Installing)]
    [InlineData(QueueStatus.Paused)]
    [InlineData(QueueStatus.NeedsConfirm)]
    [InlineData(QueueStatus.Readback)]
    internal void 在队列里的状态_算黄色(QueueStatus status)
    {
        var (queued, failed) = LibraryMarkers.Classify(status);
        Assert.True(queued);
        Assert.False(failed);
    }

    [Fact]
    public void 失败只算红色()
    {
        var (queued, failed) = LibraryMarkers.Classify(QueueStatus.Failed);
        Assert.False(queued);
        Assert.True(failed);
    }

    [Theory]
    [InlineData(QueueStatus.Done)]
    [InlineData(QueueStatus.Cancelled)]
    internal void 完成或已取消不染红黄(QueueStatus status)
    {
        var (queued, failed) = LibraryMarkers.Classify(status);
        Assert.False(queued);
        Assert.False(failed);
    }

    [Fact]
    public void 优先级_黄大于红大于绿大于无()
    {
        Assert.Equal(LibraryMarker.Queued, LibraryMarkers.Resolve(queued: true, failed: true, installed: true));
        Assert.Equal(LibraryMarker.Queued, LibraryMarkers.Resolve(queued: true, failed: false, installed: false));
        Assert.Equal(LibraryMarker.Failed, LibraryMarkers.Resolve(queued: false, failed: true, installed: true));
        Assert.Equal(LibraryMarker.Failed, LibraryMarkers.Resolve(queued: false, failed: true, installed: false));
        Assert.Equal(LibraryMarker.Installed, LibraryMarkers.Resolve(queued: false, failed: false, installed: true));
        Assert.Equal(LibraryMarker.None, LibraryMarkers.Resolve(queued: false, failed: false, installed: false));
    }

    // ── 组行 = 成员聚合（2026-11-02 用户定：任一成员有色 → 组行同色） ────────────

    [Fact]
    public void 组行_任一成员在队列则黄()
        => Assert.Equal(LibraryMarker.Queued, LibraryMarkers.ResolveGroup(new[]
        {
            LibraryMarker.None, LibraryMarker.Failed, LibraryMarker.Installed, LibraryMarker.Queued,
        }));

    [Fact]
    public void 组行_只有成员失败则红()
        => Assert.Equal(LibraryMarker.Failed, LibraryMarkers.ResolveGroup(new[]
        {
            LibraryMarker.None, LibraryMarker.Failed, LibraryMarker.Installed,
        }));

    [Fact]
    public void 组行_只有成员安装成功则绿()
        => Assert.Equal(LibraryMarker.Installed, LibraryMarkers.ResolveGroup(new[]
        {
            LibraryMarker.None, LibraryMarker.Installed,
        }));

    [Fact]
    public void 组行_没有成员染色则为无()
    {
        Assert.Equal(LibraryMarker.None, LibraryMarkers.ResolveGroup(Array.Empty<LibraryMarker>()));
        Assert.Equal(LibraryMarker.None, LibraryMarkers.ResolveGroup(new[] { LibraryMarker.None }));
    }
}
