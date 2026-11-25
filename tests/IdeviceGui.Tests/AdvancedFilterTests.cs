using System.Globalization;
using IdeviceGui.Resources;
using IdeviceGui.Services.Data;
using IdeviceGui.Services.Parsing;
using IdeviceGui.ViewModels.Library;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>「已适配」= 至少支持当前设备屏幕档位；Universal 按设备侧判断。</summary>
public sealed class DeviceAdaptationAtLeastTests
{
    [Fact]
    public void 更高档App_适配低档设备()
    {
        Assert.True(DeviceAdaptation.IsAdapted("4.7\"/5.5\"", ScreenTier.Retina35));
        Assert.True(DeviceAdaptation.IsAdapted("4\"", ScreenTier.Retina35));
        Assert.False(DeviceAdaptation.IsAdapted("3.5\"", ScreenTier.Screen4));
    }

    [Fact]
    public void 只适配一侧时_按设备类型判断()
    {
        Assert.True(DeviceAdaptation.IsAdapted("4\" + iPad Retina", ScreenTier.Screen4));
        Assert.True(DeviceAdaptation.IsAdapted("4\" + iPad Retina", ScreenTier.Ipad));
        Assert.True(DeviceAdaptation.IsAdapted("4\" + iPad Retina", ScreenTier.IpadRetina));
        Assert.False(DeviceAdaptation.IsAdapted("3.5\" + iPad", ScreenTier.Screen4));
        Assert.False(DeviceAdaptation.IsAdapted("unknown", ScreenTier.Retina35));
        Assert.False(DeviceAdaptation.IsAdapted("iPad", ScreenTier.Screen4));
    }
}

/// <summary>兼容性下拉框三种模式（2026-11-05）。</summary>
public sealed class DeviceCompatModeTests
{
    private static LibraryRow Row(string minOs, string familyText, string rawFamily = "iPhone")
    {
        var row = new LibraryRow { Id = 1 };
        row.UpdateFrom(new PackageRow
        {
            Id = 1,
            FilePath = @"C:\a.ipa",
            FileName = "a.ipa",
            DisplayName = "A",
            DeviceFamily = rawFamily,
            MinOs = minOs,
        });
        // UpdateFrom 的显示串来自证据；这里直接覆盖做纯判定
        row.DeviceFamilyText = familyText;
        return row;
    }

    [Fact]
    public void Off_总是放行()
    {
        Assert.True(DeviceCompat.Pass(Row("9.0", "3.5\""), CompatFilterMode.Off, "iPhone5,2", "8.1.2", "N42AP"));
    }

    [Fact]
    public void 可安装_只看族与MinOs()
    {
        Assert.True(DeviceCompat.Pass(Row("4.0", "3.5\""), CompatFilterMode.Installable, "iPhone5,2", "8.1.2", "N42AP"));
        Assert.False(DeviceCompat.Pass(Row("9.0", "3.5\""), CompatFilterMode.Installable, "iPhone5,2", "8.1.2", "N42AP"));
    }

    [Fact]
    public void 已适配_还要求屏幕档位至少支持()
    {
        Assert.True(DeviceCompat.Pass(Row("4.0", "4.7\"/5.5\""), CompatFilterMode.Adapted, "iPhone5,2", "8.1.2", "N42AP"));
        Assert.False(DeviceCompat.Pass(Row("4.0", "3.5\""), CompatFilterMode.Adapted, "iPhone5,2", "8.1.2", "N42AP"));
        Assert.True(DeviceCompat.Pass(Row("4.0", "4\" + iPad Retina", "Universal"), CompatFilterMode.Adapted, "iPad3,1", "6.1.3", null));
    }

    [Fact]
    public void 缺MinOs或设备信息放行()
    {
        Assert.True(DeviceCompat.Pass(Row("", "4.7\"/5.5\""), CompatFilterMode.Adapted, "iPhone5,2", "8.1.2", "N42AP"));
        Assert.True(DeviceCompat.Pass(Row("4.0", "3.5\""), CompatFilterMode.Adapted, null, null, null));
    }
}

/// <summary>高级筛选核心逻辑（2026-11-05）：组内 AND/OR、组间 AND、文本/版本/MB/多选。</summary>
public sealed class AdvancedFilterTests
{
    private static LibraryRow Row(string name = "Game", string bundle = "com.test.game", string version = "1.2.3",
        string minOs = "4.0", long appSize = 100L * 1024 * 1024, string family = "iPhone", string arch = "v7")
    {
        var row = new LibraryRow { Id = 1 };
        row.UpdateFrom(new PackageRow
        {
            Id = 1,
            FilePath = $@"C:\{bundle}.ipa",
            FileName = bundle + ".ipa",
            DisplayName = name,
            BundleId = bundle,
            Version = version,
            MinOs = minOs,
            Arch = arch,
            ParseState = ParseStates.Ok,
            ExistsFlag = true,
            AppSize = appSize,
        });
        row.DeviceFamilyText = family;
        return row;
    }

    [Fact]
    public void 组内AND_组间AND()
    {
        var state = new AdvancedFilterState
        {
            Enabled = true,
            Groups =
            {
                new AdvancedFilterGroup
                {
                    Join = FilterJoin.And,
                    Conditions =
                    {
                        new AdvancedFilterCondition { Column = AdvancedFilterColumn.Name, Text = "Game", TextMode = TextFilterMode.Contains },
                        new AdvancedFilterCondition { Column = AdvancedFilterColumn.Version, Min = "1.0", Max = "2.0" },
                    },
                },
            },
        };
        Assert.True(state.Matches(Row(), "iPhone5,2", "8.1.2", "N42AP"));
        Assert.False(state.Matches(Row(name: "Other"), "iPhone5,2", "8.1.2", "N42AP"));
    }

    [Fact]
    public void 组内OR_组间AND()
    {
        var state = new AdvancedFilterState
        {
            Enabled = true,
            Groups =
            {
                new AdvancedFilterGroup
                {
                    Join = FilterJoin.Or,
                    Conditions =
                    {
                        new AdvancedFilterCondition { Column = AdvancedFilterColumn.Name, Text = "Game", TextMode = TextFilterMode.Contains },
                        new AdvancedFilterCondition { Column = AdvancedFilterColumn.BundleId, Text = "com.test", TextMode = TextFilterMode.Contains },
                    },
                },
                new AdvancedFilterGroup
                {
                    Join = FilterJoin.And,
                    Conditions = { new AdvancedFilterCondition { Column = AdvancedFilterColumn.AppSizeMb, Min = "50", Max = "200" } },
                },
            },
        };
        Assert.True(state.Matches(Row(name: "Other", bundle: "com.test.app"), "iPhone5,2", "8.1.2", "N42AP"));
        Assert.False(state.Matches(Row(appSize: 10L * 1024 * 1024), "iPhone5,2", "8.1.2", "N42AP"));
    }

    [Fact]
    public void 设备族两侧拼接_UniversalRetina等价()
    {
        var state = new AdvancedFilterState
        {
            Enabled = true,
            Groups =
            {
                new AdvancedFilterGroup
                {
                    Conditions =
                    {
                        new AdvancedFilterCondition
                        {
                            Column = AdvancedFilterColumn.DeviceFamily,
                            PhoneSide = "4.7\"/5.5\"",
                            PadSide = "iPad Retina",
                        },
                    },
                },
            },
        };
        Assert.True(state.Matches(Row(family: "Universal Retina"), "iPhone5,2", "8.1.2", "N42AP"));
        Assert.True(state.Matches(Row(family: "4.7\"/5.5\" + iPad Retina"), "iPhone5,2", "8.1.2", "N42AP"));
    }

    [Fact]
    public void 多选值与精确包含()
    {
        var state = new AdvancedFilterState
        {
            Enabled = true,
            Groups =
            {
                new AdvancedFilterGroup
                {
                    Join = FilterJoin.And,
                    Conditions =
                    {
                        new AdvancedFilterCondition { Column = AdvancedFilterColumn.Arch, Selected = { "v7", "arm64" } },
                        new AdvancedFilterCondition { Column = AdvancedFilterColumn.Name, Text = "Game", TextMode = TextFilterMode.Exact },
                    },
                },
            },
        };
        Assert.True(state.Matches(Row(), "iPhone5,2", "8.1.2", "N42AP"));
        Assert.False(state.Matches(Row(name: "Game2"), "iPhone5,2", "8.1.2", "N42AP"));
    }

    [Fact]
    public void JSON往返()
    {
        var state = new AdvancedFilterState
        {
            Enabled = true,
            Groups =
            {
                new AdvancedFilterGroup
                {
                    Join = FilterJoin.Or,
                    Conditions =
                    {
                        new AdvancedFilterCondition { Column = AdvancedFilterColumn.Version, Min = "1.0", Max = "2.0" },
                        new AdvancedFilterCondition { Column = AdvancedFilterColumn.Arch, Selected = { "v7" } },
                    },
                },
            },
        };
        var copy = AdvancedFilterCodec.Parse(AdvancedFilterCodec.Serialize(state));
        Assert.True(copy.Enabled);
        Assert.Single(copy.Groups);
        Assert.Equal(FilterJoin.Or, copy.Groups[0].Join);
        Assert.Equal(2, copy.Groups[0].Conditions.Count);
    }
    [Fact]
    public void R文案下_设备族条件与适配判定一致()
    {
        // R 形态与 Retina 形态必须语义等价（审查报告 P1）
        Assert.True(DeviceAdaptation.IsAdapted("iPad R", ScreenTier.IpadRetina));
        Assert.True(DeviceAdaptation.IsAdapted("Universal R", ScreenTier.Screen47));
        Assert.True(DeviceAdaptation.IsAdapted("Universal R", ScreenTier.IpadRetina));
        Assert.True(DeviceAdaptation.IsAdapted("4\" + iPad R", ScreenTier.Screen4));
        Assert.True(DeviceAdaptation.IsAdapted("4\" + iPad R", ScreenTier.IpadRetina));
        Assert.Equal("iPad Retina", DeviceAdaptation.NormalizeDisplay("iPad R"));

        var state = new AdvancedFilterState
        {
            Enabled = true,
            Groups =
            {
                new AdvancedFilterGroup
                {
                    Conditions =
                    {
                        new AdvancedFilterCondition
                        {
                            Column = AdvancedFilterColumn.DeviceFamily,
                            PadSide = "iPad Retina",
                        },
                    },
                },
            },
        };
        Assert.True(state.Matches(Row(family: "iPad R"), "iPhone5,2", "8.1.2", "N42AP"));

        var universalState = new AdvancedFilterState
        {
            Enabled = true,
            Groups =
            {
                new AdvancedFilterGroup
                {
                    Conditions =
                    {
                        new AdvancedFilterCondition
                        {
                            Column = AdvancedFilterColumn.DeviceFamily,
                            PhoneSide = "4.7\"/5.5\"",
                            PadSide = "iPad Retina",
                        },
                    },
                },
            },
        };
        Assert.True(universalState.Matches(Row(family: "Universal R"), "iPhone5,2", "8.1.2", "N42AP"));
    }

    [Fact]
    public void 来源条件_按显示标签匹配()
    {
        var state = new AdvancedFilterState
        {
            Enabled = true,
            Groups =
            {
                new AdvancedFilterGroup
                {
                    Conditions =
                    {
                        new AdvancedFilterCondition
                        {
                            Column = AdvancedFilterColumn.Origin,
                            Selected = { "已重签名" },
                        },
                    },
                },
            },
        };
        var resigned = Row();
        resigned.OriginKind = "resigned";
        var decrypted = Row();
        decrypted.OriginKind = "decrypted";
        Assert.True(state.Matches(resigned, "iPhone5,2", "8.1.2", "N42AP"));
        Assert.False(state.Matches(decrypted, "iPhone5,2", "8.1.2", "N42AP"));
    }

    [Fact]
    public void 英文模式下_来源架构状态筛选仍按规范键一致匹配()
    {
        using var _ = Strings.UseCultureScope(CultureInfo.GetCultureInfo("en"));

        var resignedNormal = Row(arch: "armv7");
        resignedNormal.OriginKind = PackageOrigin.Resigned;
        Assert.Equal("Re-signed", resignedNormal.OriginText);
        Assert.Equal("已重签名", resignedNormal.OriginFilterKey);
        Assert.Equal("v7", resignedNormal.ArchFilterKey);
        Assert.Equal("Normal", resignedNormal.StateText);
        Assert.Equal("正常", resignedNormal.StateFilterKey);

        var missingRow = Row(arch: "arm64");
        missingRow.OriginKind = PackageOrigin.Decrypted;
        missingRow.ExistsFlag = false;
        Assert.Equal("Missing File", missingRow.StateText);
        Assert.Equal("文件缺失", missingRow.StateFilterKey);

        var state = new AdvancedFilterState
        {
            Enabled = true,
            Groups =
            {
                new AdvancedFilterGroup
                {
                    Join = FilterJoin.And,
                    Conditions =
                    {
                        new AdvancedFilterCondition { Column = AdvancedFilterColumn.Origin, Selected = { "已重签名" } },
                        new AdvancedFilterCondition { Column = AdvancedFilterColumn.Arch, Selected = { "v7" } },
                        new AdvancedFilterCondition { Column = AdvancedFilterColumn.State, Selected = { "正常" } },
                    },
                },
            },
        };

        Assert.True(state.Matches(resignedNormal, "iPhone5,2", "8.1.2", "N42AP"));
        Assert.False(state.Matches(missingRow, "iPhone5,2", "8.1.2", "N42AP"));

        var missingFilter = new AdvancedFilterState
        {
            Enabled = true,
            Groups =
            {
                new AdvancedFilterGroup
                {
                    Conditions =
                    {
                        new AdvancedFilterCondition { Column = AdvancedFilterColumn.State, Selected = { "文件缺失" } },
                    },
                },
            },
        };

        Assert.True(missingFilter.Matches(missingRow, "iPhone5,2", "8.1.2", "N42AP"));
        Assert.False(missingFilter.Matches(resignedNormal, "iPhone5,2", "8.1.2", "N42AP"));
    }

}
