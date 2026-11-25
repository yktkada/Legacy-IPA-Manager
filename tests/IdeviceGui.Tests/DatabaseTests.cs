using System.IO;
using IdeviceGui.Services.Data;
using IdeviceGui.Services.Scanning;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>schema v1（docs/09 §3.1）与仓储的最基本契约。</summary>
public sealed class DatabaseTests
{
    [Fact]
    public void 建库_userVersion为2_WAL_busyTimeout()
    {
        using var ws = new TestWorkspace("db");
        using var db = AppDatabase.Open(ws.Full("library.db"));

        Assert.Equal(2, db.UserVersion);
        Assert.Equal("wal", db.JournalMode);
        Assert.Equal(15000, db.BusyTimeout);   // 2026-10-30 用户拍板 5 s → 15 s
        Assert.True(File.Exists(ws.Full("library.db")));
    }

    [Fact]
    public void schema_五张表与两个身份唯一索引()
    {
        using var ws = new TestWorkspace("db");
        using var db = AppDatabase.Open(ws.Full("library.db"));

        var tables = db.Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            using var reader = cmd.ExecuteReader();
            var list = new List<string>();
            while (reader.Read()) list.Add(reader.GetString(0));
            return list;
        });

        Assert.Equal(new[] { "Devices", "Packages", "QueueItems", "ScanRoots", "Settings" }, tables);

        var indexes = db.Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            using var reader = cmd.ExecuteReader();
            var list = new List<string>();
            while (reader.Read()) list.Add(reader.GetString(0));
            return list;
        });

        Assert.Contains("UX_Packages_FileId", indexes);
        Assert.Contains("UX_Packages_PathFallback", indexes);
        Assert.Contains("UX_QueueItems_Install", indexes);
        Assert.Contains("UX_QueueItems_Uninstall", indexes);
    }

    [Fact]
    public void 重开同一库_不重建_数据保留()
    {
        using var ws = new TestWorkspace("db");
        using (var db = AppDatabase.Open(ws.Full("library.db")))
        {
            SettingsStore.SetString(db, "Log.ToFile", "false");
        }

        using var reopened = AppDatabase.Open(ws.Full("library.db"));
        Assert.Equal(2, reopened.UserVersion);
        Assert.Equal("false", SettingsStore.GetString(reopened, "Log.ToFile"));
    }

    [Fact]
    public void Settings_读写与坏值回默认()
    {
        using var ws = new TestWorkspace("db");
        using var db = AppDatabase.Open(ws.Full("library.db"));

        Assert.True(SettingsStore.GetBool(db, SettingsStore.LogToFile, fallback: true));   // 没写过 → 默认
        SettingsStore.SetString(db, SettingsStore.LogToFile, "false");
        Assert.False(SettingsStore.GetBool(db, SettingsStore.LogToFile, fallback: true));
        SettingsStore.SetString(db, SettingsStore.LogToFile, "不是布尔值");
        Assert.True(SettingsStore.GetBool(db, SettingsStore.LogToFile, fallback: true));   // 坏值回默认
    }

    [Fact]
    public void Devices_upsert两次只有一行_字段更新()
    {
        using var ws = new TestWorkspace("db");
        using var db = AppDatabase.Open(ws.Full("library.db"));

        DeviceStore.Upsert(db, new DeviceRow { Udid = "udid-1", Name = "iPhone 4", ProductType = "iPhone3,1", IosVersion = "4.1" });
        DeviceStore.Upsert(db, new DeviceRow { Udid = "udid-1", Name = "iPhone 4", ProductType = "iPhone3,1", IosVersion = "4.1", CpuArch = "armv7" });

        var row = DeviceStore.Find(db, "udid-1");
        Assert.NotNull(row);
        Assert.Equal("armv7", row!.CpuArch);
        Assert.Equal("iPhone 4", row.Name);
    }

    [Fact]
    public void ScanRoots_禁用后仍保留行()
    {
        using var ws = new TestWorkspace("db");
        using var db = AppDatabase.Open(ws.Full("library.db"));
        var (root, error) = ScanRootStore.Add(db, ws.Root);
        Assert.Null(error);

        ScanRootStore.SetEnabled(db, root!.Id, false);

        var rows = ScanRootStore.List(db);
        Assert.Single(rows);
        Assert.False(rows[0].Enabled);
    }

    [Fact]
    public void Packages_整行往返_null与ulong都不丢()
    {
        using var ws = new TestWorkspace("db");
        using var db = AppDatabase.Open(ws.Full("library.db"));
        var (root, _) = ScanRootStore.Add(db, ws.Root);

        var row = new PackageRow
        {
            ScanRootId = root!.Id,
            FilePath = ws.Full("a.ipa"),
            FileName = "a.ipa",
            NormalizedPath = ScanPaths.Normalize(ws.Full("a.ipa")),
            IdentityKind = IdentityKind.FileId,
            VolumeSerial = 0xFFFF_FFFF_FFFF_0001,
            FileIdHigh = 0,
            FileIdLow = 0x1234_5678_9ABC_DEF0,
            FileSize = 4096,
            FileMtimeUtc = UtcText.Now(),
            ContentHash = "0123456789abcdef",
            BundleId = "com.example.alpha",
            Version = "1.0",
            Build = "1",
            DisplayName = "Alpha",
            MinOs = "6.0",
            AppDirName = "App.app",
            ParseVersion = 1,
            ParseState = ParseStates.Ok,
            ExistsFlag = true,
            FirstSeenUtc = UtcText.Now(),
            LastSeenUtc = UtcText.Now(),
        };

        PackageStore.Save(db, new[] { row });
        Assert.True(row.Id > 0);

        var loaded = PackageStore.LoadByRoot(db, root.Id).Single();
        Assert.Equal(row.Id, loaded.Id);
        Assert.Equal(0xFFFF_FFFF_FFFF_0001UL, loaded.VolumeSerial);
        Assert.Equal(0x1234_5678_9ABC_DEF0UL, loaded.FileIdLow);
        Assert.Equal(0UL, loaded.FileIdHigh);
        Assert.Equal("com.example.alpha", loaded.BundleId);
        Assert.Equal(1, loaded.ParseVersion);
        Assert.True(loaded.ExistsFlag);

        loaded.DisplayName = "Alpha 2";
        loaded.ExistsFlag = false;
        PackageStore.Save(db, new[] { loaded });
        var updated = PackageStore.LoadByRoot(db, root.Id).Single();
        Assert.Equal("Alpha 2", updated.DisplayName);
        Assert.False(updated.ExistsFlag);

        // 再加一条在位行：删除缺失记录时不能把在位文件一起删掉
        updated.Id = 0;
        updated.FilePath = ws.Full("b.ipa");
        updated.FileName = "b.ipa";
        updated.NormalizedPath = ScanPaths.Normalize(ws.Full("b.ipa"));
        updated.IdentityKind = IdentityKind.PathFallback;
        updated.VolumeSerial = 0;
        updated.FileIdHigh = 0;
        updated.FileIdLow = 0;
        updated.BundleId = "com.example.beta";
        updated.DisplayName = "Beta";
        updated.ExistsFlag = true;
        PackageStore.Save(db, new[] { updated });

        Assert.Equal(1, PackageStore.DeleteMissing(db));
        var left = PackageStore.LoadAll(db);
        Assert.Single(left);
        Assert.True(left[0].ExistsFlag);
        Assert.Equal("com.example.beta", left[0].BundleId);
        Assert.Equal(0, PackageStore.DeleteMissing(db));
    }

    /// <summary>建一个临时库（目录随调用方 TestWorkspace 一起清理时用；这里用独立临时目录，Dispose 时删）。</summary>
    private static AppDatabase OpenTempDatabase()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "_test-tmp", "db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return AppDatabase.Open(Path.Combine(dir, "library.db"));
    }

    // ── R1：写锁不可重入 + 有界等待（建议稿 §2.4）──────────────────────────────
    [Fact]
    public void Write_同线程嵌套_立即抛而不是挂死()
    {
        var db = OpenTempDatabase();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            db.Write(_ => db.Write(_ => 0)));
        sw.Stop();

        Assert.Contains("不可重入", ex.Message);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"应立刻抛（实际 {sw.ElapsedMilliseconds} ms）");
        // 抛完后锁仍然可用
        Assert.NotNull(db.Write(_ => AppDatabase.Scalar(_, "PRAGMA user_version;")));
    }

    [Fact]
    public void Write_多线程并发_串行且不丢()
    {
        var db = OpenTempDatabase();
        var errors = 0;

        Parallel.For(0, 10, i =>
        {
            for (var n = 0; n < 20; n++)
            {
                try
                {
                    SettingsStore.SetString(db, "k" + i, "v" + n);
                }
                catch
                {
                    Interlocked.Increment(ref errors);
                }
            }
        });

        Assert.Equal(0, errors);
        Assert.Equal("v19", SettingsStore.GetString(db, "k7"));
    }

    [Fact]
    public void Dispose_在Write回调里_立即抛()
    {
        var db = OpenTempDatabase();
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.Throws<InvalidOperationException>(() => db.Write(_ => { db.Dispose(); return 0; }));
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 1000, $"应立刻抛（实际 {sw.ElapsedMilliseconds} ms）");
        }
        finally
        {
            db.Dispose();   // 真正释放
        }
    }

    [Fact]
    public async Task Write_跨线程锁被占满超时_抛Timeout而不是无限等()
    {
        var db = OpenTempDatabase();
        var original = AppDatabase.WriteWaitMs;
        AppDatabase.WriteWaitMs = 200;
        try
        {
            using var held = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);

            var holder = Task.Run(() => db.Write(_ => { held.Set(); release.Wait(5000); return 0; }));
            Assert.True(held.Wait(5000, TestContext.Current.CancellationToken), "持锁线程没有拿到锁");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ex = Assert.Throws<TimeoutException>(() => db.Write(_ => 0));
            sw.Stop();
            Assert.Contains("等待数据库写锁", ex.Message);
            Assert.True(sw.ElapsedMilliseconds >= 150, $"应等满上限再抛（实际 {sw.ElapsedMilliseconds} ms）");

            release.Set();
            await holder;
        }
        finally
        {
            AppDatabase.WriteWaitMs = original;
        }
    }
}
