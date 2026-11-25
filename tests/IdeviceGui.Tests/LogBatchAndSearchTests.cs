using System;
using System.Collections.Generic;
using System.Linq;
using IdeviceGui.Services;
using IdeviceGui.Services.Data;
using IdeviceGui.ViewModels;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// b3b（2026-11-01）：日志 50 ms 批量上屏 + 搜索防抖（>1000 行才延迟 200 ms）。
/// 用 <see cref="ManualUiTimer"/> 手动推进时钟，不依赖真实 Dispatcher。
/// </summary>
public sealed class LogBatchAndSearchTests
{
    // ── 日志批量 ──────────────────────────────────────────────────────

    [Fact]
    public void 日志批量_到期前不上屏_到期后按序整批上屏()
    {
        var timer = new ManualUiTimer();
        var vm = NewVm(timer);

        vm.AppendLogForTest("第一行", LogKind.End);
        vm.AppendLogForTest("第二行", LogKind.End);

        Assert.Empty(vm.LogLines);                 // 50 ms 未到
        Assert.Equal(1, timer.ScheduleCount);      // 两行只排一次 flush
        timer.FireAll();

        Assert.Equal(new[] { "第一行", "第二行" }, vm.LogLines.Select(l => l.Text));
    }

    [Fact]
    public void 日志批量_简洁模式_只把该显示的行上屏()
    {
        var timer = new ManualUiTimer();
        var vm = NewVm(timer);

        vm.AppendLogForTest("过程行 Detail");
        vm.AppendLogForTest("结束行 End", LogKind.End);
        vm.AppendLogForTest("错误行 Error", LogKind.Error);
        timer.FireAll();

        Assert.Equal(2, vm.LogLines.Count);
        var texts = vm.LogLines.Select(l => l.Text).ToList();
        Assert.Contains("结束行 End", texts);
        Assert.Contains("错误行 Error", texts);
        Assert.DoesNotContain(texts, line => line.Contains("过程行"));
    }

    [Fact]
    public void 日志批量_FlushLogBatchNow_立刻刷出()
    {
        var timer = new ManualUiTimer();
        var vm = NewVm(timer);

        vm.AppendLogForTest("立刻", LogKind.End);
        Assert.Empty(vm.LogLines);
        vm.FlushLogBatchNow();

        Assert.Equal(new[] { "立刻" }, vm.LogLines.Select(l => l.Text));
    }

    [Fact]
    public void 日志批量_上屏后触发一次Flushed事件_用于滚动()
    {
        var timer = new ManualUiTimer();
        var vm = NewVm(timer);
        var flushed = 0;
        vm.LogLinesFlushed += () => flushed++;

        vm.AppendLogForTest("a", LogKind.End);
        vm.AppendLogForTest("b", LogKind.End);
        Assert.Equal(0, flushed);
        timer.FireAll();
        Assert.Equal(1, flushed);                  // 整批只滚一次

        vm.FlushLogBatchNow();                     // 没有待上屏行 → 不再触发
        Assert.Equal(1, flushed);
    }

    // ── 搜索防抖 ──────────────────────────────────────────────────────

    [Fact]
    public void 搜索_行数不超过1000_立即生效_不排定时器()
    {
        var timer = new ManualUiTimer();
        var vm = NewVm(timer);
        vm.LibraryViewForTest.ReplaceAll(Rows(5, needleAt: -1));

        vm.LibrarySearchText = "App0003";

        Assert.False(vm.HasPendingSearchDebounce);   // 日志 flush 计时器可能另排一次，不能拿总数断言
        Assert.Single(vm.LibraryViewForTest.Build());
    }

    [Fact]
    public void 搜索_超过1000行_连续输入只在停手后按最后一次生效()
    {
        var timer = new ManualUiTimer();
        var vm = NewVm(timer);
        vm.LibraryViewForTest.ReplaceAll(Rows(1500, needleAt: 1499));

        vm.LibrarySearchText = "N";
        vm.LibrarySearchText = "Ne";
        vm.LibrarySearchText = "Needle";

        Assert.Equal(3, timer.ScheduleCount);      // 每次输入都排一次…
        Assert.Equal(1500, vm.LibraryViewForTest.Build().Count);   // …但都还没生效

        timer.FireAll();
        var built = vm.LibraryViewForTest.Build();
        Assert.Single(built);               // 只有最后一次生效
        Assert.Contains("Needle", built[0].Name);
    }

    [Fact]
    public void 搜索_超过1000行_清空也走防抖_恢复全量()
    {
        var timer = new ManualUiTimer();
        var vm = NewVm(timer);
        vm.LibraryViewForTest.ReplaceAll(Rows(1500, needleAt: -1));

        vm.LibrarySearchText = "App0001";
        timer.FireAll();
        Assert.Single(vm.LibraryViewForTest.Build());

        vm.LibrarySearchText = "";
        timer.FireAll();
        Assert.Equal(1500, vm.LibraryViewForTest.Build().Count);
    }

    // ── 帮助方法 ──────────────────────────────────────────────────────

    private static MainViewModel NewVm(IUiTimer timer) =>
        new(new FakeCliRunner(), new FakeAfcHelper(), uiTimer: timer);

    private static List<PackageRow> Rows(int count, int needleAt)
    {
        var rows = new List<PackageRow>(count);
        for (var i = 0; i < count; i++)
        {
            var name = i == needleAt ? "Needle" : $"App{i:0000}";
            rows.Add(new PackageRow
            {
                Id = i + 1,
                FilePath = $"C:\\fake\\{name}.ipa",
                FileName = name + ".ipa",
                DisplayName = name,
                BundleId = $"com.test.{i:0000}",
                Version = "1.0",
                Build = "1.0",
                MinOs = "4.0",
                DeviceFamily = "iPhone",
                Arch = "armv7",
                AppSize = 100,
                FileSize = 50,
                ParseState = ParseStates.Ok,
                ExistsFlag = true,
            });
        }

        return rows;
    }

    [Fact]
    public void 搜索防抖阈值与延迟_可由设置修改()
    {
        var timer = new ManualUiTimer();
        var vm = NewVm(timer);
        vm.LibraryViewForTest.ReplaceAll(Rows(5, needleAt: -1));

        var draft = vm.CaptureSettings();
        draft.SearchDebounceRows = 3;
        draft.SearchDebounceMs = 50;
        vm.ApplySettings(draft, persist: false);

        Assert.Equal(3, vm.SearchDebounceRowsForTest);
        Assert.Equal(50, (int)vm.SearchDebounceDelayForTest.TotalMilliseconds);

        vm.LibrarySearchText = "App0003";
        Assert.True(vm.HasPendingSearchDebounce);   // 5 行 > 阈值 3 → 延迟
        timer.FireAll();
        Assert.Single(vm.LibraryViewForTest.Build());
    }

    [Fact]
    public void 日志清空_只清内存_连待上屏的一起丢()
    {
        var timer = new ManualUiTimer();
        var vm = NewVm(timer);

        vm.AppendLogForTest("已上屏", LogKind.End);
        timer.FireAll();
        Assert.Single(vm.LogLines);

        vm.AppendLogForTest("还在 50ms 批量里", LogKind.Detail);
        vm.ClearLog();

        Assert.Empty(vm.LogLines);
        Assert.Empty(vm.AllLogLines);

        vm.FlushLogBatchNow();          // 清空后再刷，不应把待上屏行放回来
        Assert.Empty(vm.LogLines);
    }
}

/// <summary>测试用计时器：只登记，不自动执行；手动 FireAll 推进"时钟"。</summary>
internal sealed class ManualUiTimer : IUiTimer
{
    private readonly List<Entry> _entries = new();

    public int ScheduleCount { get; private set; }

    public IDisposable ScheduleOnce(TimeSpan delay, Action action)
    {
        ScheduleCount++;
        var entry = new Entry(action);
        _entries.Add(entry);
        return entry;
    }

    /// <summary>触发所有尚未取消的计时器（按登记顺序）。</summary>
    public void FireAll()
    {
        foreach (var entry in _entries.ToList())
        {
            if (!entry.Disposed) entry.Fire();
        }
    }

    internal sealed class Entry : IDisposable
    {
        private readonly Action _action;
        public Entry(Action action) => _action = action;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
        public void Fire() => _action();
    }
}
