using IdeviceGui.Resources;
using IdeviceGui.Services.Data;
using IdeviceGui.ViewModels.Library;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>M4-b2：`Grid.Columns` 的读写（顺序 / 宽度 / 显隐，key 稳定、不认"第几列"）。</summary>
public sealed class LibraryColumnsTests
{
    [Fact]
    public void 默认十三列_已装在最前_来源在名称后_路径在最后()
    {
        var keys = LibraryColumns.Defaults.Select(c => c.Key).ToArray();

        Assert.Equal(13, keys.Length);
        Assert.Equal(
            new[]
            {
                LibraryColumns.Installed, LibraryColumns.Icon, LibraryColumns.Name, LibraryColumns.Origin, LibraryColumns.AppSize, LibraryColumns.Version,
                LibraryColumns.MinOs, LibraryColumns.DeviceFamily, LibraryColumns.BundleId, LibraryColumns.Arch,
                LibraryColumns.FileSize, LibraryColumns.State, LibraryColumns.FilePath,
            },
            keys);
        Assert.Equal(Strings.Main_Column_AppSize, LibraryColumns.Defaults.Single(c => c.Key == LibraryColumns.AppSize).Header);
        Assert.Equal(LibraryColumns.FilePath, keys[^1]);

        // 2026-11-02 用户定：勾列宽度砍半 26/22 → 13/11
        var installed = LibraryColumns.Defaults.Single(c => c.Key == LibraryColumns.Installed);
        Assert.Equal(13, installed.DefaultWidth);
        Assert.Equal(11, installed.MinWidth);
    }

    [Fact]
    public void 保存后读回_顺序与宽度都不变()
    {
        using var workspace = new TestWorkspace("grid-cols");
        using var db = AppDatabase.Open(workspace.Full("library.db"));

        var states = new[]
        {
            new GridColumnState(LibraryColumns.Name, 200, true),
            new GridColumnState(LibraryColumns.Icon, 48, true),
            new GridColumnState(LibraryColumns.State, 150, false),
        };
        LibraryColumns.Save(db, states);
        var loaded = LibraryColumns.Load(db);

        // 前三条按保存的顺序；缺的 7 列按默认顺序补在后面
        Assert.Equal(LibraryColumns.Name, loaded[0].Key);
        Assert.Equal(200, loaded[0].Width);
        Assert.Equal(LibraryColumns.Icon, loaded[1].Key);
        Assert.Equal(48, loaded[1].Width);
        Assert.Equal(LibraryColumns.State, loaded[2].Key);
        Assert.False(loaded[2].Visible);
        Assert.Equal(13, loaded.Count);
    }

    [Fact]
    public void 旧版本配置_v5迁移保留用户列顺序_只补新增列()
    {
        using var workspace = new TestWorkspace("grid-cols-order");
        using var db = AppDatabase.Open(workspace.Full("library.db"));

        // 模拟 v4：用户拖成"名称 → 图标 → 体积"的自定义顺序（installed 在最前，旧版本没有 filePath）
        var oldRaw = "[{\"key\":\"installed\",\"width\":30,\"visible\":true}," +
                     "{\"key\":\"name\",\"width\":200,\"visible\":true}," +
                     "{\"key\":\"icon\",\"width\":40,\"visible\":true}," +
                     "{\"key\":\"size\",\"width\":100,\"visible\":false}]";
        SettingsStore.SetString(db, LibraryColumns.GridColumnsKey, oldRaw);
        SettingsStore.SetString(db, LibraryColumns.GridColumnsVersionKey, "4");

        var loaded = LibraryColumns.Load(db);
        var keys = loaded.Select(c => c.Key).ToList();

        // 审查 B1：用户顺序不能被冲掉（旧实现会变成 Defaults 顺序）
        Assert.Equal(
            new[] { LibraryColumns.Installed, LibraryColumns.Name, LibraryColumns.Icon, LibraryColumns.Origin },
            keys.Take(4).ToArray());
        Assert.Equal(200, loaded.Single(c => c.Key == LibraryColumns.Name).Width);
        Assert.False(loaded.Single(c => c.Key == LibraryColumns.AppSize).Visible);   // 可见性也保留
        Assert.Equal(13, loaded.Single(c => c.Key == LibraryColumns.Installed).Width);  // v5 勾列宽度仍生效
        Assert.Equal(LibraryColumns.Defaults.Count, loaded.Count);                    // 缺的列补齐
        Assert.Equal(LibraryColumns.FilePath, keys[^1]);                              // 新增的路径列在最后
    }

    [Fact]
    public void 旧版本配置_v5迁移把勾列宽度改成13或默认值()
    {
        using var workspace = new TestWorkspace("grid-cols-v5");
        using var db = AppDatabase.Open(workspace.Full("library.db"));

        // 模拟 v4 的配置：勾列被用户拖到 30 宽，其它列自定义 200
        var oldRaw = "[{\"key\":\"installed\",\"width\":30,\"visible\":true}," +
                     "{\"key\":\"name\",\"width\":200,\"visible\":true}]";
        SettingsStore.SetString(db, LibraryColumns.GridColumnsKey, oldRaw);
        SettingsStore.SetString(db, LibraryColumns.GridColumnsVersionKey, "4");

        var loaded = LibraryColumns.Load(db);

        Assert.Equal(13, loaded.Single(c => c.Key == LibraryColumns.Installed).Width);   // 勾列宽度被砍半
        Assert.Equal(200, loaded.Single(c => c.Key == LibraryColumns.Name).Width);       // 其它列保留
        Assert.Equal(LibraryColumns.LayoutVersion.ToString(),
            SettingsStore.GetString(db, LibraryColumns.GridColumnsVersionKey));
    }

        [Fact]
    public void 旧配置无版本号_保留用户顺序并补齐新增列()
    {
        using var workspace = new TestWorkspace("grid-cols-migrate");
        using var db = AppDatabase.Open(workspace.Full("library.db"));

        // 模拟老机器上的旧顺序：name 开头、state 第 3、icon 第 5（2026-11-01 实测的那份）
        var oldRaw = "[{\"key\":\"name\",\"width\":220,\"visible\":true}," +
                     "{\"key\":\"size\",\"width\":111.5,\"visible\":true}," +
                     "{\"key\":\"state\",\"width\":130,\"visible\":false}," +
                     "{\"key\":\"version\",\"width\":70,\"visible\":true}," +
                     "{\"key\":\"icon\",\"width\":44,\"visible\":true}]";
        SettingsStore.SetString(db, LibraryColumns.GridColumnsKey, oldRaw);

        var loaded = LibraryColumns.Load(db);
        var keys = loaded.Select(c => c.Key).ToList();

        Assert.Equal(13, loaded.Count);

        // 审查 B1（2026-11-03 修）：迁移**不再按默认顺序重排**——用户原有的相对顺序必须保住
        // （installed 可补到最前；其余新增列插在它们默认顺序的缝隙里，但绝不把 name/size/state/version/icon 打乱）
        Assert.Equal(LibraryColumns.Installed, loaded[0].Key);      // 新增的"已装列"补到最前
        Assert.Equal(LibraryColumns.Name, loaded[1].Key);           // name 仍是用户顺序里的第一个
        Assert.Equal(LibraryColumns.Origin, loaded[2].Key);          // 新增的来源列插在 name 后
        Assert.Equal(LibraryColumns.AppSize, loaded[3].Key);        // size 仍在 name 之后
        var stateIndex = keys.IndexOf(LibraryColumns.State);
        var versionIndex = keys.IndexOf(LibraryColumns.Version);
        var iconIndex = keys.IndexOf(LibraryColumns.Icon);
        Assert.True(stateIndex < versionIndex && versionIndex < iconIndex,
            "用户原有的 state→version→icon 相对顺序被打乱了：" + string.Join(",", keys));
        Assert.Equal(220, loaded.Single(c => c.Key == LibraryColumns.Name).Width);      // 宽度保留
        Assert.Equal(111.5, loaded.Single(c => c.Key == LibraryColumns.AppSize).Width, 3);
        Assert.False(loaded.Single(c => c.Key == LibraryColumns.State).Visible);        // 可见性保留
        Assert.Equal(LibraryColumns.FilePath, keys[^1]);                                // 新增的路径列补在最后
        Assert.Equal(44, loaded.Single(c => c.Key == LibraryColumns.Icon).Width);
        Assert.Equal(13, loaded.Single(c => c.Key == LibraryColumns.Installed).Width);  // v5 勾列宽度仍生效
        Assert.Equal(LibraryColumns.LayoutVersion.ToString(), SettingsStore.GetString(db, LibraryColumns.GridColumnsVersionKey));
    }

    [Fact]
    public void 迁移后再保存的自定义顺序被尊重()
    {
        using var workspace = new TestWorkspace("grid-cols-custom");
        using var db = AppDatabase.Open(workspace.Full("library.db"));

        var custom = new[]
        {
            new GridColumnState(LibraryColumns.Name, 200, true),
            new GridColumnState(LibraryColumns.Icon, 48, true),
            new GridColumnState(LibraryColumns.State, 150, false),
        };
        LibraryColumns.Save(db, custom);      // Save 会写版本号

        var loaded = LibraryColumns.Load(db);
        Assert.Equal(LibraryColumns.Name, loaded[0].Key);
        Assert.Equal(LibraryColumns.Icon, loaded[1].Key);
        Assert.Equal(LibraryColumns.State, loaded[2].Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("不是 JSON")]
    [InlineData("[]")]
    [InlineData("[{\"key\":null}]")]
    public void 坏值一律回默认十三列(string? raw)
        => Assert.Equal(13, LibraryColumns.Parse(raw).Count);

    [Fact]
    public void 未知列丢弃_缺列补齐_重复只留第一个()
    {
        var json = """
            [
              {"key":"unknownColumn","width":100,"visible":true},
              {"key":"name","width":200,"visible":true},
              {"key":"name","width":300,"visible":true},
              {"key":"arch","width":90,"visible":true}
            ]
            """;

        var states = LibraryColumns.Parse(json);

        Assert.Equal(13, states.Count);
        Assert.DoesNotContain(states, s => s.Key == "unknownColumn");
        Assert.Equal(200, states.Single(s => s.Key == LibraryColumns.Name).Width);   // 重复 key 只留第一个
        Assert.Equal(LibraryColumns.Name, states[0].Key);
        Assert.Equal(LibraryColumns.Arch, states[1].Key);
    }

    [Fact]
    public void 宽度越界被夹紧_坏宽度回默认()
    {
        var json = """
            [
              {"key":"name","width":5,"visible":true},
              {"key":"arch","width":9999,"visible":true},
              {"key":"state","width":0,"visible":true}
            ]
            """;
        var states = LibraryColumns.Parse(json);

        Assert.Equal(120, states.Single(s => s.Key == LibraryColumns.Name).Width);   // MinWidth=120
        Assert.Equal(600, states.Single(s => s.Key == LibraryColumns.Arch).Width);   // 上限 600
        Assert.Equal(120, states.Single(s => s.Key == LibraryColumns.State).Width);  // 坏宽度 → 默认 120
    }

    [Fact]
    public void 保存顺序按DisplayIndex_而不是Columns集合顺序()
    {
        // 用户把「状态」拖到最前、「名称」拖到最后：Columns 集合顺序没变，只有 DisplayIndex 变了
        var entries = new[]
        {
            new GridColumnEntry(LibraryColumns.Name, 2, 200, true),
            new GridColumnEntry(LibraryColumns.Icon, 1, 44, true),
            new GridColumnEntry(LibraryColumns.State, 0, 130, true),
        };

        var states = LibraryColumns.OrderForSave(entries);

        Assert.Equal(
            new[] { LibraryColumns.State, LibraryColumns.Icon, LibraryColumns.Name },
            states.Select(s => s.Key).ToArray());
        Assert.Equal(200, states.Single(s => s.Key == LibraryColumns.Name).Width);
    }

    [Fact]
    public void 序列化再解析_完全还原()
    {
        var states = LibraryColumns.DefaultStates
            .Select(s => s with { Width = s.Width + 10 })
            .ToArray();

        var roundTrip = LibraryColumns.Parse(LibraryColumns.Serialize(states));

        Assert.Equal(states.Length, roundTrip.Count);
        for (var i = 0; i < states.Length; i++)
        {
            Assert.Equal(states[i].Key, roundTrip[i].Key);
            Assert.Equal(states[i].Width, roundTrip[i].Width);
        }
    }

    [Fact]
    public void 列显隐接口_只改目标列_保留顺序与宽度()
    {
        var states = LibraryColumns.DefaultStates;

        var changed = LibraryColumns.SetVisible(states, LibraryColumns.Arch, visible: false);

        Assert.False(changed.Single(s => s.Key == LibraryColumns.Arch).Visible);
        Assert.Equal(states.Select(s => s.Key), changed.Select(s => s.Key));   // 顺序不变
        Assert.Equal(states.Single(s => s.Key == LibraryColumns.Name).Width,
            changed.Single(s => s.Key == LibraryColumns.Name).Width);          // 其它列宽度不变

        var unknown = LibraryColumns.SetVisible(states, "not-a-column", visible: false);
        Assert.Equal(states.Count, unknown.Count);
        Assert.All(unknown, s => Assert.True(s.Visible));
    }

}
