using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using IdeviceGui.Resources;
using IdeviceGui.Services;
using IdeviceGui.Services.Data;
using IdeviceGui.Services.Scanning;
using IdeviceGui.ViewModels;
using IdeviceGui.ViewModels.Library;
using IdeviceGui.Views;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// UI/VM 审计修复回归测试（2026-11-23）：覆盖 P1-5..P1-7, P2-8, P2-10, P2-12, P2-13, P3-3..P3-5。
/// </summary>
public sealed class UiAuditFixTests
{
    private static PackageRow MakePackage(
        long id,
        string name,
        string bundleId,
        string arch = "armv7",
        string parseState = ParseStates.Ok,
        string? parseError = null,
        bool exists = true) => new()
        {
            Id = id,
            FilePath = $@"C:\fake\{id}_{bundleId}.ipa",
            FileName = $"{id}_{bundleId}.ipa",
            DisplayName = name,
            BundleId = bundleId,
            Version = "1.0",
            Build = "1",
            MinOs = "4.0",
            DeviceFamily = "iPhone",
            Arch = arch,
            AppSize = 100L * 1024 * 1024,
            FileSize = 50L * 1024 * 1024,
            ParseState = parseState,
            ParseError = parseError,
            ExistsFlag = exists,
        };

    [Fact]
    public void P2_8_高级筛选选项与行投影规范键在中英文下全部对齐()
    {
        var rowNormal = new LibraryRow { Id = 1 };
        rowNormal.UpdateFrom(MakePackage(1, "NormalApp", "com.test.normal", arch: "armv7", parseState: ParseStates.Ok));

        var rowPending = new LibraryRow { Id = 2 };
        rowPending.UpdateFrom(MakePackage(2, "PendingApp", "com.test.pending", arch: "", parseState: ParseStates.Pending));

        var rowFailed = new LibraryRow { Id = 3 };
        rowFailed.UpdateFrom(MakePackage(3, "FailedApp", "com.test.failed", arch: "armv6, armv7", parseState: ParseStates.Failed, parseError: "bad zip header"));

        var rowMissing = new LibraryRow { Id = 4 };
        rowMissing.UpdateFrom(MakePackage(4, "MissingApp", "com.test.missing", arch: "arm64", parseState: ParseStates.Ok, exists: false));

        foreach (var cultureName in new[] { "zh-CN", "en" })
        {
            using var _ = Strings.UseCultureScope(CultureInfo.GetCultureInfo(cultureName));

            var stateOptions = AdvancedFilterWindow.GetStateOptions();
            var archOptions = AdvancedFilterWindow.GetArchOptions();

            Assert.Equal("未解析", rowPending.StateFilterKey);
            Assert.Equal("解析未完成", rowFailed.StateFilterKey);
            Assert.Equal("文件缺失", rowMissing.StateFilterKey);
            Assert.Equal("正常", rowNormal.StateFilterKey);

            // Pending option aligns with LibraryRow.StateFilterKey ("未解析") and legacy "待解析".
            var pendingKey = Assert.Single(stateOptions, o => o.Label == Strings.Filter_State_Pending).Value;
            Assert.Equal("未解析", pendingKey);
            var pendingFilter = new AdvancedFilterState
            {
                Enabled = true,
                Groups = { new AdvancedFilterGroup { Conditions = { new AdvancedFilterCondition { Column = AdvancedFilterColumn.State, Selected = { pendingKey } } } } },
            };
            Assert.True(pendingFilter.Matches(rowPending, null, null, null));
            Assert.False(pendingFilter.Matches(rowNormal, null, null, null));

            var legacyPendingFilter = new AdvancedFilterState
            {
                Enabled = true,
                Groups = { new AdvancedFilterGroup { Conditions = { new AdvancedFilterCondition { Column = AdvancedFilterColumn.State, Selected = { "待解析" } } } } },
            };
            Assert.True(legacyPendingFilter.Matches(rowPending, null, null, null));

            // ParseFailed option matches even when ParseError is non-empty.
            var failedKey = Assert.Single(stateOptions, o => o.Label == Strings.Filter_State_ParseFailed).Value;
            var failedFilter = new AdvancedFilterState
            {
                Enabled = true,
                Groups = { new AdvancedFilterGroup { Conditions = { new AdvancedFilterCondition { Column = AdvancedFilterColumn.State, Selected = { failedKey } } } } },
            };
            Assert.True(failedFilter.Matches(rowFailed, null, null, null));
            Assert.False(failedFilter.Matches(rowNormal, null, null, null));

            // Multi-arch ("多种") matches fat binaries ("v6, v7") and unknown ("—") matches empty arch.
            var multiArchKey = Assert.Single(archOptions, o => o.Label == Strings.Filter_Arch_Multiple).Value;
            var multiArchFilter = new AdvancedFilterState
            {
                Enabled = true,
                Groups = { new AdvancedFilterGroup { Conditions = { new AdvancedFilterCondition { Column = AdvancedFilterColumn.Arch, Selected = { multiArchKey } } } } },
            };
            Assert.True(multiArchFilter.Matches(rowFailed, null, null, null));
            Assert.False(multiArchFilter.Matches(rowNormal, null, null, null));

            var unknownArchFilter = new AdvancedFilterState
            {
                Enabled = true,
                Groups = { new AdvancedFilterGroup { Conditions = { new AdvancedFilterCondition { Column = AdvancedFilterColumn.Arch, Selected = { "—" } } } } },
            };
            Assert.True(unknownArchFilter.Matches(rowPending, null, null, null));
            Assert.False(unknownArchFilter.Matches(rowNormal, null, null, null));
        }
    }

    [Fact]
    public void P2_10_节流期间触发尾随延迟刷新_最后批次不会丢上屏()
    {
        var timer = new ManualUiTimer();
        var vm = new MainViewModel(new FakeCliRunner(), new FakeAfcHelper(), uiTimer: timer, pipeNameOverride: "");

        // 第一批：无论 Stopwatch 刚启动是否 <150 ms，FireAll 后第一批必已上屏且 _lastLibraryUiRefreshMs 已更新为最近时间戳
        vm.ApplyBatchRowsForTest([MakePackage(1, "First", "com.test.first")]);
        timer.FireAll();
        Assert.Single(vm.LibraryRows);

        // 紧接着 150 ms 内再来第二批：必然命中节流并登记 1 次尾随刷新定时器
        var schedulesBefore = timer.ScheduleCount;
        vm.ApplyBatchRowsForTest([MakePackage(2, "Second", "com.test.second")]);
        Assert.Equal(schedulesBefore + 1, timer.ScheduleCount);
        Assert.Single(vm.LibraryRows);

        timer.FireAll();
        Assert.Equal(2, vm.LibraryRows.Count);
        Assert.Contains(vm.LibraryRows, r => r.BundleId == "com.test.second");
    }

    [Fact]
    public void P2_12_设备队列空间预测_仅在飞开始早于SpaceCapturedUtc时视为已扣减()
    {
        const long gb = 1024L * 1024 * 1024;
        var queue = new QueueService(new FakeCliRunner(), new FakeAfcHelper(), null, new RecordingProgress<QueueEvent>(), new RecordingProgress<LogLine>());
        var device = new DeviceInfo { Udid = "space-udid", Name = "iPhone" };
        var capturedAt = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        device.SetSpace(new DiskUsage(true, 10 * gb, 5 * gb, capturedAt)); // 5 GB used / 10 GB total

        var qvm = new DeviceQueueViewModel(queue, device);
        var item = new QueueItem
        {
            Udid = "space-udid",
            Kind = JobKind.Install,
            BundleId = "com.test.inflight",
            FilePath = @"C:\fake\a.ipa",
            ExpectedUncompressedBytes = 2 * gb,
            PeakBytes = 3 * gb,
            FinalBytes = 2 * gb,
            Status = QueueStatus.Installing,
        };

        var jvm = qvm.AddJob(item);
        // 在飞任务开始时间晚于 SpaceCapturedUtc：说明容量快照尚未包含它，必须计入预测（5 + 2 = 7 GB）
        jvm.InFlightStartedUtc = capturedAt.AddSeconds(5);
        qvm.RecomputeProgress();
        Assert.Equal("7/10GB", qvm.SpaceAfterText);

        // 刷新容量到在飞任务开始之后：此时视为已扣减，不再重复累加（5 GB）
        device.SetSpace(new DiskUsage(true, 10 * gb, 5 * gb, capturedAt.AddSeconds(10)));
        Assert.Equal("5/10GB", qvm.SpaceAfterText);
    }

    [Fact]
    public void P2_12_入队闸门_考虑在飞时间戳与队列顺序模拟()
    {
        const long mb = 1024L * 1024;
        var capturedAt = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

        // 1) 在飞任务开始于 SpaceCapturedUtc 之后：仍需计入扣减
        var inFlight = new QueueItem
        {
            Udid = "u1",
            Kind = JobKind.Install,
            BundleId = "com.test.running",
            PeakBytes = 100 * mb,
            FinalBytes = 60 * mb,
            ExpectedUncompressedBytes = 60 * mb,
            Status = QueueStatus.Installing,
        };
        var startedMap = new Dictionary<Guid, DateTimeOffset?> { [inFlight.Id] = capturedAt.AddSeconds(2) };
        // free=169 MB：扣去在飞 60 MB 后剩 109 MB，不够新包 100 MB + 10 MB 余量
        Assert.False(MainViewModel.CheckEnqueueSpace(
            169 * mb, capturedAt, [inFlight], startedMap, "com.test.new", 100 * mb, 50 * mb, out var required));
        Assert.Equal(170 * mb, required);

        // 若快照采集于在飞开始之后：已扣减，169 MB 足够新包 100 + 10 = 110 MB
        Assert.True(MainViewModel.CheckEnqueueSpace(
            169 * mb, capturedAt.AddSeconds(5), [inFlight], startedMap, "com.test.new", 100 * mb, 50 * mb, out _));

        // 2) 队列执行序模拟：新包 Peak=200 MB 比原 Pending 包 Peak=100 MB 大，新包先装（200+10<=250），应放行
        var smallPending = new QueueItem
        {
            Udid = "u1",
            Kind = JobKind.Install,
            BundleId = "com.test.small",
            PeakBytes = 100 * mb,
            FinalBytes = 50 * mb,
            ExpectedUncompressedBytes = 50 * mb,
            OrderIndex = 0,
            Status = QueueStatus.Pending,
        };
        Assert.True(MainViewModel.CheckEnqueueSpace(
            250 * mb, capturedAt, [smallPending], new Dictionary<Guid, DateTimeOffset?>(), "com.test.big", 200 * mb, 100 * mb, out _));

        // 3) 若队列前已有装不下的超大 Pending（Peak=300 MB > 250 MB），它不会执行也不扣空间，小新包（Peak=100 MB）仍可入队
        var tooBigPending = new QueueItem
        {
            Udid = "u1",
            Kind = JobKind.Install,
            BundleId = "com.test.toobig",
            PeakBytes = 300 * mb,
            FinalBytes = 150 * mb,
            ExpectedUncompressedBytes = 150 * mb,
            OrderIndex = 0,
            Status = QueueStatus.Pending,
        };
        Assert.True(MainViewModel.CheckEnqueueSpace(
            250 * mb, capturedAt, [tooBigPending], new Dictionary<Guid, DateTimeOffset?>(), "com.test.fits", 100 * mb, 50 * mb, out _));
    }

    [Fact]
    public void P3_3与P3_4_多选展开组与全选计数排除缺失文件()
    {
        var vm = new MainViewModel(new FakeCliRunner(), new FakeAfcHelper(), pipeNameOverride: "");
        vm.LibraryViewForTest.ReplaceAll([
            MakePackage(1, "Same", "com.test.same"),
            MakePackage(2, "Same", "com.test.same"),
            MakePackage(3, "Missing", "com.test.missing", exists: false),
        ]);
        vm.ShowMissingFiles = false;
        vm.ToggleMultiSelectMode();

        var groupRow = Assert.Single(vm.LibraryRows, r => r.IsGroup);
        Assert.True(groupRow.IsCollapsed);

        // P3-3: 多选模式下展开组，组行展开成员上屏
        vm.ToggleLibraryGroup(groupRow);
        Assert.False(groupRow.IsCollapsed);
        Assert.Equal(3, vm.LibraryRows.Count); // 1 group + 2 visible members (missing hidden)

        // 再收起组，使 2 个成员处于隐藏（但非缺失）状态
        vm.ToggleLibraryGroup(groupRow);
        Assert.True(groupRow.IsCollapsed);

        // P3-4: 全选可见行（0 个可见文件），日志应排除缺失文件且不把未选中写成"已选中项不可见"
        vm.SelectAllVisibleLibraryRows();
        var logText = Assert.Single(vm.AllLogLines, l => l.Text.Contains("全选")).Text;
        Assert.Contains("另有 2 个匹配项当前不可见", logText);
        Assert.DoesNotContain("已选中项不可见", logText);
    }

    [Fact]
    public void P3_5_DeviceProductTypes无中文混杂且ModelLabel支持双语()
    {
        Assert.Equal(182, DeviceProductTypes.Names.Count);
        foreach (var kv in DeviceProductTypes.Names)
        {
            Assert.False(
                kv.Value.Any(ch => ch is >= '\u4e00' and <= '\u9fff'),
                $"Unexpected CJK character in DeviceProductTypes.Names[{kv.Key}]: {kv.Value}");
        }

        using (Strings.UseCultureScope(CultureInfo.GetCultureInfo("en")))
        {
            Assert.Equal("iPhone SE (1st generation)(iPhone8,4)", DeviceScreen.ModelLabel(null, "iPhone8,4"));
            Assert.Equal("iPad mini 1 Cellular (China)(iPad2,7)", DeviceScreen.ModelLabel(null, "iPad2,7"));
        }

        using (Strings.UseCultureScope(CultureInfo.GetCultureInfo("zh-CN")))
        {
            Assert.Equal("iPhone SE (第 1 代)(iPhone8,4)", DeviceScreen.ModelLabel(null, "iPhone8,4"));
            Assert.Equal("iPad mini 1 Cellular（中国大陆）(iPad2,7)", DeviceScreen.ModelLabel(null, "iPad2,7"));
        }
    }

    [Fact]
    public void P3_UI_新1_隐藏已选组成员经RefreshLibraryRows追加后清除组缩进与底线()
    {
        var vm = new MainViewModel(new FakeCliRunner(), new FakeAfcHelper(), pipeNameOverride: "");
        vm.LibraryViewForTest.ReplaceAll([
            MakePackage(1, "GroupApp", "com.test.group"),
            MakePackage(2, "GroupApp", "com.test.group"),
            MakePackage(3, "KeepVisible", "com.test.keep"),
        ]);
        vm.ToggleMultiSelectMode();

        var groupRow = Assert.Single(vm.LibraryRows, r => r.IsGroup);
        vm.ToggleLibraryGroup(groupRow);

        var members = vm.LibraryRows.Where(r => r.IsGroupMember).ToList();
        Assert.Equal(2, members.Count);
        Assert.True(members[^1].IsGroupEnd);

        // 选中最后一个成员行（原本 IsGroupMember=true 且 IsGroupEnd=true），再搜索过滤将其隐藏
        vm.ToggleLibraryMultiSelect(members[^1]);
        vm.LibrarySearchText = "KeepVisible";

        var appended = Assert.Single(vm.LibraryRows, r => r.Id == members[^1].Id);
        Assert.Contains(vm.LibraryRows, r => r.IsAppendedSeparator);
        Assert.False(appended.IsGroupMember);
        Assert.False(appended.IsGroupEnd);
        Assert.DoesNotContain("└", appended.NameText);
    }
}

[Collection("ProcessEnv")]
public sealed class UiAuditEnvTests
{
    [Fact]
    public async Task P1_5_300ms实时预览后确定_七项设置全部持久化到SQLite()
    {
        using var ws = new TestWorkspace("p1-5");
        var prevData = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryEnvVar);
        Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, ws.Root);
        try
        {
            var vm = new MainViewModel(new FakeCliRunner(), new FakeAfcHelper(), pipeNameOverride: "");
            await vm.OpenDatabaseForTestAsync();

            var draft = vm.CaptureSettings();
            draft.LogToFile = true;
            draft.LogDetailMode = true;
            draft.PollSeconds = 7;
            draft.AutoResume = false;
            draft.ReconnectStableSeconds = 9;
            draft.AutoRetryAttempts = 4;
            draft.DeviceFields = draft.DeviceFields.Reverse()
                .Select((f, idx) => new DeviceInfoFieldSetting(f.Field, !f.Visible, idx))
                .ToList();

            // 模拟 300 ms 实时预览先修改内存
            vm.ApplySettings(draft, persist: false);
            // 再点确定 / 应用持久化
            vm.ApplySettings(draft, persist: true);
            await vm.FlushSettingsAsync();
            await vm.ShutdownAsync();

            using var verifyDb = AppDatabase.Open(System.IO.Path.Combine(ws.Root, "library.db"));
            Assert.True(SettingsStore.GetBool(verifyDb, SettingsStore.LogToFile, false));
            Assert.Equal("detail", SettingsStore.GetString(verifyDb, SettingsStore.LogPanelMode));
            Assert.Equal("7", SettingsStore.GetString(verifyDb, "Device.PollSeconds"));
            Assert.False(SettingsStore.GetBool(verifyDb, "Device.AutoResumeAfterReconnect", true));
            Assert.Equal("9", SettingsStore.GetString(verifyDb, "Device.ReconnectStableSeconds"));
            Assert.Equal("4", SettingsStore.GetString(verifyDb, "Queue.AutoRetryAttempts"));
            Assert.Equal(
                DeviceInfoFields.Serialize(draft.DeviceFields),
                SettingsStore.GetString(verifyDb, DeviceInfoFields.SettingsKey));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, prevData);
        }
    }

    [Fact]
    public async Task P1_6_连续FlushSettingsAsync串行落库不丢写()
    {
        using var ws = new TestWorkspace("p1-6");
        var prevData = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryEnvVar);
        Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, ws.Root);
        try
        {
            var vm = new MainViewModel(new FakeCliRunner(), new FakeAfcHelper(), pipeNameOverride: "");
            await vm.OpenDatabaseForTestAsync();

            for (var i = 1; i <= 5; i++)
            {
                var d = vm.CaptureSettings();
                d.PollSeconds = Math.Clamp(i, 1, 10);
                vm.ApplySettings(d, persist: true);
            }

            await vm.FlushSettingsAsync();
            await vm.ShutdownAsync();

            using var verifyDb = AppDatabase.Open(System.IO.Path.Combine(ws.Root, "library.db"));
            Assert.Equal("5", SettingsStore.GetString(verifyDb, "Device.PollSeconds"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, prevData);
        }
    }

    [Fact]
    public async Task P2_13_DeleteScanRootAsync先等待在跑扫描结束再删库根()
    {
        using var ws = new TestWorkspace("p2-13");
        var prevData = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryEnvVar);
        Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, ws.Root);
        try
        {
            var vm = new MainViewModel(new FakeCliRunner(), new FakeAfcHelper(), scanRootOverride: ws.Root, pipeNameOverride: "");
            await vm.OpenDatabaseForTestAsync();

            var db = vm.DatabaseForTest!;
            var (addedRoot, error) = ScanRootStore.Add(db, ws.Root);
            Assert.Null(error);
            Assert.NotNull(addedRoot);

            var scanGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.SetActiveScanTaskForTest(scanGate.Task);

            var deleteTask = vm.DeleteScanRootAsync(addedRoot!);
            await Task.Delay(100, TestContext.Current.CancellationToken);

            // 扫描未完成前，扫描根不能被删库
            Assert.False(deleteTask.IsCompleted);
            Assert.Single(ScanRootStore.List(db));

            scanGate.SetResult();
            var (ok, delError) = await deleteTask;
            Assert.True(ok, delError);
            Assert.Empty(ScanRootStore.List(db));

            await vm.ShutdownAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, prevData);
        }
    }

    [Fact]
    public async Task I4_安全开关_默认关闭且支持草稿克隆重置与持久化()
    {
        var draft = new SettingsDraft();
        Assert.False(draft.AllowOutsideProgramDirWrite);

        draft.AllowOutsideProgramDirWrite = true;
        var clone = draft.Clone();
        Assert.True(clone.AllowOutsideProgramDirWrite);
        clone.AllowOutsideProgramDirWrite = false;
        Assert.True(draft.AllowOutsideProgramDirWrite);

        draft.ResetGroup(SettingsGroup.Security);
        Assert.False(draft.AllowOutsideProgramDirWrite);

        using var ws = new TestWorkspace("i4-security");
        var prevData = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryEnvVar);
        Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, ws.Root);
        try
        {
            var vm = new MainViewModel(new FakeCliRunner(), new FakeAfcHelper(), pipeNameOverride: "");
            await vm.OpenDatabaseForTestAsync();
            Assert.False(vm.CaptureSettings().AllowOutsideProgramDirWrite);

            var edited = vm.CaptureSettings();
            edited.AllowOutsideProgramDirWrite = true;
            vm.ApplySettings(edited, persist: false);
            Assert.True(vm.CaptureSettings().AllowOutsideProgramDirWrite);

            // 未持久化前回滚，不应写入库
            var rollback = vm.CaptureSettings();
            rollback.AllowOutsideProgramDirWrite = false;
            vm.ApplySettings(rollback, persist: false);
            await vm.FlushSettingsAsync();

            using (var dbBefore = AppDatabase.Open(System.IO.Path.Combine(ws.Root, "library.db")))
            {
                Assert.False(SettingsStore.GetBool(dbBefore, SettingsDraft.AllowOutsideProgramDirWriteKey, false));
            }

            // 确认应用后应写入 SQLite Settings 表
            edited.AllowOutsideProgramDirWrite = true;
            vm.ApplySettings(edited, persist: true);
            await vm.FlushSettingsAsync();
            await vm.ShutdownAsync();

            using var dbAfter = AppDatabase.Open(System.IO.Path.Combine(ws.Root, "library.db"));
            Assert.True(SettingsStore.GetBool(dbAfter, SettingsDraft.AllowOutsideProgramDirWriteKey, false));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, prevData);
        }
    }

    [Fact]
    public async Task Scan_SkipStartupScan开关_开启时InitializeAsync跳过扫盘直接加载库_关闭时正常扫盘()
    {
        var prevData = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryEnvVar);

        // 1) 开启 SkipStartupScan：磁盘有未入库 IPA，但 DB 里只有另一条既有记录 → 只加载 DB 记录，不扫盘
        using (var wsSkip = new TestWorkspace("skip-startup-on"))
        {
            wsSkip.AddIpa("disk-only.ipa", "com.disk.only", displayName: "DiskOnly");
            using (var seedDb = AppDatabase.Open(System.IO.Path.Combine(wsSkip.Root, "library.db")))
            {
                SettingsStore.SetString(seedDb, SettingsDraft.SkipStartupScanKey, "true");
                var (addedRoot, rootErr) = ScanRootStore.Add(seedDb, wsSkip.Root);
                Assert.Null(rootErr);
                var dbPkg = new PackageRow
                {
                    Id = 0,
                    ScanRootId = addedRoot!.Id,
                    FilePath = wsSkip.Full("already-in-db.ipa"),
                    FileName = "already-in-db.ipa",
                    DisplayName = "DbOnly",
                    BundleId = "com.db.only",
                    Version = "1.0",
                    Build = "1",
                    MinOs = "4.0",
                    DeviceFamily = "iPhone",
                    Arch = "armv7",
                    AppSize = 100L * 1024 * 1024,
                    FileSize = 50L * 1024 * 1024,
                    ParseState = ParseStates.Ok,
                    ExistsFlag = true,
                };
                PackageStore.Save(seedDb, [dbPkg]);
            }

            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, wsSkip.Root);
            MainViewModel? vm = null;
            try
            {
                vm = new MainViewModel(new FakeCliRunner(), new FakeAfcHelper(), scanRootOverride: wsSkip.Root, pipeNameOverride: "");
                await vm.InitializeAsync();

                Assert.True(vm.CaptureSettings().SkipStartupScan);
                Assert.Contains(vm.LibraryRows, r => r.BundleId == "com.db.only");
                Assert.DoesNotContain(vm.LibraryRows, r => r.BundleId == "com.disk.only");
                Assert.Contains(vm.AllLogLines, l => l.Text.Contains("已跳过启动增量扫描"));
            }
            finally
            {
                if (vm is not null) await vm.ShutdownAsync();
                Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, prevData);
            }
        }

        // 2) 默认关闭 SkipStartupScan：启动照常扫盘，磁盘 IPA 入库上屏
        using (var wsScan = new TestWorkspace("skip-startup-off"))
        {
            wsScan.AddIpa("disk-only.ipa", "com.disk.only", displayName: "DiskOnly");
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, wsScan.Root);
            MainViewModel? vm = null;
            try
            {
                vm = new MainViewModel(new FakeCliRunner(), new FakeAfcHelper(), scanRootOverride: wsScan.Root, pipeNameOverride: "");
                await vm.InitializeAsync();

                Assert.False(vm.CaptureSettings().SkipStartupScan);
                Assert.Contains(vm.LibraryRows, r => r.BundleId == "com.disk.only");
            }
            finally
            {
                if (vm is not null) await vm.ShutdownAsync();
                Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, prevData);
            }
        }
    }

    [Fact]
    public async Task ShutdownAsync_ReportsClosingSteps_AndSelectedDeviceChangingFiresOldUdid()
    {
        var prevData = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryEnvVar);
        using var ws = new TestWorkspace("closing-and-scroll-udid");
        Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, ws.Root);
        var cli = new FakeCliRunner { Udids = new[] { "udid-a", "udid-b" } };
        var vm = new MainViewModel(cli, new FakeAfcHelper(), scanRootOverride: ws.Root, pipeNameOverride: "");
        try
        {
            await vm.InitializeAsync();
            Assert.Equal(2, vm.Devices.Count);
            var changingFrom = new List<string?>();
            vm.SelectedDeviceChanging += udid => changingFrom.Add(udid);

            vm.SelectedDevice = vm.Devices[0];
            vm.SelectedDevice = vm.Devices[1];
            Assert.Contains("udid-a", changingFrom);

            var steps = new List<string>();
            await vm.ShutdownAsync(step => steps.Add(step));
            Assert.Contains(Strings.Main_Closing_Step_StoppingQueues, steps);
            Assert.Contains(Strings.Main_Closing_Step_KillingProcesses, steps);
        }
        finally
        {
            await vm.ShutdownAsync();
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, prevData);
        }
    }

    [Fact]
    public async Task PollDevicesAsync_IncrementalRefresh_SkipsAlreadyOnlineDevice_AndAdaptsInterval()
    {
        var prevData = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryEnvVar);
        using var ws = new TestWorkspace("incremental-device-poll");
        Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, ws.Root);
        var cli = new FakeCliRunner { Udids = new[] { "udid-1" } };
        var vm = new MainViewModel(cli, new FakeAfcHelper(), scanRootOverride: ws.Root, pipeNameOverride: "");
        try
        {
            await vm.InitializeAsync();
            Assert.Single(vm.Devices);
            Assert.Equal(1, cli.ProbeCalls);

            // 仅新增 udid-2 时，增量刷新只探测 udid-2，不重探本来在线的 udid-1
            cli.Udids = new[] { "udid-1", "udid-2" };
            await vm.PollDevicesAsync();
            Assert.Equal(2, vm.Devices.Count);
            Assert.Equal(2, cli.ProbeCalls);

            // 手动全量刷新会重探两台在线设备
            await vm.RefreshDevicesForInstallDialogAsync();
            Assert.Equal(4, cli.ProbeCalls);

            // 轮询频率自适应：无挂钩 2s -> 有 WM_DEVICECHANGE 挂钩 10s -> 队列运行中 15s
            Assert.Equal(TimeSpan.FromSeconds(2), vm.ComputeEffectivePollInterval());
            vm.SetUsbEventHookActive(true);
            Assert.Equal(TimeSpan.FromSeconds(10), vm.ComputeEffectivePollInterval());
            vm.Devices[0].Apply(new DeviceRunStateEvent("udid-1", QueueRunState.Running, ""));
            Assert.Equal(TimeSpan.FromSeconds(15), vm.ComputeEffectivePollInterval());
            vm.Devices[0].Apply(new DeviceRunStateEvent("udid-1", QueueRunState.Idle, ""));
            Assert.Equal(TimeSpan.FromSeconds(10), vm.ComputeEffectivePollInterval());
        }
        finally
        {
            await vm.ShutdownAsync();
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, prevData);
        }
    }

    [Fact]
    public async Task StatusBar_ShowsAlways_ProtectsStickyError_RecordsNewestFirstHistory_AndProbesFileOffUi()
    {
        var prevData = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryEnvVar);
        using var ws = new TestWorkspace("status-history-and-probe");
        Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, ws.Root);
        var existingPath = ws.AddIpa("valid.ipa", "com.test.valid", displayName: "Valid");
        var missingPath = System.IO.Path.Combine(ws.Root, "missing.ipa");
        var vm = new MainViewModel(new FakeCliRunner(), new FakeAfcHelper(), scanRootOverride: ws.Root, pipeNameOverride: "");
        try
        {
            Assert.True(vm.ShowBottomStatusBar);

            vm.ShowError("critical-err");
            vm.ShowStatus("routine-ok");

            Assert.True(vm.StatusIsError);
            Assert.Equal("critical-err", vm.StatusMessage);
            Assert.Equal(2, vm.StatusHistory.Count);
            Assert.Equal("routine-ok", vm.StatusHistory[0].Message);
            Assert.False(vm.StatusHistory[0].IsError);
            Assert.Equal("critical-err", vm.StatusHistory[1].Message);
            Assert.True(vm.StatusHistory[1].IsError);
            Assert.Contains("routine-ok", vm.StatusHoverSummary);
            Assert.Contains("critical-err", vm.StatusHoverSummary);

            Assert.Equal(MainViewModel.FileReachability.Reachable, await vm.ProbeFileReachableAsync(existingPath));
            Assert.Equal(MainViewModel.FileReachability.Missing, await vm.ProbeFileReachableAsync(missingPath));
        }
        finally
        {
            await vm.ShutdownAsync();
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, prevData);
        }
    }

    [Fact]
    public async Task HasSelectedDevice_TracksSelection_AndGroupFilterOptionsPreserveDefaultSelection()
    {
        var prevData = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryEnvVar);
        using var ws = new TestWorkspace("no-device-and-group-default");
        Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, ws.Root);
        var cli = new FakeCliRunner { Udids = Array.Empty<string>() };
        var vm = new MainViewModel(cli, new FakeAfcHelper(), scanRootOverride: ws.Root, pipeNameOverride: "");
        try
        {
            await vm.InitializeAsync();
            Assert.False(vm.HasSelectedDevice);
            Assert.Contains(vm.GroupFilterOptions, g => g.IsSelectable && string.Equals(g.Name, vm.ActiveGroupName, StringComparison.Ordinal));

            var changedProps = new List<string>();
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is not null) changedProps.Add(e.PropertyName);
            };

            cli.Udids = new[] { "udid-ui-polish" };
            await vm.PollDevicesAsync();
            Assert.True(vm.HasSelectedDevice);
            Assert.Contains(nameof(MainViewModel.HasSelectedDevice), changedProps);

            changedProps.Clear();
            vm.SelectedDevice = null;
            Assert.False(vm.HasSelectedDevice);
            Assert.Contains(nameof(MainViewModel.HasSelectedDevice), changedProps);
        }
        finally
        {
            await vm.ShutdownAsync();
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, prevData);
        }
    }
}
