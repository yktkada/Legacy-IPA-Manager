using IdeviceGui.Services.Data;
using IdeviceGui.ViewModels.Library;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>B1（2026-11-04）：三段缓存 + 展开/收起快路径的回归测试。</summary>
public sealed class LibraryViewCacheTests
{
    private static PackageRow Row(long id, string name, string bundleId, string version = "1.0", long appSize = 100) => new()
    {
        Id = id,
        FilePath = $"C:\\fake\\{name}.ipa",
        FileName = name + ".ipa",
        DisplayName = name,
        BundleId = bundleId,
        Version = version,
        Build = version,
        MinOs = "4.0",
        DeviceFamily = "iPhone",
        Arch = "armv7",
        AppSize = appSize,
        FileSize = 50,
        ParseState = ParseStates.Ok,
        ExistsFlag = true,
    };

    private static List<string> Seq(IEnumerable<LibraryRow> rows) => rows
        .Select(r => r.IsGroup
            ? $"G:{r.GroupBundleId}:{r.IsGroupEnd}"
            : $"F:{r.Id}:{r.IsGroupMember}:{r.IsGroupEnd}")
        .ToList();

    [Fact]
    public void 连续两次Build_行对象复用()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[]
        {
            Row(1, "Gyro", "com.gyro", "1.0"),
            Row(2, "Gyro", "com.gyro", "1.1"),
            Row(3, "Solo", "com.solo"),
        });

        var first = view.Build();
        var second = view.Build();

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Same(first[i], second[i]);   // 缓存命中时不应重建行对象
        }
    }

    [Fact]
    public void 展开快路径_与全量重算的行序列一致()
    {
        var rows = new[]
        {
            Row(1, "Gyro", "com.gyro", "1.0"),
            Row(2, "Gyro", "com.gyro", "1.1"),
            Row(3, "Gyro", "com.gyro", "1.2"),
            Row(4, "Solo", "com.solo"),
        };
        var view = new LibraryView();
        view.ReplaceAll(rows);
        view.Build();   // 首轮自动收起组

        var expanded = view.ToggleGroupFast("com.gyro");
        Assert.Contains(expanded, r => r.IsGroupMember);

        var fresh = new LibraryView();
        fresh.ReplaceAll(rows);
        fresh.Build();
        fresh.ToggleGroup("com.gyro");
        Assert.Equal(Seq(fresh.Build()), Seq(expanded));

        var collapsed = view.ToggleGroupFast("com.gyro");
        fresh.ToggleGroup("com.gyro");
        Assert.Equal(Seq(fresh.Build()), Seq(collapsed));
        Assert.DoesNotContain(collapsed, r => r.IsGroupMember);
    }

    [Fact]
    public void 搜索变化_过滤结果重算()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[] { Row(1, "Alpha", "com.a"), Row(2, "Beta", "com.b") });
        Assert.Equal(2, view.Build().Count);

        view.SearchText = "com.a";
        var filtered = view.Build();
        Assert.Single(filtered);
        Assert.Equal(1, filtered[0].Id);

        view.SearchText = "";
        Assert.Equal(2, view.Build().Count);
    }

    [Fact]
    public void 排序变化_只重排不丢行()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[]
        {
            Row(1, "Zeta", "com.z", appSize: 300),
            Row(2, "Alpha", "com.a", appSize: 100),
        });

        view.Sort = new LibrarySort(LibrarySortField.AppSize, Descending: true);
        Assert.Equal(new long[] { 1, 2 }, view.Build().Select(r => r.Id));

        view.Sort = new LibrarySort(LibrarySortField.Name, Descending: false);
        Assert.Equal(new long[] { 2, 1 }, view.Build().Select(r => r.Id));
    }

    [Fact]
    public void 数据版本变化_组内容快照刷新()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[]
        {
            Row(1, "Gyro", "com.gyro", "1.0"),
            Row(2, "Gyro", "com.gyro", "2.0"),
        });
        var before = Assert.Single(view.Build(), r => r.IsGroup);
        Assert.Contains("1.0", before.VersionText);

        view.ReplaceAll(new[]
        {
            Row(1, "Gyro", "com.gyro", "1.0"),
            Row(2, "Gyro", "com.gyro", "3.0"),
        });
        var after = Assert.Single(view.Build(), r => r.IsGroup);
        Assert.Contains("3.0", after.VersionText);
    }

    [Fact]
    public void 显示缺失开关_只影响拼装()
    {
        var missing = Row(1, "Gone", "com.gone");
        missing.ExistsFlag = false;
        var view = new LibraryView();
        view.ReplaceAll(new[] { missing, Row(2, "Alive", "com.alive") });

        Assert.Contains(view.Build(), r => r.IsMissing);

        view.ShowMissingFiles = false;
        Assert.DoesNotContain(view.Build(), r => r.IsMissing);
    }

    [Fact]
    public void 合并开关关闭_成员缩进复位()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[] { Row(1, "Gyro", "com.gyro"), Row(2, "Gyro", "com.gyro") });
        Assert.Contains(view.Build(), r => r.IsGroup);

        view.MergeSameBundle = false;
        var rows = view.Build();
        Assert.DoesNotContain(rows, r => r.IsGroup);
        Assert.All(rows, r => Assert.False(r.IsGroupMember));
    }

    [Fact]
    public void 搜索后排序_仍按主序列顺序()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[]
        {
            Row(1, "Alpha", "com.a", appSize: 100),
            Row(2, "Beta", "com.b", appSize: 300),
            Row(3, "Gamma", "com.c", appSize: 200),
        });
        view.Sort = new LibrarySort(LibrarySortField.AppSize, Descending: true);
        Assert.Equal(new long[] { 2, 3, 1 }, view.Build().Select(r => r.Id));

        view.SearchText = "com.";   // 命中全部：搜索不应改变顺序
        Assert.Equal(new long[] { 2, 3, 1 }, view.Build().Select(r => r.Id));

        view.SearchText = "com.b";  // 命中单个
        Assert.Equal(new long[] { 2 }, view.Build().Select(r => r.Id));
    }

    [Fact]
    public void 搜索状态下换排序_按新主序列重排()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[]
        {
            Row(1, "Alpha", "com.a"),
            Row(2, "Beta", "com.b"),
        });
        view.SearchText = "com.";
        Assert.Equal(new long[] { 1, 2 }, view.Build().Select(r => r.Id));

        view.Sort = new LibrarySort(LibrarySortField.Name, Descending: true);
        Assert.Equal(new long[] { 2, 1 }, view.Build().Select(r => r.Id));
    }

    [Fact]
    public void 数据版本变化_主序列缓存失效()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[]
        {
            Row(1, "Alpha", "com.a", appSize: 100),
            Row(2, "Beta", "com.b", appSize: 300),
        });
        view.Sort = new LibrarySort(LibrarySortField.AppSize, Descending: true);
        Assert.Equal(new long[] { 2, 1 }, view.Build().Select(r => r.Id));

        view.ReplaceAll(new[]
        {
            Row(1, "Alpha", "com.a", appSize: 900),
            Row(2, "Beta", "com.b", appSize: 300),
        });
        Assert.Equal(new long[] { 1, 2 }, view.Build().Select(r => r.Id));
    }

    [Fact]
    public void 组行状态_每轮Build重算()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[] { Row(1, "Gyro", "com.gyro"), Row(2, "Gyro", "com.gyro") });
        Assert.Equal(LibraryMarker.None, Assert.Single(view.Build(), r => r.IsGroup).Marker);

        view.Rows[0].Marker = LibraryMarker.Failed;   // 缓存不能吃掉成员状态变化
        Assert.Equal(LibraryMarker.Failed, Assert.Single(view.Build(), r => r.IsGroup).Marker);
    }

    [Fact]
    public void 展开快路径_混排组行前后有单文件_邻居不变且IsGroupEnd准确()
    {
        var rows = new[]
        {
            Row(1, "SingleBefore", "com.before", appSize: 900),
            Row(2, "GroupA", "com.a", "1.0", appSize: 500),
            Row(3, "GroupA", "com.a", "2.0", appSize: 300),
            Row(4, "SingleMiddle", "com.mid", appSize: 400),
            Row(5, "GroupB", "com.b", "1.0", appSize: 200),
            Row(6, "GroupB", "com.b", "2.0", appSize: 100),
            Row(7, "SingleAfter", "com.after", appSize: 50),
        };

        var view = new LibraryView { Sort = new LibrarySort(LibrarySortField.AppSize, Descending: true) };
        view.ReplaceAll(rows);
        var initial = view.Build();
        Assert.Equal(new[] { "SingleBefore", "GroupA", "SingleMiddle", "GroupB", "SingleAfter" },
            initial.Select(r => r.Name).ToArray());

        var fresh = new LibraryView { Sort = new LibrarySort(LibrarySortField.AppSize, Descending: true) };
        fresh.ReplaceAll(rows);
        fresh.Build();

        // 展开中间靠前的 GroupA
        var expandedA = view.ToggleGroupFast("com.a");
        fresh.ToggleGroup("com.a");
        Assert.Equal(Seq(fresh.Build()), Seq(expandedA));
        Assert.False(expandedA[0].IsGroupMember);
        Assert.False(expandedA[0].IsGroupEnd);
        Assert.False(expandedA[1].IsGroupEnd); // GroupA header
        Assert.True(expandedA[2].IsGroupMember && !expandedA[2].IsGroupEnd);
        Assert.True(expandedA[3].IsGroupMember && expandedA[3].IsGroupEnd);
        Assert.False(expandedA[4].IsGroupMember); // SingleMiddle untouched
        Assert.False(expandedA[4].IsGroupEnd);
        Assert.True(expandedA[5].IsGroup && expandedA[5].IsGroupEnd); // GroupB still collapsed
        Assert.False(expandedA[6].IsGroupMember); // SingleAfter untouched

        // 接着展开后方的 GroupB（验证 _groupIndex 已更新）
        var expandedBoth = view.ToggleGroupFast("com.b");
        fresh.ToggleGroup("com.b");
        Assert.Equal(Seq(fresh.Build()), Seq(expandedBoth));

        // 收起前方的 GroupA（验证 GroupB 展开状态和后方单文件不受影响）
        var collapsedA = view.ToggleGroupFast("com.a");
        fresh.ToggleGroup("com.a");
        Assert.Equal(Seq(fresh.Build()), Seq(collapsedA));
        var groupA = collapsedA.Single(r => r.IsGroup && r.GroupBundleId == "com.a");
        Assert.True(groupA.IsCollapsed);
        Assert.True(groupA.IsGroupEnd);
        Assert.All(rows.Where(r => r.BundleId == "com.a").Select(r => view.Rows.Single(x => x.Id == r.Id)),
            m =>
            {
                Assert.False(m.IsGroupMember);
                Assert.False(m.IsGroupEnd);
            });
    }
}
