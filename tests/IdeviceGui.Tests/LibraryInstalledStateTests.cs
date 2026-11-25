using IdeviceGui.ViewModels.Library;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// ✓ 列状态的聚合口径（2026-11-02 用户定：组行**复用普通行接口**，不另写一套统计；
/// **`◐` 废弃**——任一成员已装就打勾）。
/// </summary>
public sealed class LibraryInstalledStateTests
{
    [Fact]
    public void 没有成员已装_为空()
        => Assert.Equal(LibraryInstalledState.None, LibraryInstalledStates.Resolve(0, 3));

    [Fact]
    public void 任一成员已装_打勾_不再区分部分()
        => Assert.Equal(LibraryInstalledState.Yes, LibraryInstalledStates.Resolve(1, 3));

    [Fact]
    public void 全部已装_打勾()
        => Assert.Equal(LibraryInstalledState.Yes, LibraryInstalledStates.Resolve(3, 3));

    [Fact]
    public void 成员数为零_为空且不除零()
    {
        Assert.Equal(LibraryInstalledState.None, LibraryInstalledStates.Resolve(0, 0));
        Assert.Equal(LibraryInstalledState.None, LibraryInstalledStates.Resolve(2, 0));
    }
    [Fact]
    public void 同版本优先于其他版本_组行绿勾()
        => Assert.Equal(LibraryInstalledState.Yes, LibraryInstalledStates.Resolve(1, 3, 4));

    [Fact]
    public void 没有同版本但有其他版本_组行蓝底()
        => Assert.Equal(LibraryInstalledState.OtherVersion, LibraryInstalledStates.Resolve(0, 2, 4));

    [Fact]
    public void 全部为空_组行白()
        => Assert.Equal(LibraryInstalledState.None, LibraryInstalledStates.Resolve(0, 0, 4));

}
