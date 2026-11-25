using IdeviceGui.Resources;
using IdeviceGui.Services.Data;
using IdeviceGui.ViewModels.Library;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 高级搜索（2026-11-01 用户定）：**全部列**都能搜（含隐藏列）+ 匹配方式（包含/前缀/精确）+ 范围持久化；
/// 并钉住"计数与组展开/收起无关"的修复（之前展开 BundleId 会让总包数变化）。
/// </summary>
public sealed class LibrarySearchTests
{
    private static PackageRow Row(
        long id,
        string name,
        string bundleId,
        string version = "1.0",
        long appSize = 100,
        long fileSize = 50,
        string arch = "armv7",
        string minOs = "4.0",
        string family = "iPhone",
        string parseState = ParseStates.Ok,
        string origin = "decrypted") => new()
    {
        Id = id,
        FilePath = $"C:\\fake\\{name}.ipa",
        FileName = name + ".ipa",
        DisplayName = name,
        BundleId = bundleId,
        Version = version,
        Build = version,
        MinOs = minOs,
        DeviceFamily = family,
        Arch = arch,
        AppSize = appSize,
        FileSize = fileSize,
        ParseState = parseState,
        OriginKind = origin,
        ExistsFlag = true,
    };

    private static LibraryView NewView(params PackageRow[] rows)
    {
        var view = new LibraryView();
        view.ReplaceAll(rows);
        return view;
    }

    private static SearchScope Only(SearchField field)
    {
        var scope = SearchScope.Parse(SearchScope.EmptyToken);
        scope.Set(field, true);
        return scope;
    }

    [Fact]
    public void 默认范围是名称_BundleId_文件名()
    {
        var view = NewView(Row(1, "Alpha", "com.x.beta"));

        view.SearchText = "Alpha";
        Assert.Single(view.Build());

        view.SearchText = "beta";
        Assert.Single(view.Build());

        // 默认不搜架构：勾选前搜 "armv7" 无命中
        view.SearchText = "armv7";
        Assert.Empty(view.Build());
    }

    [Theory]
    [InlineData(SearchField.Name, "Alpha")]
    [InlineData(SearchField.BundleId, "com.x.alpha")]
    [InlineData(SearchField.FileName, "Alpha.ipa")]
    [InlineData(SearchField.Version, "7.5.1")]
    [InlineData(SearchField.MinOs, "6.0.1")]
    [InlineData(SearchField.DeviceFamily, "iPad")]
    [InlineData(SearchField.Arch, "armv7s")]
    [InlineData(SearchField.FilePath, "C:\\fake\\Alpha.ipa")]
    [InlineData(SearchField.AppSize, "100 B")]
    [InlineData(SearchField.FileSize, "50 B")]
    internal void 每个列字段都能单独搜到(SearchField field, string query)
    {
        var row = Row(1, "Alpha", "com.x.alpha", version: "7.5.1", appSize: 100, fileSize: 50,
            arch: "armv7s", minOs: "6.0.1", family: "iPad");
        var view = NewView(row);
        view.Scope = Only(field);

        view.SearchText = query;
        Assert.Single(view.Build());
    }

    [Fact]
    public void 来源与状态列也能按当前语言显示名搜到()
    {
        var row = Row(1, "Alpha", "com.x.alpha", origin: "decrypted");
        var view = NewView(row);

        view.Scope = Only(SearchField.Origin);
        view.SearchText = Strings.Filter_Origin_Decrypted;
        Assert.Single(view.Build());

        view.Scope = Only(SearchField.State);
        view.SearchText = Strings.Filter_State_Normal;
        Assert.Single(view.Build());
    }

    [Fact]
    public void 空范围不按字段过滤()
    {
        var view = NewView(Row(1, "Alpha", "com.x.alpha"), Row(2, "Beta", "com.x.beta"));
        view.Scope = SearchScope.Parse(SearchScope.EmptyToken);

        view.SearchText = "zzz";
        Assert.Equal(2, view.Build().Count);
    }

    [Fact]
    public void 匹配方式_包含_前缀_精确()
    {
        var view = NewView(Row(1, "MemoryInfo", "com.recession.memoryinfo"));
        var scope = Only(SearchField.Name);
        view.Scope = scope;

        view.MatchMode = SearchMatchMode.Contains;
        view.SearchText = "memory";
        Assert.Single(view.Build());

        view.MatchMode = SearchMatchMode.Prefix;
        view.SearchText = "memory";
        Assert.Single(view.Build());          // 名称以 MemoryInfo 开头
        view.SearchText = "info";
        Assert.Empty(view.Build());           // 前缀：info 不在开头

        view.MatchMode = SearchMatchMode.Exact;
        view.SearchText = "MemoryInfo";
        Assert.Single(view.Build());
        view.SearchText = "memory";
        Assert.Empty(view.Build());           // 精确：不分大小写但必须整串相等
    }

    [Fact]
    public void 范围与匹配方式持久化往返()
    {
        var scope = SearchScope.Parse(SearchScope.EmptyToken);
        scope.Set(SearchField.Arch, true);
        scope.Set(SearchField.FilePath, true);

        var text = scope.Format();
        Assert.Equal("arch,filePath", text);

        var parsed = SearchScope.Parse(text);
        Assert.True(parsed.IsArch);
        Assert.True(parsed.IsFilePath);
        Assert.False(parsed.IsName);

        // 坏值/未配置 → 默认三项；none → 空范围
        var fallback = SearchScope.Parse("garbage,unknown");
        Assert.True(fallback.IsName && fallback.IsBundleId && fallback.IsFileName);

        Assert.True(SearchScope.Parse(null).IsName);
        Assert.True(SearchScope.Parse(SearchScope.EmptyToken).IsEmpty);

        Assert.Equal("contains", LibraryPrefs.FormatSearchMode(SearchMatchMode.Contains));
        Assert.Equal("prefix", LibraryPrefs.FormatSearchMode(SearchMatchMode.Prefix));
        Assert.Equal("exact", LibraryPrefs.FormatSearchMode(SearchMatchMode.Exact));
        Assert.Equal(SearchMatchMode.Exact, LibraryPrefs.ParseSearchMode("EXACT"));
        Assert.Equal(SearchMatchMode.Contains, LibraryPrefs.ParseSearchMode("nonsense"));
    }

    // ── 计数修复：与组展开/收起无关 ──────────────────────────────────────────

    [Fact]
    public void 计数_展开收起BundleId不改变文件数与组数()
    {
        var view = NewView(
            Row(1, "Gyroblox", "com.gyro", version: "1.0"),
            Row(2, "Gyroblox", "com.gyro", version: "1.1"),
            Row(3, "Line Art", "com.line"));

        view.MergeSameBundle = true;

        var (files, groups, missing) = view.CountFiltered();
        Assert.Equal(3, files);
        Assert.Equal(1, groups);
        Assert.Equal(0, missing);

        // 收起再展开：文件数/组数必须不变（之前按可见行数算，收起后成员行消失 → 包数变小）
        var groupRow = view.Build().First(r => r.IsGroup);
        view.ToggleGroup(groupRow.GroupBundleId);
        Assert.Equal((3, 1, 0), view.CountFiltered());

        view.ToggleGroup(groupRow.GroupBundleId);
        Assert.Equal((3, 1, 0), view.CountFiltered());
    }

    [Fact]
    public void 文件缺失_排到最后且不参与合并_计数单列()
    {
        var missing = Row(9, "Zzz GONE", "com.gone");
        missing.ExistsFlag = false;

        var view = NewView(
            Row(1, "Alpha", "com.x.alpha", appSize: 999),
            missing,
            Row(2, "Beta", "com.x.beta"));
        view.MergeSameBundle = true;

        var rows = view.Build();
        Assert.Equal(3, rows.Count);
        Assert.Equal("Zzz GONE", rows[^1].Name);          // 缺失行永远在最后（即便体积/名称排序不同）
        Assert.True(rows[^1].IsMissing);
        Assert.Equal(Strings.Filter_State_Missing, rows[^1].StateText);

        var (files, groups, missingCount) = view.CountFiltered();
        Assert.Equal(2, files);
        Assert.Equal(0, groups);
        Assert.Equal(1, missingCount);
    }

    [Fact]
    public void 兼容过滤_只显示设备族与最低iOS都满足的包_计数同步()
    {
        var view = NewView(
            Row(1, "Old", "com.x.old", minOs: "4.0", family: "Universal"),
            Row(2, "New", "com.x.new", minOs: "9.0", family: "Universal"),
            Row(3, "iPadOnly", "com.x.ipad", minOs: "4.0", family: "iPad"));
        view.DeviceProductType = "iPhone3,1";
        view.DeviceVersion = "4.1";
        view.CompatFilter = CompatFilterMode.Installable;

        var rows = view.Build();
        Assert.Single(rows);
        Assert.Equal("Old", rows[0].Name);

        var (files, groups, missing) = view.CountFiltered();
        Assert.Equal(1, files);
        Assert.Equal(0, groups);
        Assert.Equal(0, missing);
    }

    [Fact]
    public void 兼容过滤关了_全部显示()
    {
        var view = NewView(
            Row(1, "Old", "com.x.old", minOs: "4.0"),
            Row(2, "New", "com.x.new", minOs: "9.0"));
        view.DeviceProductType = "iPhone3,1";
        view.DeviceVersion = "4.1";

        Assert.Equal(2, view.Build().Count);
    }

        [Fact]
    public void 隐藏不存在的文件_列表与计数都不含缺失行()
    {
        var missing = Row(9, "Gone", "com.gone");
        missing.ExistsFlag = false;
        var view = NewView(Row(1, "Alpha", "com.x.alpha"), missing);

        view.ShowMissingFiles = false;
        var rows = view.Build();
        Assert.Single(rows);
        Assert.Equal("Alpha", rows[0].Name);
        var (files, groups, missingCount) = view.CountFiltered();
        Assert.Equal(1, files);
        Assert.Equal(0, missingCount);

        view.ShowMissingFiles = true;
        Assert.Equal(2, view.Build().Count);
        Assert.Equal(1, view.CountFiltered().Missing);
    }

        [Fact]
    public void 缺失行的状态文案是文件缺失()
    {
        var package = Row(1, "Alpha", "com.x.alpha");
        package.ExistsFlag = false;
        var view = NewView(package);
        var row = Assert.Single(view.Build());
        Assert.True(row.IsMissing);
        Assert.Equal(Strings.Filter_State_Missing, row.StateText);
    }

        [Fact]
    public void 计数_搜索时只统计命中的文件()
    {
        var view = NewView(
            Row(1, "Gyroblox", "com.gyro"),
            Row(2, "Gyroblox", "com.gyro"),
            Row(3, "Line Art", "com.line"));
        view.MergeSameBundle = true;

        view.Scope = Only(SearchField.Name);
        view.SearchText = "Line";

        var (files, groups, missing) = view.CountFiltered();
        Assert.Equal(1, files);
        Assert.Equal(0, groups);
        Assert.Equal(0, missing);
    }
}
