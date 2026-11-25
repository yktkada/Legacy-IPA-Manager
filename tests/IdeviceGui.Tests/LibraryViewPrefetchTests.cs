using IdeviceGui.Services.Data;
using IdeviceGui.ViewModels.Library;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>C-全量预热（2026-11-04）：索引与现算排序必须逐行一致；数据变化即失效。</summary>
public sealed class LibraryViewPrefetchTests
{
    private static PackageRow Row(long id, string version, long appSize) => new()
    {
        Id = id,
        FilePath = $"C:\\fake\\{id}.ipa",
        FileName = $"{id}.ipa",
        DisplayName = $"Row{id}",
        BundleId = $"com.test.{id}",
        Version = version,
        Build = version,
        MinOs = version,
        DeviceFamily = id % 2 == 0 ? "iPhone" : "iPad",
        Arch = id % 3 == 0 ? "armv7,arm64" : "armv7",
        AppSize = appSize,
        FileSize = appSize / 2,
        ParseState = ParseStates.Ok,
        ExistsFlag = true,
    };

    private static PackageRow[] Rows() => new[]
    {
        Row(1, "1.0", 300),
        Row(2, "1.10", 100),
        Row(3, "2.0", 200),
        Row(4, "bad", 500),
        Row(5, "", 400),
        Row(6, "3.0.1", 600),
    };

    [Fact]
    public void 预热索引_与现算排序完全一致()
    {
        foreach (var field in LibraryView.PrefetchFields)
        {
            foreach (var descending in new[] { false, true })
            {
                var view = new LibraryView();
                view.ReplaceAll(Rows());
                var (snapshot, version) = view.SnapshotForPrefetch();
                var indexes = LibraryView.BuildPrefetchIndexes(snapshot, CancellationToken.None);
                view.ApplyPrefetch(indexes, version);
                Assert.True(view.HasValidPrefetch);

                view.Sort = new LibrarySort(field, descending);
                var actual = view.Build().Select(r => r.Id).ToList();

                var reference = new LibraryView();
                reference.ReplaceAll(Rows());
                reference.Sort = new LibrarySort(field, descending);
                var expected = reference.Build().Select(r => r.Id).ToList();

                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public void 数据变化_预热索引失效()
    {
        var view = new LibraryView();
        view.ReplaceAll(Rows());
        var (snapshot, version) = view.SnapshotForPrefetch();
        view.ApplyPrefetch(LibraryView.BuildPrefetchIndexes(snapshot, CancellationToken.None), version);
        Assert.True(view.HasValidPrefetch);

        view.ReplaceAll(Rows());
        Assert.False(view.HasValidPrefetch);
    }
}
