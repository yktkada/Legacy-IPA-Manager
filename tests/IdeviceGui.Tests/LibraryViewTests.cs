using IdeviceGui.Resources;
using IdeviceGui.Services.Data;
using IdeviceGui.ViewModels.Library;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// M4-b2：资料库网格的纯逻辑——列配置（`Grid.Columns`）与视图（搜索 / 合并 / 排序）。
/// 不碰 WPF：行模型与视图都在 <c>ViewModels\Library</c> 里，可直接 new 出来断言。
/// </summary>
public sealed class LibraryViewTests
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
        string? iconKey = null) => new()
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
        IconKey = iconKey,
        ExistsFlag = true,
    };

    [Fact]
    public void 合并组_架构显示组内所有架构的全集()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[]
        {
            Row(1, "Gyro", "com.gyro", version: "1.0", arch: "armv6"),
            Row(2, "Gyro", "com.gyro", version: "1.1", arch: "armv7s,arm64"),
            Row(3, "Gyro", "com.gyro", version: "1.2", arch: "armv7"),
        });

        var group = view.Build().First(r => r.IsGroup);
        Assert.Equal("v6, v7, v7s, arm64", group.ArchText);
    }

    // ── 搜索（名称 + BundleId 子串） ─────────────────────────────────────────

    [Fact]
    public void 搜索_名称或BundleId子串都命中_大小写不敏感()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[] { Row(1, "DOOM", "com.id.doom"), Row(2, "Line Art", "com.example.line") });

        view.SearchText = "doom";
        Assert.Single(view.Build());

        view.SearchText = "COM.EXAMPLE";
        var hit = Assert.Single(view.Build());
        Assert.Equal("Line Art", hit.Name);

        view.SearchText = "   ";
        Assert.Equal(2, view.Build().Count);
    }

    [Fact]
    public void 搜索_无匹配时为空()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[] { Row(1, "A", "com.a") });
        view.SearchText = "不存在的词";
        Assert.Empty(view.Build());
    }

    // ── 同 BundleId 合并（docs/06 §6.5） ─────────────────────────────────────

    [Fact]
    public void 合并_同BundleId多包_组行带个数与版本范围_且默认收起()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[]
        {
            Row(1, "Asphalt", "com.gameloft.asphalt", version: "1.0", appSize: 300),
            Row(2, "Asphalt", "com.gameloft.asphalt", version: "3.0", appSize: 100),
            Row(3, "Other", "com.other.app", appSize: 200),
        });

        var rows = view.Build();

        // 2026-10-31 用户拍板：勾选合并后**组默认收起** → 组行 + 单包 1 行 = 2（成员不铺开）
        Assert.Equal(2, rows.Count);
        var group = Assert.Single(rows, r => r.IsGroup);
        Assert.Equal(2, group.GroupCount);
        Assert.Equal("Asphalt", group.NameText);                 // 2026-11-01：个数拆到图标列
        Assert.Equal(Strings.Format(nameof(Strings.Library_Group_CountFormat), 2), group.GroupText);
        Assert.Equal("1.0–3.0", group.VersionText);
        Assert.True(group.IsCollapsed);
        Assert.DoesNotContain(rows, r => r.IsGroupMember);

        // 双击展开后才能看到成员，且都带缩进
        view.ToggleGroup("com.gameloft.asphalt");
        var expanded = view.Build();
        var members = expanded.Where(r => r.IsGroupMember).ToList();
        Assert.Equal(2, members.Count);
        Assert.All(members, m => Assert.StartsWith("    └ ", m.NameText));
    }

    [Fact]
    public void 合并_扫描中途新出现的组也默认收起()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[] { Row(1, "A", "com.a", appSize: 100) });
        var first = view.Build();                       // 单包不成组

        // 扫描后新增两条同 BundleId 的包 → 新组首次出现
        view.ReplaceAll(new[]
        {
            Row(1, "A", "com.a", appSize: 100),
            Row(2, "B", "com.b", version: "1.0", appSize: 200),
            Row(3, "B", "com.b", version: "2.0", appSize: 300),
        });
        var rows = view.Build();

        var group = Assert.Single(rows, r => r.IsGroup);
        Assert.True(group.IsCollapsed);                 // 复审 B6：新组也要默认收起
        Assert.DoesNotContain(rows, r => r.IsGroupMember);
    }

    [Fact]
    public void 合并_关掉再勾选_组重新收起()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[]
        {
            Row(1, "A", "com.a", version: "1.0"),
            Row(2, "A", "com.a", version: "2.0"),
        });

        Assert.True(Assert.Single(view.Build(), r => r.IsGroup).IsCollapsed);   // 默认收起

        view.ToggleGroup("com.a");                     // 手动展开
        Assert.False(Assert.Single(view.Build(), r => r.IsGroup).IsCollapsed);

        view.MergeSameBundle = false;                  // 关掉合并
        Assert.Equal(2, view.Build().Count);

        view.MergeSameBundle = true;                   // 重新勾选 → 组默认收起
        var rows = view.Build();
        var group = Assert.Single(rows, r => r.IsGroup);
        Assert.True(group.IsCollapsed);
        Assert.DoesNotContain(rows, r => r.IsGroupMember);
    }

    [Fact]
    public void 合并_组行的体积与最低iOS显示最小到最大_不相加()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[]
        {
            Row(1, "Asphalt", "com.gameloft.asphalt", version: "1.0", appSize: 300_000_000, fileSize: 60_000_000, minOs: "6.0"),
            Row(2, "Asphalt", "com.gameloft.asphalt", version: "3.0", appSize: 100_000_000, fileSize: 40_000_000, minOs: "9.0"),
        });

        var group = Assert.Single(view.Build(), r => r.IsGroup);

        // 体积：最小–最大（不是 400 MB 的合计）
        Assert.Equal("95.4–286.1 MB", group.AppSizeText);   // 2026-10-31：同单位只写一次单位，避免窄列被裁
        // 原始大小：最小–最大（不是 100 MB 的合计）
        Assert.Equal("38.1–57.2 MB", group.FileSizeText);
        Assert.DoesNotContain("95.4", group.FileSizeText);
        // 最低 iOS：最小–最大
        Assert.Equal("6.0–9.0", group.MinOsText);
        Assert.DoesNotContain("400", group.AppSizeText);
    }

    [Fact]
    public void 合并_组内体积与最低iOS相同_只显示单值()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[]
        {
            Row(1, "A", "com.a", version: "1.0", appSize: 100_000_000, minOs: "7.0"),
            Row(2, "A", "com.a", version: "1.1", appSize: 100_000_000, minOs: "7.0"),
        });

        var group = Assert.Single(view.Build(), r => r.IsGroup);
        Assert.Equal("95.4 MB", group.AppSizeText);

        Assert.Equal("7.0", group.MinOsText);
    }
    [Fact]
    public void 合并_GB体积范围_同单位只写一次单位()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[]
        {
            Row(1, "Big", "com.big", version: "1.0", appSize: 1_234_567_890),  // ≈1.15 GB（≥1 GiB 才用 GB 单位）
            Row(2, "Big", "com.big", version: "2.0", appSize: 2_060_000_000),   // ≈1.92 GB
        });

        var group = Assert.Single(view.Build(), r => r.IsGroup);

        Assert.Equal("1.15–1.92 GB", group.AppSizeText);
    }

    [Fact]
    public void 合并_收起组后只留组行()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[]
        {
            Row(1, "A", "com.a", version: "1.0"),
            Row(2, "A", "com.a", version: "2.0"),
        });

        view.ToggleGroup("com.a");
        var rows = view.Build();

        var group = Assert.Single(rows);
        Assert.True(group.IsGroup);
        Assert.True(group.IsCollapsed);
    }

    [Fact]
    public void 合并_关掉后一行一个文件()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[]
        {
            Row(1, "A", "com.a", version: "1.0"),
            Row(2, "A", "com.a", version: "2.0"),
        });

        view.MergeSameBundle = false;
        var rows = view.Build();

        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, r => r.IsGroup);
        Assert.DoesNotContain(rows, r => r.IsGroupMember);
    }

    [Fact]
    public void 合并_单包不成组_BundleId为空也不成组()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[]
        {
            Row(1, "Only", "com.only"),
            Row(2, "NoId-1", ""),
            Row(3, "NoId-2", ""),
        });

        var rows = view.Build();
        Assert.Equal(3, rows.Count);
        Assert.DoesNotContain(rows, r => r.IsGroup);
    }

    // ── 排序（默认解压后体积降序） ───────────────────────────────────────────

    [Fact]
    public void 排序_默认按解压后体积降序_同体积按名称升序()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[]
        {
            Row(1, "Small", "com.s", appSize: 10),
            Row(2, "Big", "com.b", appSize: 900),
            Row(3, "Mid-B", "com.mb", appSize: 500),
            Row(4, "Mid-A", "com.ma", appSize: 500),
        });

        var names = view.Build().Select(r => r.Name).ToArray();
        Assert.Equal(new[] { "Big", "Mid-A", "Mid-B", "Small" }, names);
    }

    [Fact]
    public void 排序_可改成名称升序()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[]
        {
            Row(1, "Zeta", "com.z", appSize: 10),
            Row(2, "Alpha", "com.a", appSize: 900),
        });

        view.Sort = new LibrarySort(LibrarySortField.Name, Descending: false);
        var names = view.Build().Select(r => r.Name).ToArray();
        Assert.Equal(new[] { "Alpha", "Zeta" }, names);
    }

    [Fact]
    public void 版本比较_按数字段而不是字符串()
    {
        Assert.True(LibraryView.CompareVersion("1.10", "1.9") > 0);
        Assert.True(LibraryView.CompareVersion("2.0", "10.0") < 0);
        Assert.True(LibraryView.CompareVersion("4.2.10", "4.2.9") > 0);   // 三位数 + 多位数补丁段
        Assert.Equal(0, LibraryView.CompareVersion("4.3", "4.3.0"));
    }

    // ── 审核 M4-b2 采纳项的回归（2026-10-31） ────────────────────────────────

    [Fact]
    public void 行被移动或改名后_路径跟着更新()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[] { Row(1, "A", "com.a") });
        var row = view.Rows.Single();
        Assert.Equal(@"C:\fake\A.ipa", row.FilePath);

        var moved = Row(1, "A", "com.a");
        moved.FilePath = @"C:\fake\new\A.ipa";      // 同 Id、路径变了（扫描识别到移动/改名）
        view.ReplaceAll(new[] { moved });

        Assert.Equal(@"C:\fake\new\A.ipa", row.FilePath);
        Assert.Same(row, view.Rows.Single());           // 仍然是复用同一个行对象
    }

    [Fact]
    public void 临时行_同路径入库后自动摘掉_不出现重复行()
    {
        var view = new LibraryView();
        view.AddTransient(new IpaInfo
        {
            Path = @"C:\fake\A.ipa",
            Name = "A",
            BundleId = "com.a",
            Version = "1.0",
            Size = 100,
        }, syntheticId: -1_000_000_000);

        Assert.Single(view.Build());                    // 只有临时行

        view.ReplaceAll(new[] { Row(1, "A", "com.a") });  // 扫描把同一文件入库
        var rows = view.Build();

        var single = Assert.Single(rows);
        Assert.False(single.IsGroup);                    // 不再伪造出「（2 个）」组
        Assert.False(single.IsTransient);
        Assert.Equal(1, single.Id);
    }

    [Fact]
    public void 合并开着再关掉_成员缩进必须复位()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[]
        {
            Row(1, "A", "com.a", version: "1.0"),
            Row(2, "A", "com.a", version: "2.0"),
        });

        var merged = view.Build();
        view.ToggleGroup("com.a");                              // 组默认收起 → 先展开，让成员被标 true
        Assert.Contains(view.Build(), r => r.IsGroupMember);

        view.MergeSameBundle = false;
        var files = view.Build();

        Assert.Equal(2, files.Count);
        Assert.All(files, r => Assert.False(r.IsGroupMember));
        Assert.All(files, r => Assert.DoesNotContain("└", r.NameText));   // 缩进不能残留
    }

    [Fact]
    public void 版本未知_入队版本给null而不是占位符()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[]
        {
            Row(1, "Unknown", "com.u", version: ""),
            Row(2, "Known", "com.k", version: "1.2.3"),
        });

        var unknown = view.Rows.Single(r => r.Id == 1);
        var known = view.Rows.Single(r => r.Id == 2);

        Assert.Equal("—", unknown.VersionText);            // 显示仍是占位
        Assert.Null(unknown.PackageVersionForQueue);        // 但入队/恢复判定拿 null（只按 BundleId）
        Assert.Equal("1.2.3", known.PackageVersionForQueue);
    }

    [Fact]
    public void Build列号未知时_回退Build作为入队版本()
    {
        var view = new LibraryView();
        var row = Row(1, "OnlyBuild", "com.b", version: "");
        row.Build = "42";
        view.ReplaceAll(new[] { row });

        var only = view.Rows.Single();
        Assert.Equal("42", only.VersionText);
        Assert.Equal("42", only.PackageVersionForQueue);
    }

    [Fact]
    public void 图标键变化后_旧任务不得赋值()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[] { Row(1, "A", "com.a", iconKey: "abc-64") });
        var row = view.Rows.Single();

        Assert.True(row.HasIconKey("abc-64"));     // 调度时的键仍然有效 → 可以赋值
        Assert.False(row.HasIconKey("def-64"));    // 键已变 → 旧图不能写回
        Assert.False(row.HasIconKey(null));
    }

    // ── 行复用（不整表重建） ────────────────────────────────────────────────

    [Fact]
    public void 刷新_按库主键复用行对象_只更新变化的字段()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[] { Row(1, "A", "com.a", appSize: 100) });
        var first = view.Rows.Single();

        view.ReplaceAll(new[] { Row(1, "A", "com.a", appSize: 200) });

        Assert.Same(first, view.Rows.Single());          // 还是同一个对象
        Assert.Equal(200, first.AppSize);                // 值原地更新
    }

    [Fact]
    public void 刷新_库里没有的行会被摘掉()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[] { Row(1, "A", "com.a"), Row(2, "B", "com.b") });
        view.ReplaceAll(new[] { Row(2, "B", "com.b") });

        var only = Assert.Single(view.Rows);
        Assert.Equal(2, only.Id);
    }

    [Fact]
    public void 合并收起_筛选全集仍保留_全选可见口径不会选中组内成员()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[] { Row(1, "A", "com.a"), Row(2, "A", "com.a") });

        var rows = view.Build();
        Assert.Single(rows, r => r.IsGroup);
        Assert.Equal(2, view.FilteredFiles.Count);   // 筛选全集含组内成员
        Assert.DoesNotContain(rows, r => !r.IsGroup);   // 默认收起 → 可见文件行为 0
    }

    [Fact]
    public void 分组过滤_只显示成员_空组为空而不是全部()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[] { Row(1, "A", "com.a"), Row(2, "B", "com.b") });

        view.GroupRowIds = new HashSet<long> { 1 };
        Assert.Single(view.Build(), r => r.Id == 1);

        view.GroupRowIds = new HashSet<long>();
        Assert.Empty(view.Build());
    }

    [Fact]
    public void Build_返回副本_调用方追加不污染内部缓存()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[] { Row(1, "A", "com.a") });
        var first = view.Build();
        first.Add(new LibraryRow { Id = 99, FileName = "Ghost.ipa", BundleId = "com.ghost" });

        var second = view.Build();
        Assert.DoesNotContain(second, r => r.Id == 99);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void 解析失败的行_状态列显示失败原因()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[] { Row(1, "Bad", "com.bad", parseState: ParseStates.Failed) });
        var row = view.Rows.Single();
        row.ParseError = "坏包";

        Assert.Equal(ParseStates.Failed, row.ParseState);
        Assert.StartsWith(Strings.Filter_State_ParseFailed, row.StateText);
    }

    [Fact]
    public void 体积未知_显示占位而不是0B()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[] { Row(1, "Old", "com.old", appSize: 0, fileSize: 2048) });
        var row = view.Rows.Single();

        Assert.Equal("—", row.AppSizeText);              // 旧行没深解析
        Assert.Equal("2.0 KB", row.FileSizeText);
    }

    [Fact]
    public void 兼容冲突预留位_未判定时不标红()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[] { Row(1, "A", "com.a") });
        var row = view.Rows.Single();

        Assert.Equal(CompatState.Unknown, row.Compat);
        Assert.False(row.IsCompatConflict);              // M5 之前状态列不红

        row.Compat = CompatState.Conflict;
        row.CompatConflict = new CompatConflict("需 iOS 9.0+");
        Assert.True(row.IsCompatConflict);
        Assert.Contains(Strings.Library_State_NotInstallable, row.StateText);
    }

    // ── 组行状态＝成员聚合（2026-11-02 用户定） ──────────────────────────────

    [Fact]
    public void 合并组_状态按成员聚合_组收起时也算进去()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[]
        {
            Row(1, "Gyro", "com.gyro", version: "1.0"),
            Row(2, "Gyro", "com.gyro", version: "1.1"),
        });

        // 成员行的状态由 RefreshLibraryMarkers 写入：一个已装、一个在队列
        view.Rows[0].InstalledState = LibraryInstalledState.Yes;
        view.Rows[1].Marker = LibraryMarker.Queued;

        var rows = view.Build();
        var group = Assert.Single(rows, r => r.IsGroup);

        Assert.True(group.IsCollapsed);                                  // 默认收起：成员不在 rows 里
        Assert.DoesNotContain(rows, r => r.IsGroupMember);
        Assert.Equal(LibraryMarker.Queued, group.Marker);                // 任一成员在队列 → 组行黄
        Assert.Equal(LibraryInstalledState.Yes, group.InstalledState);   // 任一成员已装 → 组行打勾（◐ 已废弃）
    }

    [Fact]
    public void 合并组_成员安装成功则组行绿_与普通行同一套聚合()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[]
        {
            Row(1, "Gyro", "com.gyro", version: "1.0"),
            Row(2, "Gyro", "com.gyro", version: "1.1"),
        });

        // 成员行的绿色来自队列安装成功记录（RefreshLibraryMarkers 写入 Marker = Installed）
        view.Rows[1].Marker = LibraryMarker.Installed;

        var group = Assert.Single(view.Build(), r => r.IsGroup);
        Assert.Equal(LibraryMarker.Installed, group.Marker);             // 任一成员绿 → 组行绿（图标列取这个值）
        Assert.Equal(LibraryInstalledState.None, group.InstalledState);  // 绿不是勾：勾只看设备回读
    }

    [Fact]
    public void 合并组_全部成员已装则打勾_成员失败则红()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[]
        {
            Row(1, "Gyro", "com.gyro", version: "1.0"),
            Row(2, "Gyro", "com.gyro", version: "1.1"),
        });

        foreach (var row in view.Rows) row.InstalledState = LibraryInstalledState.Yes;
        var allInstalled = Assert.Single(view.Build(), r => r.IsGroup);
        Assert.Equal(LibraryInstalledState.Yes, allInstalled.InstalledState);
        Assert.Equal(LibraryMarker.None, allInstalled.Marker);

        view.Rows[1].Marker = LibraryMarker.Failed;
        var failed = Assert.Single(view.Build(), r => r.IsGroup);
        Assert.Equal(LibraryMarker.Failed, failed.Marker);               // 只有成员失败 → 组行红
        Assert.Equal(LibraryInstalledState.Yes, failed.InstalledState);  // 勾不受红影响
    }

    [Fact]
    public void 全部文件行_含被搜索过滤掉的行_标记刷新不能只遍历界面行()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[] { Row(1, "A", "com.a"), Row(2, "B", "com.b") });
        view.SearchText = "com.a";

        Assert.Single(view.Build());
        Assert.Equal(2, view.AllFiles.Count());   // 组收起 / 被过滤的成员也要能刷状态（踩坑 C34）
    }
    [Fact]
    public void 合并组_其他版本蓝底_但同版本成员优先绿勾()
    {
        var view = new LibraryView();
        view.ReplaceAll(new[]
        {
            Row(1, "Gyro", "com.gyro", version: "1.0"),
            Row(2, "Gyro", "com.gyro", version: "1.1"),
        });
        view.Rows[0].InstalledState = LibraryInstalledState.OtherVersion;
        view.Rows[1].InstalledState = LibraryInstalledState.OtherVersion;

        var blue = Assert.Single(view.Build(), r => r.IsGroup);
        Assert.Equal(LibraryInstalledState.OtherVersion, blue.InstalledState);
        Assert.False(blue.IsInstalledTicked);
        Assert.True(blue.IsInstalledOtherVersion);

        view.Rows[1].InstalledState = LibraryInstalledState.Yes;
        var green = Assert.Single(view.Build(), r => r.IsGroup);
        Assert.Equal(LibraryInstalledState.Yes, green.InstalledState);
        Assert.True(green.IsInstalledTicked);
        Assert.False(green.IsInstalledOtherVersion);
    }

    [Fact]
    public void 合并_组不置顶_按名称与体积排序与单文件穿插混排()
    {
        var missing = Row(99, "AaaMissing", "com.missing", appSize: 9999);
        missing.ExistsFlag = false;

        var view = new LibraryView();
        view.ReplaceAll(new[]
        {
            Row(1, "Alpha", "com.alpha", appSize: 100),
            Row(2, "Beta", "com.beta", version: "1.0", appSize: 500),
            Row(3, "Beta", "com.beta", version: "2.0", appSize: 150),
            Row(4, "Mid", "com.mid", appSize: 400),
            Row(5, "Z_Top", "com.ztop", appSize: 900),
            Row(6, "Zeta", "com.zeta", version: "1.0", appSize: 700),
            Row(7, "Zeta", "com.zeta", version: "2.0", appSize: 50),
            missing,
        });

        view.Sort = new LibrarySort(LibrarySortField.Name, Descending: false);
        var byName = view.Build();
        Assert.Equal(
            new[] { "Alpha", "Beta", "Mid", "Z_Top", "Zeta", "AaaMissing" },
            byName.Select(r => r.Name).ToArray());
        Assert.Equal(
            new[] { false, true, false, false, true, false },
            byName.Select(r => r.IsGroup).ToArray());
        Assert.True(byName[1].IsGroupEnd);
        Assert.True(byName[4].IsGroupEnd);
        Assert.True(byName[^1].IsMissing);

        view.Sort = new LibrarySort(LibrarySortField.AppSize, Descending: true);
        var bySize = view.Build();
        Assert.Equal(
            new[] { "Z_Top", "Zeta", "Beta", "Mid", "Alpha", "AaaMissing" },
            bySize.Select(r => r.Name).ToArray());
        Assert.Equal(
            new[] { false, true, true, false, false, false },
            bySize.Select(r => r.IsGroup).ToArray());
        Assert.True(bySize[1].IsGroupEnd);
        Assert.True(bySize[2].IsGroupEnd);
        Assert.True(bySize[^1].IsMissing);
    }

}
