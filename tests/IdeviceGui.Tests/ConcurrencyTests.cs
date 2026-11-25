using System.IO;
using IdeviceGui.Services.Data;
using IdeviceGui.Services.Scanning;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 并发与锁（B7 补充验证）：
/// ① 两条扫描并发 → 不撞唯一索引、不重复行；② 扫描 + 设备写库并发；③ 外部写者持锁时扫描会等（busy_timeout）。
/// </summary>
public sealed class ConcurrencyTests
{
    [Fact]
    public async Task 两个扫描器并发扫同一根_不重复行不抛异常()
    {
        using var harness = new ScanHarness();
        for (var index = 0; index < 50; index++)
            harness.Ws.AddIpa($"f{index:D3}.ipa", $"com.b7.c{index:D3}");

        var second = new IpaScanner(harness.Db);
        var first = harness.Scanner;

        var task1 = Task.Run(() => first.ScanAsync(harness.Root.Id, harness.Root.Path).GetAwaiter().GetResult());
        var task2 = Task.Run(() => second.ScanAsync(harness.Root.Id, harness.Root.Path).GetAwaiter().GetResult());
        await Task.WhenAll(task1, task2);

        var rows = harness.Rows();
        Assert.Equal(50, rows.Count);
        Assert.Equal(50, rows.Select(r => r.NormalizedPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(rows, r => Assert.Equal(ParseStates.Ok, r.ParseState));
    }

    [Fact]
    public async Task 扫描与设备写库并发_都成功()
    {
        using var harness = new ScanHarness();
        for (var index = 0; index < 30; index++)
            harness.Ws.AddIpa($"g{index:D3}.ipa", $"com.b7.d{index:D3}");

        var scanTask = Task.Run(() => harness.Scanner.ScanAsync(harness.Root.Id, harness.Root.Path).GetAwaiter().GetResult());
        var deviceTasks = Enumerable.Range(0, 40).Select(index => Task.Run(() =>
            DeviceStore.Upsert(harness.Db, new DeviceRow
            {
                Udid = index % 2 == 0 ? "udid-A" : "udid-B",
                Name = "iPhone " + index,
                ProductType = "iPhone3,1",
                IosVersion = "4.1",
                CpuArch = "armv7",
                LastSeenUtc = UtcText.Now(),
            }))).ToArray();

        await Task.WhenAll(deviceTasks.Append(scanTask));

        Assert.Equal(30, harness.Rows().Count);
        // 并发 upsert 的"最后写者"不确定，只断言两条 udid 都在、字段来自某一批写入
        var deviceA = DeviceStore.Find(harness.Db, "udid-A");
        var deviceB = DeviceStore.Find(harness.Db, "udid-B");
        Assert.NotNull(deviceA);
        Assert.NotNull(deviceB);
        Assert.StartsWith("iPhone ", deviceA!.Name);
        Assert.StartsWith("iPhone ", deviceB!.Name);
        Assert.Equal(2, harness.Db.Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Devices;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }));
    }

    [Fact]
    public async Task 外部写者持锁时_扫描等待后成功()
    {
        using var harness = new ScanHarness();
        harness.Ws.AddIpa("h.ipa", "com.b7.e001");

        // 模拟"另一个进程"持写锁 800 ms：BEGIN IMMEDIATE + 插入 + 睡一会儿
        using var locker = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = harness.Db.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        locker.Open();
        using (var busy = locker.CreateCommand())
        {
            busy.CommandText = "PRAGMA busy_timeout=5000;";
            busy.ExecuteNonQuery();
        }

        using var tx = locker.BeginTransaction(deferred: false);
        using (var insert = locker.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO Settings(Key, Value) VALUES('Probe.Locker','1') " +
                                 "ON CONFLICT(Key) DO UPDATE SET Value='1';";
            insert.ExecuteNonQuery();
        }

        var holdMs = 800;
        var scanTask = Task.Run(() =>
        {
            Thread.Sleep(100);   // 让扫描先读到"没有变化"，再撞上写锁
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = harness.Scanner.ScanAsync(harness.Root.Id, harness.Root.Path).GetAwaiter().GetResult();
            watch.Stop();
            return (Result: result, Ms: watch.ElapsedMilliseconds);
        });

        Thread.Sleep(holdMs);
        tx.Commit();

        var (result, elapsedMs) = await scanTask;

        Assert.Equal(1, result.Added);
        Assert.Single(harness.Rows());
        // 等了锁：耗时应明显长于无锁情况（只记录趋势，不设硬阈值）
        Assert.True(elapsedMs >= holdMs - 200, $"应当等锁，实际 {elapsedMs} ms");
    }

    // ── P2-3：短超时写（写探测 / 设备快照）要真的"很快就放弃" ───────────────
    [Fact]
    public void 短超时写_外部持锁时约两秒放弃_且连接超时被恢复()
    {
        using var harness = new ScanHarness();

        using var locker = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = harness.Db.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        locker.Open();
        using var tx = locker.BeginTransaction(deferred: false);
        using (var insert = locker.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO Settings(Key, Value) VALUES('Probe.Locker','1') " +
                                 "ON CONFLICT(Key) DO UPDATE SET Value='1';";
            insert.ExecuteNonQuery();
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<SqliteException>(() => harness.Db.Write(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO Settings(Key, Value) VALUES('Probe.Short','1');";
            return cmd.ExecuteNonQuery();
        }, waitMs: 1000, busyTimeoutMs: 1000));
        watch.Stop();
        // 5 s 是给慢机器留的余量；关键是远小于 provider 默认的 30 s
        Assert.True(watch.ElapsedMilliseconds < 5000, $"短超时应当很快放弃（实际 {watch.ElapsedMilliseconds} ms）");

        tx.Rollback();

        // 恢复：锁释放后普通写立刻可成功（说明 DefaultTimeout / busy_timeout 都还回去了）
        harness.Db.Write(_ => 0);
        Assert.Null(harness.Scanner.ProbeDatabase());
    }
}
