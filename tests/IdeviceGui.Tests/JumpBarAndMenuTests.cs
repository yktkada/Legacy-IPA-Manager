using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using IdeviceGui.Resources;
using IdeviceGui.Services;
using IdeviceGui.Services.Data;
using IdeviceGui.ViewModels;
using IdeviceGui.ViewModels.Library;
using IdeviceGui.Views;
using IdeviceIndex;
using Microsoft.Data.Sqlite;
using Xunit;
using GuiJumpBar = IdeviceGui.Views.AlphabetJumpBar;
using GuiJumpBuckets = IdeviceGui.ViewModels.Library.AlphabetBuckets;
using GuiJumpTier = IdeviceGui.Views.JumpBarLayoutTier;
using IndexJumpBar = IdeviceIndex.AlphabetJumpBar;
using IndexJumpBuckets = IdeviceIndex.AlphabetBuckets;
using IndexJumpTier = IdeviceIndex.JumpBarLayoutTier;
using IndexWindow = IdeviceIndex.MainWindow;

namespace IdeviceGui.Tests;

[Collection("WpfUiSmoke")]
public sealed class JumpBarAndMenuTests
{
    private static void RunOnSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            var prevCtx = SynchronizationContext.Current;
            try
            {
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(
                    new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(prevCtx);
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (captured is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(captured).Throw();
    }

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(20),
        };
        var sw = Stopwatch.StartNew();
        timer.Tick += (_, _) =>
        {
            if (condition() || sw.Elapsed >= timeout)
            {
                timer.Stop();
                frame.Continue = false;
            }
        };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        Assert.True(condition(), $"Timed out after {timeout.TotalSeconds:F1}s");
    }

    [Fact]
    public void AlphabetBuckets_ClassifiesDigitsLettersChinesePinyinAndSymbols_IdenticallyInGuiAndIndex()
    {
        var cases = new (string Input, string Expected)[]
        {
            ("007", "0"),
            ("99 Bottles", "9"),
            ("５G工具", "5"),
            ("apple", "A"),
            ("Zoom", "Z"),
            ("Ｗhat", "W"),
            ("微信", "W"),
            ("支付宝", "Z"),
            ("抖音", "D"),
            ("百度", "B"),
            ("饿了么", "E"),
            ("愛奇藝", "A"),
            ("遊戲中心", "Y"),
            ("@special", "#"),
            ("", "#"),
            ("   ", "#"),
        };

        foreach (var (input, expected) in cases)
        {
            Assert.Equal(expected, GuiJumpBuckets.ClassifyLabel(input));
            Assert.Equal(expected, IndexJumpBuckets.ClassifyLabel(input));
        }

        // CompareNames puts 0-9 -> A-Z (English + Pinyin mixed together) -> #
        var list = new[] { "@App", "支付宝", "Apple", "微信", "007", "爱思", "WhatsApp" };
        var sorted = list.OrderBy(x => x, Comparer<string>.Create(GuiJumpBuckets.CompareNames)).ToArray();
        Assert.Equal("007", sorted[0]);
        Assert.Equal("@App", sorted[^1]);
        var idxApple = Array.IndexOf(sorted, "Apple");
        var idxAiSi = Array.IndexOf(sorted, "爱思");
        var idxWhatsApp = Array.IndexOf(sorted, "WhatsApp");
        var idxWeChat = Array.IndexOf(sorted, "微信");
        Assert.True(Math.Abs(idxApple - idxAiSi) == 1);
        Assert.True(Math.Abs(idxWhatsApp - idxWeChat) == 1);
        Assert.True(idxAiSi < idxWhatsApp);
    }

    [Fact]
    public void AlphabetJumpBar_FollowsExactFourTierAdaptivePriority()
    {
        RunOnSta(() =>
        {
            var bar = new GuiJumpBar();
            var counts = new int[GuiJumpBuckets.BucketCount];
            counts[10] = 3; // A
            counts[11] = 2; // B
            counts[32] = 5; // W
            counts[36] = 1; // #
            bar.UpdateAvailability(counts);

            // Tier 0: Full width -> all 37 visible, unavailable disabled
            bar.ApplyResponsiveLayoutForTests(1600);
            Assert.Equal(GuiJumpTier.Full, bar.CurrentTierForTests);
            Assert.All(bar.ButtonsForTests, b => Assert.Equal(Visibility.Visible, b.Visibility));
            Assert.False(bar.ButtonsForTests[0].IsEnabled);
            Assert.True(bar.ButtonsForTests[10].IsEnabled);

            // Tier 1: Medium width -> hide empty buttons first while keeping active visible
            bar.ApplyResponsiveLayoutForTests(360);
            Assert.Equal(GuiJumpTier.HideEmpty, bar.CurrentTierForTests);
            Assert.Equal(Visibility.Collapsed, bar.ButtonsForTests[0].Visibility);
            Assert.Equal(Visibility.Visible, bar.ButtonsForTests[10].Visibility);

            // Tier 2 / 3: Very tight with many active buckets -> Compact then Scroll
            Array.Fill(counts, 1);
            bar.UpdateAvailability(counts);
            bar.ApplyResponsiveLayoutForTests(760);
            Assert.Equal(GuiJumpTier.Compact, bar.CurrentTierForTests);

            bar.ApplyResponsiveLayoutForTests(120);
            Assert.Equal(GuiJumpTier.Scroll, bar.CurrentTierForTests);

            // Index build of jump bar mirrors identical tiers
            var indexBar = new IndexJumpBar();
            indexBar.UpdateAvailability(counts);
            indexBar.ApplyResponsiveLayoutForTests(120);
            Assert.Equal(IndexJumpTier.Scroll, indexBar.CurrentTierForTests);
        });
    }

    [Fact]
    public async Task DeleteMissingRecordsAsync_DetectsDiskDeletesWithoutManualScan_AndPreservesUnreachableRoot()
    {
        using var ws = new TestWorkspace("delete-missing-bug");
        var prevData = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryEnvVar);
        Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, ws.Root);
        try
        {
            var reachableDir = Path.Combine(ws.Root, "reachable");
            var unreachableDir = Path.Combine(ws.Root, "unplugged_usb_drive");
            Directory.CreateDirectory(reachableDir);
            Directory.CreateDirectory(unreachableDir);
            var liveFile = Path.Combine(reachableDir, "live.ipa");
            var deletedFile = Path.Combine(reachableDir, "gone.ipa");
            File.WriteAllBytes(liveFile, [1, 2, 3]);
            // Note: deletedFile deliberately does NOT exist on disk even though DB still thinks ExistsFlag=1.
            var unreachableFile = Path.Combine(unreachableDir, "keep.ipa");

            var vm = new MainViewModel(new FakeCliRunner(), new FakeAfcHelper(), scanRootOverride: reachableDir, pipeNameOverride: "");
            await vm.OpenDatabaseForTestAsync();
            var db = vm.DatabaseForTest!;

            var (reachRoot, e1) = ScanRootStore.Add(db, reachableDir);
            Assert.Null(e1);
            var (unreachRoot, e2) = ScanRootStore.Add(db, unreachableDir);
            Assert.Null(e2);
            // Simulate external drive unplugged after adding scan root.
            Directory.Delete(unreachableDir);

            PackageStore.Save(db, [
                new PackageRow
                {
                    ScanRootId = reachRoot!.Id,
                    FilePath = liveFile,
                    NormalizedPath = liveFile.ToLowerInvariant(),
                    FileName = "live.ipa",
                    DisplayName = "LiveApp",
                    BundleId = "com.test.live",
                    IdentityKind = IdentityKind.PathFallback,
                    ParseState = ParseStates.Ok,
                    ExistsFlag = true,
                },
                new PackageRow
                {
                    ScanRootId = reachRoot.Id,
                    FilePath = deletedFile,
                    NormalizedPath = deletedFile.ToLowerInvariant(),
                    FileName = "gone.ipa",
                    DisplayName = "GoneApp",
                    BundleId = "com.test.gone",
                    IdentityKind = IdentityKind.PathFallback,
                    ParseState = ParseStates.Ok,
                    ExistsFlag = true, // Simulate deleted on disk after last scan!
                },
                new PackageRow
                {
                    ScanRootId = unreachRoot!.Id,
                    FilePath = unreachableFile,
                    NormalizedPath = unreachableFile.ToLowerInvariant(),
                    FileName = "keep.ipa",
                    DisplayName = "UnpluggedApp",
                    BundleId = "com.test.keep",
                    IdentityKind = IdentityKind.PathFallback,
                    ParseState = ParseStates.Ok,
                    ExistsFlag = true,
                },
            ]);

            var allBefore = PackageStore.LoadAll(db);
            vm.LibraryViewForTest.ReplaceAll(allBefore);
            Assert.Equal(3, allBefore.Count);

            // DeleteMissingRecordsAsync must reconcile reachable disk deletions & remove immediately!
            var removed = await vm.DeleteMissingRecordsAsync();
            Assert.Equal(1, removed);

            var remainingDb = PackageStore.LoadAll(db);
            Assert.Equal(2, remainingDb.Count);
            Assert.DoesNotContain(remainingDb, r => r.BundleId == "com.test.gone");
            Assert.Contains(remainingDb, r => r.BundleId == "com.test.keep");
            Assert.DoesNotContain(vm.LibraryRows, r => r.BundleId == "com.test.gone");

            await vm.ShutdownAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, prevData);
        }
    }

    [Fact]
    public void MainWindow_ContextMenu_NestsAllCopyItems_RemovesClearAndAddRoot_HasUniqueShortcutTags()
    {
        using var ws = new TestWorkspace("main-menu-refactor");
        var prevData = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryEnvVar);
        Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, ws.Root);
        try
        {
            RunOnSta(() =>
            {
                var window = new IdeviceGui.Views.MainWindow(new FakeCliRunner(), new FakeAfcHelper(), ws.Root);
                try
                {
                    var menu = window.SingleLibraryContextMenu;
                    var topItems = menu.Items.OfType<MenuItem>().ToList();

                    // ClearMarks and AddScanFolder removed
                    Assert.DoesNotContain(topItems, i => i.Header?.ToString()?.Contains("清除") == true);
                    Assert.DoesNotContain(topItems, i => i.Header?.ToString()?.Contains("添加扫描") == true);

                    // Copy parent contains 7 copy actions
                    var copyParent = Assert.Single(topItems, i => Equals(i.Tag, "C"));
                    var copyChildren = copyParent.Items.OfType<MenuItem>().ToList();
                    Assert.Equal(7, copyChildren.Count);
                    Assert.Equal(new[] { "B", "N", "V", "O", "F", "P", "R" }, copyChildren.Select(c => (string)c.Tag).ToArray());

                    // Open folder has single-letter badge E in left icon slot and no Ctrl+Alt gesture text
                    var forceInstall = Assert.Single(topItems, i => Equals(i.Tag, "I"));
                    Assert.DoesNotContain("跳过", forceInstall.Header?.ToString() ?? "");

                    var openFolder = Assert.Single(topItems, i => Equals(i.Tag, "E"));
                    Assert.Equal("", openFolder.InputGestureText);
                    Assert.Equal("E", Assert.IsType<TextBlock>(openFolder.Icon).Text);

                    // Every tagged item uses left icon badge without trailing (X) or Ctrl+Alt+
                    foreach (var item in topItems.Concat(copyChildren))
                    {
                        if (item.Tag is string tag && !string.IsNullOrEmpty(tag))
                        {
                            Assert.Equal("", item.InputGestureText);
                            Assert.Equal(tag, Assert.IsType<TextBlock>(item.Icon).Text);
                        }
                    }

                    // Top-level menu shortcut tags are all unique
                    var topTags = topItems.Select(i => i.Tag?.ToString()).Where(t => !string.IsNullOrEmpty(t)).ToList();
                    Assert.Equal(topTags.Count, topTags.Distinct(StringComparer.OrdinalIgnoreCase).Count());
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, prevData);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void PerfAcceptance_Index200kAndMain50k_MeetVirtualListAndJumpBarTargets()
    {
        using var ws = new TestWorkspace("perf-200k-50k");
        var dbPath = ws.Full("index200k.db");

        // 1. Seed 200,000 entries in SQLite index.db
        var sampleTitles = new[]
        {
            "Alpha", "Beta", "微信", "支付宝", "抖音", "百度", "1Password", "007", "@Tool", "Zeta",
            "愛奇藝", "网易云音乐", "淘宝", "京东", "小红书", "饿了么", "高德地图", "Safari", "Minecraft", "Game"
        };
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
        {
            conn.Open();
            using var setup = conn.CreateCommand();
            setup.CommandText = """
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
                INSERT INTO meta VALUES ('schema_version', '1');
                CREATE TABLE entries (
                    id TEXT PRIMARY KEY,
                    bundle_id TEXT NOT NULL,
                    bundle_lower TEXT NOT NULL,
                    title TEXT NOT NULL,
                    version TEXT NOT NULL,
                    min_os TEXT NOT NULL,
                    min_os_num INTEGER NOT NULL,
                    size INTEGER NOT NULL,
                    size_exact INTEGER NOT NULL DEFAULT 1,
                    device_mask INTEGER NOT NULL,
                    icon TEXT NOT NULL,
                    category TEXT NOT NULL,
                    subgenre TEXT NOT NULL,
                    developer TEXT NOT NULL
                );
                CREATE TABLE sources (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    entry_id TEXT NOT NULL,
                    source_id TEXT NOT NULL,
                    url TEXT NOT NULL,
                    page TEXT NOT NULL,
                    size INTEGER NOT NULL,
                    sha256 TEXT
                );
                CREATE TABLE descriptions (
                    entry_id TEXT NOT NULL,
                    lang TEXT NOT NULL,
                    text TEXT NOT NULL,
                    PRIMARY KEY (entry_id, lang)
                );
                CREATE INDEX ix_entries_bundle ON entries(bundle_lower);
                CREATE INDEX ix_entries_title ON entries(title COLLATE NOCASE);
                CREATE INDEX ix_sources_entry ON sources(entry_id);
                """;
            setup.ExecuteNonQuery();

            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO entries VALUES ($id, $b, $bl, $t, '1.0', '6.0', 60000, 1048576, 1, 2, '', 'Games', '', 'Dev');
                """;
            var pId = cmd.Parameters.Add("$id", SqliteType.Text);
            var pB = cmd.Parameters.Add("$b", SqliteType.Text);
            var pBl = cmd.Parameters.Add("$bl", SqliteType.Text);
            var pT = cmd.Parameters.Add("$t", SqliteType.Text);
            for (var i = 0; i < 200_000; i++)
            {
                var id = $"e{i}";
                pId.Value = id;
                pB.Value = $"com.perf.app{i}";
                pBl.Value = $"com.perf.app{i}";
                pT.Value = $"{sampleTitles[i % sampleTitles.Length]}_{i}";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        SqliteConnection.ClearAllPools();

        // 2. Measure 200k Index database load + parallel Pinyin sort + JumpIndex + BulkObservableCollection bind
        var db = new IndexDatabase(dbPath);
        var swQuery = Stopwatch.StartNew();
        var loaded = db.Search(new IndexQuery("", 0, 0, IndexSort.Title, false), limit: 0, offset: 0, includeSourceCount: false, skipSqlOrder: true);
        swQuery.Stop();
        Assert.Equal(200_000, loaded.Count);

        var swSort = Stopwatch.StartNew();
        var sorted = IndexWindow.SortRowsByPinyinName(loaded, descending: false);
        var counts = new int[IndexJumpBuckets.BucketCount];
        var firstIndices = new int[IndexJumpBuckets.BucketCount];
        IndexJumpBuckets.BuildIndex(sorted, static r => r.BaseTitle, null, counts, firstIndices);
        swSort.Stop();

        var swBind = Stopwatch.StartNew();
        var bulk = new BulkObservableCollection<IndexRow>();
        bulk.ReplaceAll(sorted);
        swBind.Stop();
        Assert.Equal(200_000, bulk.Count);
        Assert.True(counts[IndexJumpBuckets.ClassifyIndex("微信")] > 0);

        // 3. Measure 50,000 rows in IdeviceGui LibraryView (Pinyin Name sort + Build + JumpIndex)
        var libRows = new List<PackageRow>(50_000);
        for (var i = 0; i < 50_000; i++)
        {
            libRows.Add(new PackageRow
            {
                Id = i + 1,
                FilePath = $@"C:\lib\{i}.ipa",
                FileName = $"{i}.ipa",
                DisplayName = $"{sampleTitles[i % sampleTitles.Length]}_{i}",
                BundleId = $"com.main.app{i}",
                Version = "1.0",
                MinOs = "5.0",
                AppSize = 10_000_000,
                FileSize = 5_000_000,
                ParseState = ParseStates.Ok,
                ExistsFlag = true,
            });
        }

        var libView = new LibraryView();
        libView.ReplaceAll(libRows);
        libView.Sort = new LibrarySort(LibrarySortField.Name, Descending: false);

        var sw50kBuild = Stopwatch.StartNew();
        var built50k = libView.Build();
        var counts50k = new int[GuiJumpBuckets.BucketCount];
        var first50k = new int[GuiJumpBuckets.BucketCount];
        GuiJumpBuckets.BuildIndex(built50k, static r => r.NameText, static r => !r.IsAppendedSeparator && !r.IsMissing, counts50k, first50k);
        sw50kBuild.Stop();
        Assert.Equal(50_000, built50k.Count);

        // 4. End-to-end STA WPF IdeviceIndex.MainWindow loading 200,000 rows via ReloadAsync()
        var swUi200k = Stopwatch.StartNew();
        var defaultSettingsPath = IndexerSettings.DefaultPath;
        var hadDefaultSettings = File.Exists(defaultSettingsPath);
        byte[]? prevSettings = hadDefaultSettings ? File.ReadAllBytes(defaultSettingsPath) : null;
        try
        {
            IndexerSettings.SaveDisplayLimit(0, defaultSettingsPath);
            RunOnSta(() =>
            {
                var win = new IndexWindow(dbPath)
                {
                    Left = -32000,
                    Top = -32000,
                    Width = 900,
                    Height = 640,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                };
                try
                {
                    win.Show();
                    PumpUntil(() => win.RowsForTests.Count == 200_000, TimeSpan.FromSeconds(15));
                    swUi200k.Stop();
                }
                finally
                {
                    win.Close();
                }
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (hadDefaultSettings && prevSettings is not null)
                File.WriteAllBytes(defaultSettingsPath, prevSettings);
            else if (!hadDefaultSettings && File.Exists(defaultSettingsPath))
                try { File.Delete(defaultSettingsPath); } catch { }
        }

        Console.WriteLine(
            $"[PERF] Index200k query={swQuery.ElapsedMilliseconds}ms sort+bucket={swSort.ElapsedMilliseconds}ms bulkBind={swBind.ElapsedMilliseconds}ms uiWindowLoad={swUi200k.ElapsedMilliseconds}ms | Main50k build+bucket={sw50kBuild.ElapsedMilliseconds}ms");
    }
}
