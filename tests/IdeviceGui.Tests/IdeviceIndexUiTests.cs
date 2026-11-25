using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using IdeviceIndex;
using Microsoft.Data.Sqlite;
using Xunit;
using IndexDoubleClickState = IdeviceIndex.DoubleClickState;
using IndexMultiSelectRange = IdeviceIndex.MultiSelectRange;
using IndexStrings = IdeviceIndex.Resources.Strings;

namespace IdeviceGui.Tests;

[Collection("WpfUiSmoke")]
public sealed class IdeviceIndexUiTests
{
    private static IndexRow SampleRow(string id = "id-1", string title = "Demo App", string bundleId = "com.example.demo") => new()
    {
        Id = id,
        BundleId = bundleId,
        Title = title,
        Version = "1.2.3",
        MinOs = "6.0",
        Size = 2 * 1024 * 1024,
        SizeExact = true,
        DeviceMask = 2 | 4,
        SourceCount = 2,
        Category = "Games",
    };

    [Theory]
    [InlineData(null, 1000L, false)]
    [InlineData(1000L, 1000L, true)]   // 0 ms
    [InlineData(1000L, 1400L, true)]   // 400 ms exact boundary
    [InlineData(1000L, 1401L, false)]  // 401 ms expired
    [InlineData(1000L, 999L, false)]   // negative delta
    public void DoubleClickState_BoundaryChecks(long? lastClickMs, long nowMs, bool expected)
    {
        Assert.Equal(400, IndexDoubleClickState.WindowMs);
        Assert.Equal(expected, IndexDoubleClickState.IsSecondClick(lastClickMs, nowMs));
    }

    [Fact]
    public void MultiSelectRange_XorDrag_TogglesRangeRelativeToBaseSelection()
    {
        var r0 = SampleRow("0");
        var r1 = SampleRow("1");
        var r2 = SampleRow("2");
        var r3 = SampleRow("3");
        var r4 = SampleRow("4");
        r1.IsMultiSelected = true;
        r4.IsMultiSelected = true;

        var rows = new[] { r0, r1, r2, r3, r4 };
        var baseSelection = new HashSet<IndexRow> { r1, r4 };

        // Drag upward from r2 to r0 -> range [0..2] gets XORed against {r1, r4}
        var ok = IndexMultiSelectRange.Apply(rows, anchor: r2, current: r0, baseSelection, skip: _ => false);
        Assert.True(ok);
        Assert.True(r0.IsMultiSelected);
        Assert.False(r1.IsMultiSelected); // was in base, XOR range -> deselected
        Assert.True(r2.IsMultiSelected);
        Assert.False(r3.IsMultiSelected);
        Assert.True(r4.IsMultiSelected);  // outside range, preserved

        // Skip predicate leaves row untouched
        var skippedOk = IndexMultiSelectRange.Apply(rows, anchor: r0, current: r4, baseSelection, skip: r => r.Id == "2");
        Assert.True(skippedOk);
        Assert.True(r2.IsMultiSelected);

        // Missing anchor returns false
        Assert.False(IndexMultiSelectRange.Apply(rows, SampleRow("missing"), r1, baseSelection, _ => false));
    }

    [Fact]
    public void DownloadQueueColumns_Defaults_AllHiddenFallback_Format_AndJsonRoundTrip()
    {
        var defaults = DownloadQueueColumns.Default;
        Assert.Equal(10, defaults.Count);
        var defaultVisible = DownloadQueueColumns.OrderedVisible(defaults);
        Assert.Equal(
            [
                DownloadQueueColumn.Status,
                DownloadQueueColumn.Note,
                DownloadQueueColumn.Name,
                DownloadQueueColumn.Version,
                DownloadQueueColumn.Size,
            ],
            defaultVisible);

        // All hidden falls back to Status
        var allHidden = defaults.Select(s => s with { Visible = false }).ToList();
        Assert.Equal([DownloadQueueColumn.Status], DownloadQueueColumns.OrderedVisible(allHidden));

        // Format hides Note for Succeeded/Cancelled, shows for Pending/Paused/Failed
        var row = SampleRow("q1", "Test IPA", "com.test.ipa");
        var job = new DownloadJobRow(row, defaultVisible, sourceSummary: "src1", targetPath: @"D:\dl\test.ipa", note: "waiting");
        var pendingDisplay = DownloadQueueColumns.Format(job, defaultVisible);
        Assert.Contains("waiting", pendingDisplay, StringComparison.Ordinal);
        Assert.Contains("Test IPA", pendingDisplay, StringComparison.Ordinal);

        job.Status = DownloadJobStatus.Succeeded;
        var doneDisplay = DownloadQueueColumns.Format(job, defaultVisible);
        Assert.DoesNotContain("waiting", doneDisplay, StringComparison.Ordinal);

        // JSON round-trip & corrupt fallback
        var custom = new List<DownloadQueueColumnSetting>
        {
            new(DownloadQueueColumn.BundleId, true, 0),
            new(DownloadQueueColumn.Status, true, 1),
            new(DownloadQueueColumn.Note, false, 2),
        };
        var json = DownloadQueueColumns.Serialize(custom);
        var parsed = DownloadQueueColumns.Parse(json);
        Assert.Equal(DownloadQueueColumns.Default.Count, parsed.Count);
        Assert.Equal(DownloadQueueColumn.BundleId, parsed[0].Column);
        Assert.True(parsed[0].Visible);
        Assert.False(parsed.Single(s => s.Column == DownloadQueueColumn.Note).Visible);

        Assert.Equal(DownloadQueueColumns.Default, DownloadQueueColumns.Parse("{not json"));
        Assert.Equal(DownloadQueueColumns.Default, DownloadQueueColumns.Parse("[]"));
    }

    [Fact]
    public void IndexLayoutStore_Normalization_Clamping_AndRightSections()
    {
        var normalized = IndexLayoutStore.NormalizePair([3, 1], IndexLayoutState.Default.Columns);
        Assert.Equal([75d, 25d], normalized);
        Assert.Equal([62d, 38d], IndexLayoutStore.ParsePair("bad", IndexLayoutState.Default.Columns));
        Assert.Equal([62d, 38d], IndexLayoutStore.ParsePair("[0,50]", IndexLayoutState.Default.Columns));

        Assert.Equal(240d, IndexLayoutStore.ParseOverlayWidth("100"));
        Assert.Equal(1200d, IndexLayoutStore.ParseOverlayWidth("5000"));
        Assert.Equal(420d, IndexLayoutStore.ParseOverlayWidth("abc"));

        var sections = new List<RightSectionSetting>
        {
            new(RightSection.Details, true, 0),
            new(RightSection.Queue, false, 1),
        };
        var serialized = IndexLayoutStore.SerializeRightSections(sections);
        var parsed = IndexLayoutStore.ParseRightSections(serialized);
        Assert.Equal([RightSection.Details], IndexLayoutStore.OrderedVisibleSections(parsed));

        // Both hidden yields empty visible list (collapses right pane cleanly)
        var bothHidden = new[]
        {
            new RightSectionSetting(RightSection.Queue, false, 0),
            new RightSectionSetting(RightSection.Details, false, 1),
        };
        Assert.Empty(IndexLayoutStore.OrderedVisibleSections(bothHidden));

        // Bilingual headers non-empty and distinct
        var enCulture = CultureInfo.GetCultureInfo("en");
        var zhCulture = CultureInfo.GetCultureInfo("zh-CN");
        using (IndexStrings.UseCultureScope(enCulture))
        {
            var enQueue = IndexLayoutStore.Header(RightSection.Queue);
            var enDetails = IndexLayoutStore.Header(RightSection.Details);
            using (IndexStrings.UseCultureScope(zhCulture))
            {
                Assert.NotEqual(enQueue, IndexLayoutStore.Header(RightSection.Queue));
                Assert.NotEqual(enDetails, IndexLayoutStore.Header(RightSection.Details));
            }
        }
    }

    [Fact]
    public void IndexGridColumns_ParseSerialize_SetVisible_AndRowHeightClamp()
    {
        Assert.Equal(8, IndexGridColumns.Defaults.Count);
        Assert.Equal(24d, IndexGridColumns.RowHeightSmall);
        Assert.Equal(32d, IndexGridColumns.RowHeightMedium);
        Assert.Equal(42d, IndexGridColumns.RowHeightLarge);

        Assert.Equal(16d, IndexGridColumns.ClampRowHeight(5));
        Assert.Equal(48d, IndexGridColumns.ClampRowHeight(90));
        Assert.Equal(32d, IndexGridColumns.ClampRowHeight(double.NaN));
        Assert.Equal(32d, IndexGridColumns.ClampRowHeight(0));

        var custom = new List<IndexGridColumnState>
        {
            new(IndexGridColumns.Title, 250, true),
            new(IndexGridColumns.BundleId, 20, false), // below minWidth 100 -> clamps to 100
        };
        var parsed = IndexGridColumns.Parse(IndexGridColumns.Serialize(custom));
        Assert.Equal(8, parsed.Count);
        Assert.Equal(IndexGridColumns.Title, parsed[0].Key);
        Assert.Equal(250, parsed[0].Width);
        Assert.Equal(100, parsed[1].Width);
        Assert.False(parsed[1].Visible);

        var hiddenTitle = IndexGridColumns.SetVisible(parsed, IndexGridColumns.Title, false);
        Assert.False(hiddenTitle.Single(c => c.Key == IndexGridColumns.Title).Visible);

        Assert.Equal(IndexGridColumns.DefaultStates, IndexGridColumns.Parse("corrupt"));
    }

    [Fact]
    public void IndexerSettings_RoundTrip_PreservesLanguageAndUnknownKeys()
    {
        using var ws = new TestWorkspace("index-settings-i1");
        var settingsPath = ws.Full("data/indexer.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, """
            {
              "Ui.Language": "en",
              "Download.Connections": 8,
              "Custom.Unknown": "keep-me"
            }
            """);

        var layout = new IndexLayoutState(
            [70d, 30d],
            RightHidden: true,
            RightOverlayWidth: 510d,
            RightSplit: [40d, 60d],
            RightSections:
            [
                new(RightSection.Details, true, 0),
                new(RightSection.Queue, true, 1),
            ]);
        var gridCols = IndexGridColumns.SetVisible(IndexGridColumns.DefaultStates, IndexGridColumns.Category, false);
        var queueCols = new List<DownloadQueueColumnSetting>
        {
            new(DownloadQueueColumn.Status, true, 0),
            new(DownloadQueueColumn.BundleId, true, 1),
            new(DownloadQueueColumn.Name, true, 2),
        };

        IndexerSettings.SaveWindowState(layout, IndexGridColumns.RowHeightLarge, gridCols, settingsPath);
        IndexerSettings.SaveQueueColumns(queueCols, settingsPath);

        Assert.Equal("en", IndexerSettings.LoadLanguage(settingsPath));
        var loadedLayout = IndexerSettings.LoadLayout(settingsPath);
        Assert.Equal([70d, 30d], loadedLayout.Columns);
        Assert.True(loadedLayout.RightHidden);
        Assert.Equal(510d, loadedLayout.RightOverlayWidth);
        Assert.Equal([40d, 60d], loadedLayout.RightSplit);
        Assert.Equal([RightSection.Details, RightSection.Queue], IndexLayoutStore.OrderedVisibleSections(loadedLayout.RightSections));

        Assert.Equal(42d, IndexerSettings.LoadGridRowHeight(settingsPath));
        var loadedCols = IndexerSettings.LoadGridColumns(settingsPath);
        Assert.False(loadedCols.Single(c => c.Key == IndexGridColumns.Category).Visible);

        var loadedQueueCols = IndexerSettings.LoadQueueColumns(settingsPath);
        Assert.True(loadedQueueCols.Single(c => c.Column == DownloadQueueColumn.BundleId).Visible);

        var rawObj = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        Assert.Equal(8, rawObj["Download.Connections"]!.GetValue<int>());
        Assert.Equal("keep-me", rawObj["Custom.Unknown"]!.GetValue<string>());

        // Corrupt file falls back cleanly
        var corruptPath = ws.Full("corrupt.json");
        File.WriteAllText(corruptPath, "{ broken");
        Assert.Equal(IndexLayoutState.Default.Columns, IndexerSettings.LoadLayout(corruptPath).Columns);
        Assert.Equal(32d, IndexerSettings.LoadGridRowHeight(corruptPath));
        Assert.Equal(IndexGridColumns.DefaultStates, IndexerSettings.LoadGridColumns(corruptPath));
        Assert.Equal(DownloadQueueColumns.Default, IndexerSettings.LoadQueueColumns(corruptPath));
    }

    [Fact]
    public void DownloadQueueStore_Lifecycle_Reuse_Batch_Cancel_Suspend_Clear_AndVisualSync()
    {
        var store = new DownloadQueueStore();
        var r1 = SampleRow("a", "Alpha", "com.alpha");
        var r2 = SampleRow("b", "Beta", "com.beta");

        var (job1, enqueued1) = store.Enqueue(r1, sourceSummary: "src");
        Assert.True(enqueued1);
        Assert.Equal(DownloadJobStatus.Pending, job1.Status);
        Assert.Equal(DownloadRowVisual.Waiting, job1.Visual);
        Assert.Single(store.Jobs);

        // Duplicate enqueue while active is skipped without duplicating
        var (_, enqueuedDup) = store.Enqueue(r1);
        Assert.False(enqueuedDup);
        Assert.Single(store.Jobs);

        // Batch enqueue skips r1, adds r2
        var (batchAdded, batchSkipped) = store.EnqueueMany([r1, r2]);
        Assert.Equal(1, batchAdded);
        Assert.Equal(1, batchSkipped);
        Assert.Equal(2, store.Jobs.Count);

        store.SyncRowVisuals([r1, r2]);
        Assert.Equal(DownloadRowVisual.Waiting, r1.Visual);
        Assert.Equal(DownloadRowVisual.Waiting, r2.Visual);

        // Suspend / Resume toggles Pending <-> Paused
        Assert.True(store.ToggleSuspend());
        Assert.All(store.Jobs, j => Assert.Equal(DownloadJobStatus.Paused, j.Status));
        store.SyncRowVisuals([r1, r2]);
        Assert.Equal(DownloadRowVisual.Paused, r1.Visual);

        Assert.False(store.ToggleSuspend());
        Assert.All(store.Jobs, j => Assert.Equal(DownloadJobStatus.Pending, j.Status));

        // Cancel transitions to Cancelled and clears visual upon ClearFinished
        Assert.True(store.Cancel(r1.Id));
        Assert.Equal(DownloadJobStatus.Cancelled, job1.Status);
        Assert.False(job1.CanCancel);

        // Re-enqueueing finished row reuses same DownloadJobRow instance in-place
        var (reusedJob, reused) = store.Enqueue(r1);
        Assert.True(reused);
        Assert.Same(job1, reusedJob);
        Assert.Equal(DownloadJobStatus.Pending, reusedJob.Status);
        Assert.Equal(2, store.Jobs.Count);

        // Mark r2 succeeded and clear finished
        store.Jobs.Single(j => j.Id == r2.Id).Status = DownloadJobStatus.Succeeded;
        Assert.Equal(1, store.ClearFinished());
        Assert.Single(store.Jobs);
        store.SyncRowVisuals([r1, r2]);
        Assert.Equal(DownloadRowVisual.Waiting, r1.Visual);
        Assert.Equal(DownloadRowVisual.None, r2.Visual);
    }

    [Fact]
    public void IndexDatabase_GetById_FindsExistingAndReturnsNullForMissing()
    {
        using var ws = new TestWorkspace("index-getbyid");
        var dbPath = ws.Full("index.db");
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
                CREATE TABLE entries (
                    id TEXT PRIMARY KEY,
                    bundle_id TEXT NOT NULL,
                    bundle_lower TEXT NOT NULL,
                    title TEXT,
                    version TEXT,
                    min_os TEXT,
                    min_os_num INTEGER,
                    size INTEGER,
                    size_exact INTEGER,
                    device_mask INTEGER,
                    icon TEXT,
                    category TEXT,
                    subgenre TEXT,
                    developer TEXT
                );
                CREATE TABLE sources (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    entry_id TEXT NOT NULL,
                    source_id TEXT,
                    url TEXT NOT NULL,
                    page TEXT,
                    size INTEGER
                );
                INSERT INTO entries VALUES ('e1', 'com.example.one', 'com.example.one', 'One', '1.0', '5.0', 50000, 1024, 1, 2, '', 'Games', '', 'Dev');
                INSERT INTO sources (entry_id, source_id, url, page, size) VALUES ('e1', 's1', 'https://127.0.0.1/1.ipa', '', 1024);
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var db = new IndexDatabase(dbPath);
        var found = db.GetById("e1");
        Assert.NotNull(found);
        Assert.Equal("com.example.one", found.BundleId);
        Assert.Equal(1, found.SourceCount);

        Assert.Null(db.GetById("missing"));

        var batch = db.GetByIds(["e1", "missing"]);
        Assert.Single(batch);
        Assert.Equal("e1", batch[0].Id);
        Assert.Empty(db.GetByIds([]));
    }

    [Fact]
    public void App_SingleInstanceMutex_PreventsDuplicateAcquire_UntilReleased()
    {
        var mutexName = $"IdeviceIndex.Test.Mutex.{Guid.NewGuid():N}";
        Mutex? first = null;
        Mutex? second = null;
        Mutex? third = null;
        try
        {
            Assert.True(IdeviceIndex.App.TryAcquireSingleInstanceMutex(mutexName, out first));
            Assert.NotNull(first);

            Assert.False(IdeviceIndex.App.TryAcquireSingleInstanceMutex(mutexName, out second));
            Assert.Null(second);

            IdeviceIndex.App.ReleaseSingleInstanceMutex(ref first);
            Assert.Null(first);

            Assert.True(IdeviceIndex.App.TryAcquireSingleInstanceMutex(mutexName, out third));
            Assert.NotNull(third);
        }
        finally
        {
            IdeviceIndex.App.ReleaseSingleInstanceMutex(ref second);
            IdeviceIndex.App.ReleaseSingleInstanceMutex(ref first);
            IdeviceIndex.App.ReleaseSingleInstanceMutex(ref third);
        }
    }

    [Fact]
    public void IndexGridColumns_IconMinWidth_AllowsSmallRowHeight_And_BatchEnqueuePreservesSourceSummary()
    {
        var iconSpec = IndexGridColumns.Find(IndexGridColumns.Icon);
        Assert.NotNull(iconSpec);
        Assert.True(iconSpec!.MinWidth <= IndexGridColumns.RowHeightSmall + 6);

        var store = new DownloadQueueStore();
        var r1 = SampleRow("b1", "App One", "com.one");
        var (added, skipped) = store.EnqueueMany([r1]);
        Assert.Equal(1, added);
        Assert.Equal(0, skipped);
        Assert.Equal("2", store.Jobs[0].SourceSummary);
    }

    [Fact]
    public void Strings_LicenseBody_SingleInstance_And_PolicySystemDir_LocalizedInEnAndZhCn()
    {
        var en = CultureInfo.GetCultureInfo("en");
        var zh = CultureInfo.GetCultureInfo("zh-CN");

        foreach (var key in new[]
        {
            nameof(IndexStrings.License_Body),
            nameof(IndexStrings.App_SingleInstance_Title),
            nameof(IndexStrings.App_SingleInstance_AlreadyRunning),
            nameof(IndexStrings.Policy_Error_ForbiddenSystemDirFormat),
            nameof(IndexStrings.Toolbar_Settings),
            nameof(IndexStrings.DownloadDir_Warning_NotInScanRoot),
            nameof(IndexStrings.DownloadDir_Warning_OutsideBlocked),
            nameof(IndexStrings.Settings_Security_ConfirmTitle),
            nameof(IndexStrings.Settings_Security_ConfirmEnable),
        })
        {
            var enText = IndexStrings.ResourceManager.GetString(key, en);
            var zhText = IndexStrings.ResourceManager.GetString(key, zh);
            Assert.False(string.IsNullOrWhiteSpace(enText), $"Missing EN string for {key}");
            Assert.False(string.IsNullOrWhiteSpace(zhText), $"Missing ZH string for {key}");
            Assert.NotEqual(enText, zhText);
        }
    }

    [Fact]
    public void IndexSettingsDraft_LoadSave_AllSettings_MoveOrder_ResetGroup_And_Fallback()
    {
        using var ws = new TestWorkspace("index-settings-draft");
        var progDir = ws.Full("prog");
        var settingsPath = Path.Combine(progDir, "data", "indexer.json");
        var customDir = ws.Full("dl-custom");
        var customSess = ws.Full("sess-custom");

        var draft = IndexSettingsDraft.Load(settingsPath, progDir);
        Assert.False(draft.AllowOutsideProgramDirWrite);
        Assert.Equal("system", draft.UiLanguageSetting);

        draft.UiLanguageSetting = "zh-CN";
        draft.AllowOutsideProgramDirWrite = true;
        draft.RightSections = IndexSettingsDraft.MoveSection(draft.RightSections, 0, 1);
        Assert.Equal(RightSection.Details, draft.RightSections[0].Section);
        Assert.Equal(0, draft.RightSections[0].Order);

        draft.QueueColumns = IndexSettingsDraft.MoveQueueColumn(draft.QueueColumns, 1, -1);
        Assert.Equal(DownloadQueueColumn.Note, draft.QueueColumns[0].Column);

        draft.Download = IndexSettingsDraft.NormalizeFromInputs(
            dir: customDir,
            rememberLastDir: false,
            connectionsText: "10",
            splitsText: "6",
            minSplitSize: "8M",
            concurrencyText: "5",
            speedLimit: "4M",
            perTaskSpeedLimit: "512K",
            retryTimesText: "5",
            retryWaitText: "20",
            timeoutText: "90",
            continueDownload: false,
            proxyMode: DownloadSettings.ProxyManual,
            proxyUrl: "http://127.0.0.1:8888",
            proxyBypass: "<local>;example.com",
            userAgent: "TestUA/3.0",
            sessionDirMode: DownloadSettings.SessionCustom,
            sessionDirCustom: customSess,
            controlFileMode: DownloadSettings.ControlSession,
            keepPartFiles: false,
            partTtlHoursText: "48",
            spaceMarginMbText: "120",
            maxFileSizeMbText: "256",
            autoImport: false,
            autoRefreshFallback: false,
            openFolderWhenDone: true,
            programDir: progDir);

        draft.Save(settingsPath, progDir);

        var reloaded = IndexSettingsDraft.Load(settingsPath, progDir);
        Assert.Equal("zh-CN", reloaded.UiLanguageSetting);
        Assert.True(reloaded.AllowOutsideProgramDirWrite);
        Assert.Equal(RightSection.Details, reloaded.RightSections[0].Section);
        Assert.Equal(DownloadQueueColumn.Note, reloaded.QueueColumns[0].Column);
        Assert.Equal(Path.GetFullPath(customDir), reloaded.Download.Dir);
        Assert.False(reloaded.Download.RememberLastDir);
        Assert.Equal(10, reloaded.Download.Connections);
        Assert.Equal(6, reloaded.Download.Splits);
        Assert.Equal("8M", reloaded.Download.MinSplitSize);
        Assert.Equal(5, reloaded.Download.Concurrency);
        Assert.Equal("4M", reloaded.Download.SpeedLimit);
        Assert.Equal("512K", reloaded.Download.PerTaskSpeedLimit);
        Assert.Equal(5, reloaded.Download.RetryTimes);
        Assert.Equal(20, reloaded.Download.RetryWait);
        Assert.Equal(90, reloaded.Download.Timeout);
        Assert.False(reloaded.Download.Continue);
        Assert.Equal(DownloadSettings.ProxyManual, reloaded.Download.ProxyMode);
        Assert.Equal("http://127.0.0.1:8888", reloaded.Download.ProxyUrl);
        Assert.Equal("<local>;example.com", reloaded.Download.ProxyBypass);
        Assert.Equal("TestUA/3.0", reloaded.Download.UserAgent);
        Assert.Equal(DownloadSettings.SessionCustom, reloaded.Download.SessionDirMode);
        Assert.Equal(Path.GetFullPath(customSess), reloaded.Download.SessionDirCustom);
        Assert.Equal(DownloadSettings.ControlSession, reloaded.Download.ControlFileMode);
        Assert.False(reloaded.Download.KeepPartFiles);
        Assert.Equal(48, reloaded.Download.PartTtlHours);
        Assert.Equal(120, reloaded.Download.SpaceMarginMB);
        Assert.Equal(256, reloaded.Download.MaxFileSizeMB);
        Assert.False(reloaded.Download.AutoImport);
        Assert.False(reloaded.Download.AutoRefreshFallback);
        Assert.True(reloaded.Download.OpenFolderWhenDone);

        // Out-of-range / invalid raw inputs fall back to defaults
        var fallback = IndexSettingsDraft.NormalizeFromInputs(
            dir: "",
            rememberLastDir: true,
            connectionsText: "999",
            splitsText: "abc",
            minSplitSize: "512M",
            concurrencyText: "0",
            speedLimit: "1K",
            perTaskSpeedLimit: "bad",
            retryTimesText: "-1",
            retryWaitText: "999",
            timeoutText: "1",
            continueDownload: true,
            proxyMode: "bad",
            proxyUrl: "",
            proxyBypass: "",
            userAgent: "",
            sessionDirMode: "bad",
            sessionDirCustom: "",
            controlFileMode: "bad",
            keepPartFiles: true,
            partTtlHoursText: "0",
            spaceMarginMbText: "9999",
            maxFileSizeMbText: "-2",
            autoImport: true,
            autoRefreshFallback: true,
            openFolderWhenDone: false,
            programDir: progDir);
        Assert.Equal(DownloadSettings.CreateDefault(progDir) with { ProxyBypass = "" }, fallback);

        // Reset Security / General / Download / NetworkSession groups
        reloaded.ResetGroup(IndexSettingsGroup.Security, progDir);
        Assert.False(reloaded.AllowOutsideProgramDirWrite);
        reloaded.ResetGroup(IndexSettingsGroup.General, progDir);
        Assert.Equal("system", reloaded.UiLanguageSetting);
        Assert.Equal(RightSection.Queue, reloaded.RightSections[0].Section);
        reloaded.ResetGroup(IndexSettingsGroup.Download, progDir);
        Assert.Equal(DownloadSettings.CreateDefault(progDir).Connections, reloaded.Download.Connections);
        Assert.Equal(DownloadSettings.CreateDefault(progDir).Dir, reloaded.Download.Dir);
        reloaded.ResetGroup(IndexSettingsGroup.NetworkSession, progDir);
        Assert.Equal(DownloadSettings.ProxySystem, reloaded.Download.ProxyMode);
        Assert.True(reloaded.Download.KeepPartFiles);
    }

    [Fact]
    public void MainWindow_DownloadDirChoices_WarningEvaluation_And_SafeHttpUriValidation()
    {
        using var ws = new TestWorkspace("index-dir-warning");
        var progDir = ws.Full("prog");
        var defaultLib = DownloadSettings.DefaultDir(progDir);
        var scanRoot = ws.Full("scan-root");
        var otherOutside = ws.Full("other-outside");

        var choices = IdeviceIndex.MainWindow.BuildDownloadDirChoices(otherOutside, [scanRoot, defaultLib], progDir);
        Assert.Equal(3, choices.Count);
        Assert.Equal(defaultLib, choices[0]);
        Assert.Equal(Path.GetFullPath(scanRoot), choices[1]);
        Assert.Equal(Path.GetFullPath(otherOutside), choices[2]);

        // Outside program dir with AllowOutsideWrite=false -> blocked warning
        Assert.Equal(
            IndexStrings.DownloadDir_Warning_OutsideBlocked,
            IdeviceIndex.MainWindow.EvaluateDownloadDirWarning(otherOutside, [scanRoot], true, allowOutsideWrite: false, progDir));

        // Outside program dir with AllowOutsideWrite=true, outside scan roots -> non-root warning
        Assert.Equal(
            IndexStrings.DownloadDir_Warning_NotInScanRoot,
            IdeviceIndex.MainWindow.EvaluateDownloadDirWarning(otherOutside, [scanRoot], true, allowOutsideWrite: true, progDir));

        // Inside scan roots (exact root and subdirectory) with AllowOutsideWrite=true -> no warning
        Assert.Null(
            IdeviceIndex.MainWindow.EvaluateDownloadDirWarning(scanRoot, [scanRoot], true, allowOutsideWrite: true, progDir));
        Assert.Null(
            IdeviceIndex.MainWindow.EvaluateDownloadDirWarning(Path.Combine(scanRoot, "sub"), [scanRoot], true, allowOutsideWrite: true, progDir));
        // Sibling prefix trap (scan-root2 vs scan-root) -> warning
        Assert.Equal(
            IndexStrings.DownloadDir_Warning_NotInScanRoot,
            IdeviceIndex.MainWindow.EvaluateDownloadDirWarning(ws.Full("scan-root2"), [scanRoot], true, allowOutsideWrite: true, progDir));

        // Safe HTTP/HTTPS URI validation
        Assert.True(IdeviceIndex.MainWindow.TryCreateSafeHttpUri("https://example.com/app.ipa", out var httpsUri));
        Assert.Equal("https", httpsUri!.Scheme);
        Assert.True(IdeviceIndex.MainWindow.TryCreateSafeHttpUri("http://127.0.0.1:8080/a.ipa", out _));
        Assert.False(IdeviceIndex.MainWindow.TryCreateSafeHttpUri("file:///C:/Windows/win.ini", out _));
        Assert.False(IdeviceIndex.MainWindow.TryCreateSafeHttpUri("javascript:alert(1)", out _));
        Assert.False(IdeviceIndex.MainWindow.TryCreateSafeHttpUri("ftp://example.com/a.ipa", out _));
        Assert.False(IdeviceIndex.MainWindow.TryCreateSafeHttpUri("", out _));
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en")]
    public void MainWindow_StaSmoke_InitializesMeasuresAndUpdatesLayout_WithoutBindingErrors(string cultureName)
    {
        using var ws = new TestWorkspace("index-ui-main");
        var dbPath = CreateMinimalIndexDatabase(ws);
        var defaultSettingsPath = IndexerSettings.DefaultPath;
        var hadDefaultSettings = File.Exists(defaultSettingsPath);

        try
        {
            RunOnStaWithBindingCheck(cultureName, () =>
            {
                var window = new IdeviceIndex.MainWindow(dbPath);
                try
                {
                    window.Measure(new Size(1280, 780));
                    window.Arrange(new Rect(new Size(1280, 780)));
                    window.UpdateLayout();

                    Assert.Equal(IndexStrings.Window_Title, window.Title);
                    Assert.Equal(8, window.ResultsGrid.Columns.Count);
                    Assert.NotEmpty(window.DownloadDirCombo.Items);
                    Assert.Equal(IndexStrings.Toolbar_Settings, window.SettingsButton.Content);
                    Assert.Equal(IndexStrings.Toolbar_HideRight, window.RightToggleButton.Content);
                    Assert.Equal(IndexStrings.Queue_Suspend, window.QueueSuspendButton.Content);
                    Assert.Equal(5, window.MinOsOpCombo.Items.Count);
                    Assert.True(window.MergeBundleCheck.IsChecked);
                    Assert.NotNull(window.SearchOptionsButton);
                    Assert.NotNull(window.AdvancedFilterToggle);
                    Assert.NotNull(window.AdvancedFilterButton);
                }
                finally
                {
                    CloseWindowAndPump(window);
                }
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (!hadDefaultSettings && File.Exists(defaultSettingsPath))
            {
                try { File.Delete(defaultSettingsPath); } catch { }
            }
        }
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en")]
    public void SettingsWindow_StaSmoke_InitializesAllTabsAndMeasures_WithoutBindingErrors(string cultureName)
    {
        RunOnStaWithBindingCheck(cultureName, () =>
        {
            var window = new IdeviceIndex.SettingsWindow();
            try
            {
                window.Measure(new Size(1280, 780));
                window.Arrange(new Rect(new Size(1280, 780)));
                window.UpdateLayout();

                Assert.Equal(IndexStrings.Settings_Title, window.Title);
                Assert.Equal(5, window.Tabs.Items.Count);
                Assert.Equal(IndexStrings.Settings_Button_Ok, window.OkButton.Content);
                Assert.Equal(IndexStrings.Settings_Button_Apply, window.ApplyButton.Content);
                Assert.Equal(IndexStrings.Settings_Button_Cancel, window.CancelButton.Content);
                Assert.Equal(2, window.RightSectionsList.Items.Count);
                Assert.Equal(10, window.QueueColumnsList.Items.Count);
                Assert.NotNull(window.DisplayLimitBox);
                Assert.NotNull(window.OpenLinkProgramBox);
                Assert.Equal(IndexStrings.License_Body, window.LicenseTextBox.Text);
                Assert.Equal(IndexStrings.Settings_License_OpenMain, window.OpenAppLicenseButton.Content);
                Assert.Equal(IndexStrings.Settings_License_OpenAria2, window.OpenAria2LicenseButton.Content);

                for (var i = 0; i < window.Tabs.Items.Count; i++)
                {
                    window.Tabs.SelectedIndex = i;
                    window.UpdateLayout();
                }

                var headers = window.Tabs.Items.Cast<TabItem>().Select(t => t.Header as string).ToList();
                Assert.Equal(
                    [
                        IndexStrings.Settings_Tab_General,
                        IndexStrings.Settings_Tab_Download,
                        IndexStrings.Settings_Tab_NetworkSession,
                        IndexStrings.Settings_Tab_Security,
                        IndexStrings.Settings_Tab_License,
                    ],
                    headers);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en")]
    public void IndexAdvancedFilterWindow_StaSmoke_InitializesAndMeasures_WithoutBindingErrors(string cultureName)
    {
        RunOnStaWithBindingCheck(cultureName, () =>
        {
            var state = new IndexAdvancedFilterState
            {
                Enabled = true,
                Groups =
                [
                    new IndexAdvancedFilterGroup
                    {
                        Join = IndexFilterJoin.Or,
                        Conditions =
                        [
                            new IndexAdvancedFilterCondition
                            {
                                Column = IndexFilterColumn.DeviceFamily,
                                Selected = ["iPhone", "iPad"],
                            },
                            new IndexAdvancedFilterCondition
                            {
                                Column = IndexFilterColumn.SizeMb,
                                Min = "1",
                                Max = "100",
                            },
                        ],
                    },
                ],
            };

            var window = new IdeviceIndex.IndexAdvancedFilterWindow(state, _ => { });
            try
            {
                window.Measure(new Size(1280, 780));
                window.Arrange(new Rect(new Size(1280, 780)));
                window.UpdateLayout();

                Assert.Equal(IndexStrings.Filter_Title, window.Title);
                Assert.Equal(IndexStrings.Filter_Apply, window.ApplyButton.Content);
                Assert.Equal(IndexStrings.Filter_Cancel, window.CancelButton.Content);
                Assert.True(window.GroupsPanel.Children.Count >= 1);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void IndexerSettings_NewKeys_PersistAndValidateCleanly()
    {
        using var ws = new TestWorkspace("index-new-settings");
        var settingsPath = ws.Full("settings.ini");

        // Defaults
        Assert.Equal(IndexerSettings.DefaultDisplayLimit, IndexerSettings.LoadDisplayLimit(settingsPath));
        Assert.True(IndexerSettings.LoadMergeSameBundle(settingsPath));
        Assert.Equal("", IndexerSettings.LoadOpenLinkProgram(settingsPath));
        Assert.Equal(IndexSearchMatchMode.Contains, IndexerSettings.LoadSearchMatchMode(settingsPath));

        // DisplayLimit: positive, 0 (unlimited), negative/invalid fallback (default 0)
        Assert.Equal(0, IndexerSettings.ParseDisplayLimit("0"));
        Assert.Equal(0, IndexerSettings.ParseDisplayLimit("-5"));
        Assert.Equal(0, IndexerSettings.ParseDisplayLimit("not-a-number"));

        IndexerSettings.SaveDisplayLimit(0, settingsPath);
        Assert.Equal(0, IndexerSettings.LoadDisplayLimit(settingsPath));

        IndexerSettings.SaveMergeSameBundle(false, settingsPath);
        Assert.False(IndexerSettings.LoadMergeSameBundle(settingsPath));

        var scope = new IndexSearchScope { IsTitle = false, IsBundleId = true, IsSource = true };
        IndexerSettings.SaveSearchScope(scope, settingsPath);
        var loadedScope = IndexerSettings.LoadSearchScope(settingsPath);
        Assert.False(loadedScope.IsTitle);
        Assert.True(loadedScope.IsBundleId);
        Assert.True(loadedScope.IsSource);

        IndexerSettings.SaveSearchMatchMode(IndexSearchMatchMode.Prefix, settingsPath);
        Assert.Equal(IndexSearchMatchMode.Prefix, IndexerSettings.LoadSearchMatchMode(settingsPath));

        var adv = new IndexAdvancedFilterState
        {
            Enabled = true,
            Groups =
            [
                new IndexAdvancedFilterGroup
                {
                    Join = IndexFilterJoin.And,
                    Conditions =
                    [
                        new IndexAdvancedFilterCondition
                        {
                            Column = IndexFilterColumn.Category,
                            TextMode = IndexTextFilterMode.Exact,
                            Text = "Games",
                        },
                    ],
                },
            ],
        };
        IndexerSettings.SaveAdvancedFilter(adv, settingsPath);
        var loadedAdv = IndexerSettings.LoadAdvancedFilter(settingsPath);
        Assert.True(loadedAdv.Enabled);
        Assert.Equal(1, loadedAdv.ActiveConditionCount);

        var prog = @"C:\Tools\browser.exe";
        IndexerSettings.SaveOpenLinkProgram(prog, settingsPath);
        Assert.Equal(prog, IndexerSettings.LoadOpenLinkProgram(settingsPath));

        // IndexSettingsDraft round-trip for DisplayLimit + OpenLinkProgram
        var draft = IndexSettingsDraft.Load(settingsPath, ws.Root);
        Assert.Equal(0, draft.DisplayLimit);
        Assert.Equal(prog, draft.OpenLinkProgram);
        draft.DisplayLimit = 250;
        draft.OpenLinkProgram = @"C:\Tools\other.exe";
        draft.Save(settingsPath, ws.Root);
        Assert.Equal(250, IndexerSettings.LoadDisplayLimit(settingsPath));
        Assert.Equal(@"C:\Tools\other.exe", IndexerSettings.LoadOpenLinkProgram(settingsPath));
    }

    [Fact]
    public void ExternalLinkOpener_BuildsSafeStartInfo_AndRejectsInvalidSchemesOrMissingExe()
    {
        // Valid http/https without custom program -> UseShellExecute=true
        var defaultRes = ExternalLinkOpener.TryBuildStartInfo("https://example.com/a.ipa?x=1&y=2", "");
        Assert.True(defaultRes.Ok);
        Assert.NotNull(defaultRes.StartInfo);
        Assert.True(defaultRes.StartInfo!.UseShellExecute);
        Assert.Equal("https://example.com/a.ipa?x=1&y=2", defaultRes.StartInfo.FileName);

        // Unsafe scheme rejected
        var unsafeRes = ExternalLinkOpener.TryBuildStartInfo("javascript:alert(1)", "");
        Assert.False(unsafeRes.Ok);
        Assert.Null(unsafeRes.StartInfo);

        // Custom .exe uses UseShellExecute=false and ArgumentList (no command string concatenation)
        var customRes = ExternalLinkOpener.TryBuildStartInfo(
            "http://127.0.0.1:8080/test.ipa",
            @"C:\Tools\curl-ui.exe",
            fileExists: p => p.EndsWith("curl-ui.exe", StringComparison.OrdinalIgnoreCase));
        Assert.True(customRes.Ok);
        Assert.NotNull(customRes.StartInfo);
        Assert.False(customRes.StartInfo!.UseShellExecute);
        Assert.Equal(Path.GetFullPath(@"C:\Tools\curl-ui.exe"), customRes.StartInfo.FileName);
        Assert.Equal(["http://127.0.0.1:8080/test.ipa"], customRes.StartInfo.ArgumentList);

        // Non-.exe or missing custom program rejected
        var nonExeRes = ExternalLinkOpener.TryBuildStartInfo(
            "https://example.com/a.ipa",
            @"C:\Tools\script.bat",
            fileExists: _ => true);
        Assert.False(nonExeRes.Ok);

        var missingRes = ExternalLinkOpener.TryBuildStartInfo(
            "https://example.com/a.ipa",
            @"C:\Tools\missing.exe",
            fileExists: _ => false);
        Assert.False(missingRes.Ok);
    }

    [Fact]
    public void DownloadEngine_ClassifyAria2Error_AppendsProxyHintOnSslHandshakeFailure()
    {
        var sslSnapshot = new Aria2StatusSnapshot(
            Gid: "g1",
            Status: "error",
            TotalLength: 0,
            CompletedLength: 0,
            DownloadSpeed: 0,
            Connections: 0,
            ErrorCode: "1",
            ErrorMessage: "SSL/TLS handshake failure: error:0A000126:SSL routines::unexpected eof while reading",
            FilePath: null);

        var sslOutcome = DownloadEngine.ClassifyAria2Error(sslSnapshot);
        Assert.NotNull(sslOutcome.ErrorMessage);
        Assert.Contains(IndexStrings.Download_Hint_ProxyAria2Direct, sslOutcome.ErrorMessage);

        var normalSnapshot = sslSnapshot with { ErrorMessage = "Resource not found", ErrorCode = "3" };
        var normalOutcome = DownloadEngine.ClassifyAria2Error(normalSnapshot);
        Assert.NotNull(normalOutcome.ErrorMessage);
        Assert.DoesNotContain(IndexStrings.Download_Hint_ProxyAria2Direct, normalOutcome.ErrorMessage);
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en")]
    public void ConfirmOutsideWriteWindow_StaSmoke_HasCancelDefault_AndZeroCheckBoxes(string cultureName)
    {
        RunOnStaWithBindingCheck(cultureName, () =>
        {
            var window = new IdeviceIndex.ConfirmOutsideWriteWindow();
            try
            {
                window.Measure(new Size(1280, 780));
                window.Arrange(new Rect(new Size(1280, 780)));
                window.UpdateLayout();

                Assert.Equal(IndexStrings.Settings_Security_ConfirmTitle, window.Title);
                Assert.True(window.CancelButton.IsCancel);
                Assert.Equal(IndexStrings.Settings_Security_ConfirmCancel, window.CancelButton.Content);
                Assert.False(window.EnableButton.IsDefault);
                Assert.False(window.EnableButton.IsCancel);
                Assert.Equal(IndexStrings.Settings_Security_ConfirmEnable, window.EnableButton.Content);

                Assert.Equal(0, CountVisualCheckBoxes(window));
                Assert.Equal(0, CountLogicalCheckBoxes(window));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SettingsWindow_OkButtonClick_SavesAndClosesWithoutCancelling()
    {
        using var ws = new TestWorkspace("index-settings-ok");
        var settingsPath = Path.Combine(ws.Root, "data", "indexer.json");

        RunOnStaWithBindingCheck("en", () =>
        {
            var window = new IdeviceIndex.SettingsWindow(
                settingsPath: settingsPath,
                programDir: ws.Root)
            {
                Left = -32000,
                Top = -32000,
                Width = 120,
                Height = 120,
                ShowActivated = false,
                ShowInTaskbar = false,
            };

            var closed = false;
            var frame = new DispatcherFrame();
            window.Closed += (_, _) =>
            {
                closed = true;
                frame.Continue = false;
            };

            var timeout = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
            {
                Interval = TimeSpan.FromSeconds(5),
            };
            timeout.Tick += (_, _) =>
            {
                timeout.Stop();
                frame.Continue = false;
            };

            try
            {
                window.Show();
                window.DisplayLimitBox.Text = "321";
                window.OkButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

                if (!closed)
                {
                    timeout.Start();
                    Dispatcher.PushFrame(frame);
                }

                Assert.True(closed, "SettingsWindow should close cleanly after OkButton click.");
                Assert.True(File.Exists(settingsPath));
                Assert.Equal(321, IndexerSettings.LoadDisplayLimit(settingsPath));
            }
            finally
            {
                timeout.Stop();
                if (!closed)
                    CloseWindowAndPump(window);
            }
        });
    }

    [Fact]
    public void MainWindow_ExpandedGroup_IsGroupEndAndResolveSelectedRows_DoNotThrowOnDuplicateId()
    {
        using var ws = new TestWorkspace("index-group-expand-enqueue");
        var dbPath = CreateGroupedIndexDatabase(ws);
        var defaultSettingsPath = IndexerSettings.DefaultPath;
        var hadDefaultSettings = File.Exists(defaultSettingsPath);

        try
        {
            RunOnStaWithBindingCheck("en", () =>
            {
                var window = new IdeviceIndex.MainWindow(dbPath)
                {
                    Left = -32000,
                    Top = -32000,
                    Width = 800,
                    Height = 600,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                };

                try
                {
                    window.Show();
                    PumpUntil(() => window.RowsForTests.Count == 2, TimeSpan.FromSeconds(5));
                    Assert.Equal(2, window.RowsForTests.Count);

                    var groupRow = Assert.Single(window.RowsForTests, r => r.IsGroup);
                    Assert.True(groupRow.IsGroupEnd);
                    Assert.False(groupRow.IsExpanded);

                    // Expand group asynchronously on STA dispatcher
                    var expandTask = window.ToggleGroupExpandAsync(groupRow);
                    PumpUntil(() => expandTask.IsCompleted, TimeSpan.FromSeconds(5));
                    expandTask.GetAwaiter().GetResult();

                    Assert.True(groupRow.IsExpanded);
                    Assert.False(groupRow.IsGroupEnd);
                    Assert.Equal(4, window.RowsForTests.Count);
                    Assert.True(window.RowsForTests[2].IsGroupEnd);

                    // P1-2 regression: group header + expanded member share representative Id; resolving selected rows must not throw ArgumentException
                    var resolvedTask = window.ResolveSelectedRowsForEnqueueAsync(
                        new IndexDatabase(dbPath),
                        [groupRow.Id, "s1"]);
                    PumpUntil(() => resolvedTask.IsCompleted, TimeSpan.FromSeconds(5));
                    var resolved = resolvedTask.GetAwaiter().GetResult();

                    Assert.Equal(2, resolved.Count);
                    Assert.All(resolved, r => Assert.False(r.IsGroup));

                    // Collapse group restores IsGroupEnd = true
                    var collapseTask = window.ToggleGroupExpandAsync(groupRow);
                    PumpUntil(() => collapseTask.IsCompleted, TimeSpan.FromSeconds(5));
                    collapseTask.GetAwaiter().GetResult();

                    Assert.False(groupRow.IsExpanded);
                    Assert.True(groupRow.IsGroupEnd);
                    Assert.Equal(2, window.RowsForTests.Count);
                }
                finally
                {
                    CloseWindowAndPump(window);
                }
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (!hadDefaultSettings && File.Exists(defaultSettingsPath))
            {
                try { File.Delete(defaultSettingsPath); } catch { }
            }
        }
    }

    [Fact]
    public void MainWindow_MinOsMenu_ClickParentFillsMajorMinor_AndClickChildFillsRevision()
    {
        using var ws = new TestWorkspace("index-minos-menu");
        var dbPath = CreateMinimalIndexDatabase(ws);
        var defaultSettingsPath = IndexerSettings.DefaultPath;
        var hadDefaultSettings = File.Exists(defaultSettingsPath);

        try
        {
            RunOnStaWithBindingCheck("en", () =>
            {
                var window = new IdeviceIndex.MainWindow(dbPath);
                try
                {
                    var menu = window.BuildMinOsMenu();
                    var parent61 = menu.Items.OfType<MenuItem>().First(m => Equals(m.Header, "6.1"));
                    var child613 = parent61.Items.OfType<MenuItem>().First(m => Equals(m.Header, "6.1.3"));
                    var anyItem = menu.Items.OfType<MenuItem>().First();

                    var mouseArgs = new System.Windows.Input.MouseButtonEventArgs(
                        System.Windows.Input.Mouse.PrimaryDevice,
                        0,
                        System.Windows.Input.MouseButton.Left)
                    {
                        RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent,
                    };
                    parent61.RaiseEvent(mouseArgs);
                    Assert.Equal("6.1", window.MinOsBox.Text);

                    child613.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Assert.Equal("6.1.3", window.MinOsBox.Text);

                    anyItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Assert.Equal("", window.MinOsBox.Text);
                }
                finally
                {
                    CloseWindowAndPump(window);
                }
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (!hadDefaultSettings && File.Exists(defaultSettingsPath))
            {
                try { File.Delete(defaultSettingsPath); } catch { }
            }
        }
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en")]
    public void MainWindow_MissingDb_ShowsBanner_WithoutBindingErrors(string cultureName)
    {
        var defaultSettingsPath = IndexerSettings.DefaultPath;
        var hadDefaultSettings = File.Exists(defaultSettingsPath);

        try
        {
            RunOnStaWithBindingCheck(cultureName, () =>
            {
                var window = new IdeviceIndex.MainWindow(null)
                {
                    Left = -32000,
                    Top = -32000,
                    Width = 900,
                    Height = 600,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                };

                try
                {
                    window.Show();
                    PumpUntil(() => window.MissingDbBanner.Visibility == Visibility.Visible, TimeSpan.FromSeconds(5));

                    Assert.Equal(Visibility.Visible, window.MissingDbBanner.Visibility);
                    Assert.Equal(IndexStrings.Status_NoIndexLoaded, window.PathText.Text);
                    Assert.Equal(IndexStrings.Toolbar_Index, window.ImportIndexButton.Content);
                    Assert.Equal(IndexStrings.Toolbar_ImportIndex, window.BannerImportButton.Content);
                    Assert.Empty(window.RowsForTests);

                    // Verify RightToggleButton, ImportIndexButton, SettingsButton moved to 2nd row right after BrowseDownloadDirButton
                    var row2Children = window.DownloadDirButtonsPanel.Children.OfType<Button>().ToList();
                    Assert.Equal(
                        [
                            window.BrowseDownloadDirButton,
                            window.RightToggleButton,
                            window.ImportIndexButton,
                            window.SettingsButton,
                        ],
                        row2Children);
                }
                finally
                {
                    CloseWindowAndPump(window);
                }
            });
        }
        finally
        {
            if (!hadDefaultSettings && File.Exists(defaultSettingsPath))
            {
                try { File.Delete(defaultSettingsPath); } catch { }
            }
        }
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en")]
    public void IndexImportWindow_StaSmoke_InitializesBothModesAndMeasures_WithoutBindingErrors(string cultureName)
    {
        using var ws = new TestWorkspace("index-import-window-ui");
        var settingsPath = Path.Combine(ws.Root, "data", "indexer.json");

        RunOnStaWithBindingCheck(cultureName, () =>
        {
            var window = new IdeviceIndex.IndexImportWindow(
                programDir: ws.Root,
                currentDbPath: null,
                currentIsExternal: false,
                settingsPath: settingsPath);
            try
            {
                window.Measure(new Size(1280, 780));
                window.Arrange(new Rect(new Size(1280, 780)));
                window.UpdateLayout();

                Assert.Equal(IndexStrings.Import_Title, window.Title);
                Assert.Equal(IndexStrings.Import_StartButton, window.ImportButton.Content);
                Assert.Equal(IndexStrings.Import_DownloadApplyButton, window.DownloadUpdateButton.Content);
                Assert.False(window.ImportButton.IsEnabled);
                Assert.False(window.DownloadUpdateButton.IsEnabled);

                window.UpdateUrlBox.Text = "http://127.0.0.1/manifest.json";
                window.UpdateLayout();
                Assert.True(window.DownloadUpdateButton.IsEnabled);

                window.FilePathBox.Text = ws.Full("custom.db");
                window.UpdateLayout();
                Assert.Equal(Visibility.Visible, window.DbModePanel.Visibility);
                Assert.Equal(Visibility.Collapsed, window.JsonModePanel.Visibility);
                Assert.True(window.ImportButton.IsEnabled);

                window.FilePathBox.Text = ws.Full("entries.jsonl.gz");
                window.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, window.DbModePanel.Visibility);
                Assert.Equal(Visibility.Visible, window.JsonModePanel.Visibility);
                Assert.True(window.ImportButton.IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void FormatDownloadDirDisplay_FormatsProgramRelativeAndExternalPaths()
    {
        var progDir = @"D:\Tools\LegacyIPA";
        Assert.Equal(@"\", IdeviceIndex.MainWindow.FormatDownloadDirDisplay(progDir, progDir));
        Assert.Equal(@"\", IdeviceIndex.MainWindow.FormatDownloadDirDisplay(progDir + @"\", progDir));
        Assert.Equal(@"\library", IdeviceIndex.MainWindow.FormatDownloadDirDisplay(Path.Combine(progDir, "library"), progDir));
        Assert.Equal(@"\library\sub", IdeviceIndex.MainWindow.FormatDownloadDirDisplay(Path.Combine(progDir, "library", "sub"), progDir));
        Assert.Equal(@"E:\External\IPAs", IdeviceIndex.MainWindow.FormatDownloadDirDisplay(@"E:\External\IPAs\", progDir));
        Assert.Equal(@"D:\Tools\LegacyIPA2\library", IdeviceIndex.MainWindow.FormatDownloadDirDisplay(@"D:\Tools\LegacyIPA2\library", progDir));
    }

    [Fact]
    public void MainWindow_ScrollBatchLoading_TracksSqlOffsetIndependentlyOfExpandedGroups()
    {
        using var ws = new TestWorkspace("index-batch-scroll");
        var dbPath = CreateMultiGroupPagingDatabase(ws);
        var defaultSettingsPath = IndexerSettings.DefaultPath;
        var hadDefaultSettings = File.Exists(defaultSettingsPath);
        byte[]? prevSettingsBytes = hadDefaultSettings ? File.ReadAllBytes(defaultSettingsPath) : null;

        try
        {
            IndexerSettings.SaveDisplayLimit(2, defaultSettingsPath);
            IndexerSettings.SaveMergeSameBundle(true, defaultSettingsPath);

            RunOnStaWithBindingCheck("en", () =>
            {
                var window = new IdeviceIndex.MainWindow(dbPath)
                {
                    Left = -32000,
                    Top = -32000,
                    Width = 800,
                    Height = 600,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                };

                try
                {
                    window.Show();
                    PumpUntil(() => window.RowsForTests.Count == 2, TimeSpan.FromSeconds(5));
                    Assert.Equal(2, window.RowsForTests.Count);
                    Assert.Equal(2, window.LoadedSqlOffsetForTests);
                    Assert.True(window.HasMoreRowsForTests);

                    // Expand first group: _rows increases from 2 to 4, but SQL offset MUST remain 2!
                    var groupRow = Assert.Single(window.RowsForTests, r => r.IsGroup);
                    var expandTask = window.ToggleGroupExpandAsync(groupRow);
                    PumpUntil(() => expandTask.IsCompleted, TimeSpan.FromSeconds(5));
                    expandTask.GetAwaiter().GetResult();

                    Assert.Equal(4, window.RowsForTests.Count);
                    Assert.Equal(2, window.LoadedSqlOffsetForTests);

                    // Load next batch -> appends only the 3rd group (does NOT re-fetch offset 4 and miss group 3)
                    var moreTask = window.LoadMoreRowsAsync();
                    PumpUntil(() => moreTask.IsCompleted, TimeSpan.FromSeconds(5));
                    moreTask.GetAwaiter().GetResult();

                    Assert.Equal(5, window.RowsForTests.Count);
                    Assert.Equal(3, window.LoadedSqlOffsetForTests);
                    Assert.False(window.HasMoreRowsForTests);
                    Assert.Equal("Zebra Solo", window.RowsForTests[^1].BaseTitle);
                }
                finally
                {
                    CloseWindowAndPump(window);
                }
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (hadDefaultSettings && prevSettingsBytes is not null)
            {
                File.WriteAllBytes(defaultSettingsPath, prevSettingsBytes);
            }
            else if (!hadDefaultSettings && File.Exists(defaultSettingsPath))
            {
                try { File.Delete(defaultSettingsPath); } catch { }
            }
        }
    }

    private static string CreateMultiGroupPagingDatabase(TestWorkspace ws)
    {
        var dbPath = ws.Full("paging.db");
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
                CREATE TABLE entries (
                    id TEXT PRIMARY KEY,
                    bundle_id TEXT NOT NULL,
                    bundle_lower TEXT NOT NULL,
                    title TEXT, version TEXT, min_os TEXT, min_os_num INTEGER,
                    size INTEGER, size_exact INTEGER, device_mask INTEGER,
                    icon TEXT, category TEXT, subgenre TEXT, developer TEXT
                );
                CREATE TABLE sources (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    entry_id TEXT NOT NULL,
                    source_id TEXT,
                    url TEXT NOT NULL,
                    page TEXT,
                    size INTEGER,
                    sha256 TEXT
                );
                CREATE TABLE descriptions (
                    entry_id TEXT NOT NULL,
                    lang TEXT NOT NULL,
                    text TEXT NOT NULL
                );
                INSERT INTO meta VALUES ('schema_version', '1');
                INSERT INTO entries VALUES ('a1', 'com.demo.alpha', 'com.demo.alpha', 'Alpha 1', '1.0', '5.0', 50000, 1024, 1, 2, '', 'Games', '', 'Dev');
                INSERT INTO entries VALUES ('a2', 'com.demo.alpha', 'com.demo.alpha', 'Alpha 2', '2.0', '6.0', 60000, 2048, 1, 2, '', 'Games', '', 'Dev');
                INSERT INTO entries VALUES ('m1', 'com.demo.mid', 'com.demo.mid', 'Mid Solo', '1.0', '6.0', 60000, 1024, 1, 2, '', 'Tools', '', 'Dev');
                INSERT INTO entries VALUES ('z1', 'com.demo.zebra', 'com.demo.zebra', 'Zebra Solo', '1.0', '6.0', 60000, 1024, 1, 2, '', 'Tools', '', 'Dev');
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        return dbPath;
    }

    private static void PumpUntil(Func<bool> condition, TimeSpan timeoutDuration)
    {
        if (condition()) return;
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow + timeoutDuration;
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(20),
        };
        timer.Tick += (_, _) =>
        {
            if (condition() || DateTime.UtcNow >= deadline)
            {
                timer.Stop();
                frame.Continue = false;
            }
        };
        timer.Start();
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timer.Stop();
        }
    }

    private static string CreateGroupedIndexDatabase(TestWorkspace ws)
    {
        var dbPath = ws.Full("grouped.db");
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
                CREATE TABLE entries (
                    id TEXT PRIMARY KEY,
                    bundle_id TEXT NOT NULL,
                    bundle_lower TEXT NOT NULL,
                    title TEXT, version TEXT, min_os TEXT, min_os_num INTEGER,
                    size INTEGER, size_exact INTEGER, device_mask INTEGER,
                    icon TEXT, category TEXT, subgenre TEXT, developer TEXT
                );
                CREATE TABLE sources (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    entry_id TEXT NOT NULL,
                    source_id TEXT,
                    url TEXT NOT NULL,
                    page TEXT,
                    size INTEGER,
                    sha256 TEXT
                );
                CREATE TABLE descriptions (
                    entry_id TEXT NOT NULL,
                    lang TEXT NOT NULL,
                    text TEXT NOT NULL
                );
                INSERT INTO meta VALUES ('schema_version', '1');
                INSERT INTO entries VALUES ('g1', 'com.demo.grp', 'com.demo.grp', 'Group App 1', '1.0', '5.0', 50000, 1024, 1, 2, '', 'Games', '', 'Dev');
                INSERT INTO entries VALUES ('g2', 'com.demo.grp', 'com.demo.grp', 'Group App 2', '2.0', '6.0', 60000, 2048, 1, 2, '', 'Games', '', 'Dev');
                INSERT INTO entries VALUES ('s1', 'com.demo.solo', 'com.demo.solo', 'Solo App', '1.0', '6.0', 60000, 1024, 1, 2, '', 'Tools', '', 'Dev');
                INSERT INTO sources (entry_id, source_id, url, page, size, sha256) VALUES ('g1', 'm1', 'http://127.0.0.1/g1.ipa', '', 1024, NULL);
                INSERT INTO sources (entry_id, source_id, url, page, size, sha256) VALUES ('g2', 'm1', 'http://127.0.0.1/g2.ipa', '', 2048, NULL);
                INSERT INTO sources (entry_id, source_id, url, page, size, sha256) VALUES ('s1', 'm1', 'http://127.0.0.1/s1.ipa', '', 1024, NULL);
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        return dbPath;
    }

    private static string CreateMinimalIndexDatabase(TestWorkspace ws)
    {
        var dbPath = ws.Full("index.db");
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
                CREATE TABLE entries (
                    id TEXT PRIMARY KEY,
                    bundle_id TEXT NOT NULL,
                    bundle_lower TEXT NOT NULL,
                    title TEXT, version TEXT, min_os TEXT, min_os_num INTEGER,
                    size INTEGER, size_exact INTEGER, device_mask INTEGER,
                    icon TEXT, category TEXT, subgenre TEXT, developer TEXT
                );
                CREATE TABLE sources (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    entry_id TEXT NOT NULL,
                    source_id TEXT,
                    url TEXT NOT NULL,
                    page TEXT,
                    size INTEGER,
                    sha256 TEXT
                );
                CREATE TABLE descriptions (
                    entry_id TEXT NOT NULL,
                    lang TEXT NOT NULL,
                    text TEXT NOT NULL
                );
                INSERT INTO meta VALUES ('schema_version', '1');
                INSERT INTO entries VALUES ('e1', 'com.smoke.demo', 'com.smoke.demo', 'Smoke Demo', '1.0', '6.0', 60000, 1024, 1, 2, '', 'Games', '', 'Dev');
                INSERT INTO sources (entry_id, source_id, url, page, size, sha256) VALUES ('e1', 'm1', 'http://127.0.0.1/demo.ipa', '', 1024, NULL);
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        return dbPath;
    }

    private static void CloseWindowAndPump(Window window)
    {
        var isClosed = false;
        var frame = new DispatcherFrame();
        window.Closed += (_, _) =>
        {
            isClosed = true;
            frame.Continue = false;
        };
        window.Close();
        if (isClosed) return;

        var timeout = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        timeout.Tick += (_, _) =>
        {
            timeout.Stop();
            frame.Continue = false;
        };
        timeout.Start();
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timeout.Stop();
        }
    }

    private static int CountVisualCheckBoxes(DependencyObject root)
    {
        var count = root is CheckBox ? 1 : 0;
        var childrenCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childrenCount; i++)
            count += CountVisualCheckBoxes(VisualTreeHelper.GetChild(root, i));
        return count;
    }

    private static int CountLogicalCheckBoxes(DependencyObject root)
    {
        var count = root is CheckBox ? 1 : 0;
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is DependencyObject dep)
                count += CountLogicalCheckBoxes(dep);
        }
        return count;
    }

    private static void RunOnStaWithBindingCheck(string cultureName, Action action)
    {
        Exception? caught = null;
        var bindingErrors = new List<string>();

        var thread = new Thread(() =>
        {
            var prevCulture = Thread.CurrentThread.CurrentCulture;
            var prevUiCulture = Thread.CurrentThread.CurrentUICulture;
            var culture = CultureInfo.GetCultureInfo(cultureName);
            var bindingSource = PresentationTraceSources.DataBindingSource;
            var prevLevel = bindingSource.Switch.Level;
            var listener = new BindingErrorListener(bindingErrors);

            try
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                Thread.CurrentThread.CurrentCulture = culture;
                Thread.CurrentThread.CurrentUICulture = culture;
                using var _ = IndexStrings.UseCultureScope(culture);

                bindingSource.Switch.Level = SourceLevels.Error;
                bindingSource.Listeners.Add(listener);

                action();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
            finally
            {
                bindingSource.Listeners.Remove(listener);
                bindingSource.Switch.Level = prevLevel;
                try
                {
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
                catch
                {
                    // Ignore dispatcher shutdown cleanup errors
                }
                Thread.CurrentThread.CurrentCulture = prevCulture;
                Thread.CurrentThread.CurrentUICulture = prevUiCulture;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (caught is not null)
            ExceptionDispatchInfo.Capture(caught).Throw();

        Assert.True(
            bindingErrors.Count == 0,
            $"WPF data-binding errors under culture '{cultureName}':\n" + string.Join("\n", bindingErrors));
    }

    private sealed class BindingErrorListener(List<string> errors) : TraceListener
    {
        public override void Write(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
                errors.Add(message);
        }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
                errors.Add(message);
        }

        public override void TraceEvent(
            TraceEventCache? eventCache,
            string source,
            TraceEventType eventType,
            int id,
            string? message)
        {
            if (eventType <= TraceEventType.Error && !string.IsNullOrWhiteSpace(message))
                errors.Add($"[{eventType}] {message}");
        }

        public override void TraceEvent(
            TraceEventCache? eventCache,
            string source,
            TraceEventType eventType,
            int id,
            string? format,
            params object?[]? args)
        {
            if (eventType > TraceEventType.Error || string.IsNullOrWhiteSpace(format))
                return;
            var formatted = args is { Length: > 0 }
                ? string.Format(CultureInfo.InvariantCulture, format, args)
                : format;
            errors.Add($"[{eventType}] {formatted}");
        }
    }
}
