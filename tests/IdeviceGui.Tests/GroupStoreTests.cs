using System.IO;
using IdeviceGui.Services.Groups;
using IdeviceGui.ViewModels.Library;
using Xunit;

namespace IdeviceGui.Tests;

public sealed class GroupStoreTests
{
    private static LibraryRow Row(long id, string path, ulong? fileId = null,
        string bundle = "com.x", string version = "1.0", string build = "1", long size = 100)
        => new()
        {
            Id = id,
            FilePath = path,
            NormalizedPath = path,
            FileName = Path.GetFileName(path),
            BundleId = bundle,
            Version = version,
            Build = build,
            FileSize = size,
            VolumeSerial = fileId is null ? null : 7,
            FileIdHigh = fileId is null ? null : 0,
            FileIdLow = fileId,
        };

    [Fact]
    public void 分组JSON往返无损()
    {
        using var ws = new TestWorkspace("groups");
        var file = new GroupFile();
        file.Groups.Add(new UserGroup
        {
            Name = "游戏",
            Members = { GroupStore.FromRow(Row(1, ws.Full("a.ipa"), fileId: 11)) },
        });

        Assert.True(GroupStore.Save(file, ws.Root, out var error));
        Assert.Null(error);
        var loaded = GroupStore.Load(ws.Root, out var warning);
        Assert.Null(warning);
        Assert.Single(loaded.Groups);
        Assert.Equal("游戏", loaded.Groups[0].Name);
        Assert.Equal(11UL, loaded.Groups[0].Members[0].FileIdLow);
    }

    [Fact]
    public void 分组成员按FileId路径快照绑定_找不到的移除()
    {
        using var ws = new TestWorkspace("groups");
        var byId = Row(1, ws.Full("id.ipa"), fileId: 11);
        var byPath = Row(2, ws.Full("path.ipa"), bundle: "com.p", version: "2.0", build: "2", size: 200);
        var bySnapshot = Row(3, ws.Full("snap.ipa"), bundle: "com.s", version: "3.0", build: "3", size: 300);
        var rows = new[] { byId, byPath, bySnapshot };

        var group = new UserGroup
        {
            Name = "混合",
            Members =
            {
                new GroupMember { VolumeSerial = 7, FileIdHigh = 0, FileIdLow = 11 },
                new GroupMember { NormalizedPath = ws.Full("path.ipa") },
                new GroupMember { BundleId = "com.s", Version = "3.0", Build = "3", FileSize = 300 },
                new GroupMember { BundleId = "com.gone", Version = "1.0", Build = "1", FileSize = 1 },
            },
        };
        var file = new GroupFile { Groups = { group } };

        var result = GroupStore.Resolve(file, rows);

        Assert.True(result.Changed);                       // 最后一个找不到 → 移除
        Assert.Equal(3, group.Members.Count);
        Assert.Equal(3, result.RowIds["混合"].Count);
    }

    [Fact]
    public void 库为空时Resolve不清空成员()
    {
        var group = new UserGroup
        {
            Name = "保留",
            Members = { new GroupMember { BundleId = "com.x", Version = "1.0", Build = "1", FileSize = 100 } },
        };
        var file = new GroupFile { Groups = { group } };

        var result = GroupStore.Resolve(file, Array.Empty<LibraryRow>());

        Assert.False(result.Changed);
        Assert.Single(group.Members);
    }

    [Fact]
    public void 坏JSON备份且返回空分组()
    {
        using var ws = new TestWorkspace("groups");
        File.WriteAllText(GroupStore.PathFor(ws.Root), "{ not json");
        var file = GroupStore.Load(ws.Root, out var warning);
        Assert.Empty(file.Groups);
        Assert.NotNull(warning);
        Assert.Single(Directory.GetFiles(ws.Root, "groups.bad-*.json"));
    }
}
