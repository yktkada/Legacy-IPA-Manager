using IdeviceGui.Services.Data;
using IdeviceGui.ViewModels.Library;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>2026-11-04 第二批：C3 计数随 Build 产出、C4 绿色集合版本号。</summary>
public sealed class LibraryCountsAndMarksTests
{
    private static PackageRow Row(long id, string name, string bundleId) => new()
    {
        Id = id,
        FilePath = $"C:\\fake\\{name}.ipa",
        FileName = name + ".ipa",
        DisplayName = name,
        BundleId = bundleId,
        Version = "1.0",
        Build = "1.0",
        MinOs = "4.0",
        DeviceFamily = "iPhone",
        Arch = "armv7",
        AppSize = 100,
        FileSize = 50,
        ParseState = ParseStates.Ok,
        ExistsFlag = true,
    };

    private static LibraryView Build()
    {
        var view = new LibraryView();
        var missing = Row(3, "Gone", "com.gone");
        missing.ExistsFlag = false;
        view.ReplaceAll(new[] { Row(1, "A", "com.x"), Row(2, "B", "com.x"), missing });
        return view;
    }

    [Fact]
    public void LastCounts_与CountFiltered一致_合并开()
    {
        var view = Build();
        view.Build();

        Assert.Equal(view.CountFiltered(), view.LastCounts);
        Assert.Equal((2, 1, 1), view.LastCounts);
    }

    [Fact]
    public void LastCounts_合并关_组数为0()
    {
        var view = Build();
        view.MergeSameBundle = false;
        view.Build();

        Assert.Equal(view.CountFiltered(), view.LastCounts);
        Assert.Equal(0, view.LastCounts.Groups);
    }

    [Fact]
    public void LastCounts_隐藏缺失_missing为0()
    {
        var view = Build();
        view.ShowMissingFiles = false;
        view.Build();

        Assert.Equal(view.CountFiltered(), view.LastCounts);
        Assert.Equal(0, view.LastCounts.Missing);
    }

    [Fact]
    public void 绿色集合版本_Add_Clear_都递增()
    {
        var marks = new DeviceSuccessMarks();
        var version = marks.Version;

        marks.Add("udid", "com.x", "1.0");
        Assert.True(marks.Version > version);
        version = marks.Version;

        marks.Clear("udid", "com.x");
        Assert.True(marks.Version > version);
        version = marks.Version;

        marks.Add("udid", "com.y", "1.0");
        marks.Clear("udid");
        Assert.True(marks.Version > version);
    }
}
