using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IdeviceIndex.Resources;
using Microsoft.Win32;

namespace IdeviceIndex;

public partial class MainWindow : Window
{
    private IndexDatabase? _db;
    private readonly BulkObservableCollection<IndexRow> _rows = new();
    private readonly int[] _jumpCounts = new int[AlphabetBuckets.BucketCount];
    private readonly int[] _jumpFirstIndices = new int[AlphabetBuckets.BucketCount];
    private int? _pendingJumpBucket;
    private readonly DownloadQueueStore _queue = new();
    private readonly DownloadEngine _downloadEngine;
    private readonly MainAppPipeClient _pipeClient = new();
    private readonly DispatcherTimer _debounce;
    private readonly SemaphoreSlim _iconGate = new(4, 4);
    private readonly HashSet<IndexRow> _iconPending = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _expandingGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _windowCts = new();
    private readonly HashSet<string> _selectedIds = new(StringComparer.Ordinal);
    private readonly Dictionary<DataGridColumn, string> _columnKeys = new();
    private readonly GridSplitter _rightSectionSplitter;

    private IReadOnlyList<string> _knownScanRoots = [];
    private bool _mainAppConnected;
    private bool _allowOutsideWrite;
    private bool _rememberLastDir = true;
    private string _activeDownloadDir = DownloadSettings.DefaultDir();
    private bool _suppressDownloadDirChanged;
    private bool _suppressFilterEvents;
    private IndexLayoutState _layout = IndexLayoutState.Default;
    private double _rowHeight = IndexGridColumns.RowHeightMedium;
    private int _displayLimit = IndexerSettings.DefaultDisplayLimit;
    private bool _mergeSameBundle = true;
    private IndexSort _currentSort = IndexSort.Title;
    private IndexSearchScope _searchScope = new();
    private IndexSearchMatchMode _searchMatchMode = IndexSearchMatchMode.Contains;
    private IndexAdvancedFilterState _advancedFilter = new();
    private string _openLinkProgram = "";
    private ContextMenu? _minOsMenu;
    private bool _rightOverlayOpen;
    private bool _isMultiSelectMode;
    private bool _ready;
    private bool _aria2Ready;
    private string? _aria2Notice;
    private bool _isShuttingDown;
    private bool _shutdownCompleted;
    private bool _descending;
    private int _reloadVersion;
    private int _detailVersion;
    private int _iconFailures;
    private int _loadedSqlOffset;
    private long _activeTotal;
    private bool _hasMoreRows;
    private bool _loadingMore;

    private IndexRow? _armedRow;
    private DataGridColumn? _armedColumn;
    private long _armedAtMs;
    private DispatcherTimer? _armedTimer;

    private IndexRow? _dragCandidateRow;
    private Point _dragStartPoint;
    private bool _dragMoved;
    private HashSet<IndexRow> _baseDragSelection = new();

    private DownloadJobRow? _queueArmedJob;
    private long _queueArmedAtMs;

    internal static Action<string> OpenFolderAction { get; set; } = OpenFolderInExplorer;

    public MainWindow(string? dbPath = null)
    {
        _db = string.IsNullOrWhiteSpace(dbPath) ? null : new IndexDatabase(Path.GetFullPath(dbPath));
        InitializeComponent();

        _rightSectionSplitter = new GridSplitter
        {
            Height = IndexLayoutStore.SplitterSize,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Background = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)),
        };
        _rightSectionSplitter.MouseDoubleClick += OnRightSplitterDoubleClick;

        ResultsGrid.ItemsSource = _rows;
        QueueList.ItemsSource = _queue.Jobs;
        _queue.ApplyColumns(IndexerSettings.LoadQueueColumns());
        var progress = new Progress<DownloadProgressSnapshot>(OnDownloadProgress);
        _downloadEngine = new DownloadEngine(
            id =>
            {
                var db = Volatile.Read(ref _db);
                return db is null ? [] : db.GetSources(id);
            },
            progress)
        {
            OnDownloadCompletedAsync = (info, ct) =>
            {
                var settings = IndexerSettings.LoadDownloadSettings();
                var cachedRoots = Volatile.Read(ref _knownScanRoots);
                return ResolvePostDownloadImportNoteAsync(
                    _pipeClient,
                    info,
                    settings,
                    cachedRoots,
                    AppContext.BaseDirectory,
                    ct);
            },
        };
        _queue.OnEnqueued = job => _downloadEngine.EnqueueJob(job);
        _queue.OnCancelled = job => _ = CancelEngineJobSafeAsync(job.Id);
        _queue.OnSuspendChanged = suspended => _ = SetEngineSuspendedSafeAsync(suspended);
        RefreshQueueSummary();

        _layout = IndexerSettings.LoadLayout();
        _rowHeight = IndexerSettings.LoadGridRowHeight();
        _displayLimit = IndexerSettings.LoadDisplayLimit();
        _mergeSameBundle = IndexerSettings.LoadMergeSameBundle();
        _searchScope = IndexerSettings.LoadSearchScope();
        _searchMatchMode = IndexerSettings.LoadSearchMatchMode();
        _advancedFilter = IndexerSettings.LoadAdvancedFilter();
        _openLinkProgram = IndexerSettings.LoadOpenLinkProgram();

        _suppressFilterEvents = true;
        try
        {
            MergeBundleCheck.IsChecked = _mergeSameBundle;
            AdvancedFilterToggle.IsChecked = _advancedFilter.Enabled;
        }
        finally
        {
            _suppressFilterEvents = false;
        }
        SearchOptionsMenu.DataContext = _searchScope;
        _searchScope.PropertyChanged += OnSearchScopePropertyChanged;
        SyncSearchModeMenuChecks();
        UpdateAdvancedFilterButton();

        ResultsGrid.RowHeight = _rowHeight;
        ApplyIndexColumns(IndexerSettings.LoadGridColumns());
        ApplyRightSections();
        ApplyLayout();

        var initialDownloadSettings = IndexerSettings.LoadDownloadSettings();
        _allowOutsideWrite = IndexerSettings.LoadAllowOutsideProgramDirWrite();
        _rememberLastDir = initialDownloadSettings.RememberLastDir;
        _activeDownloadDir = initialDownloadSettings.Dir;
        _downloadEngine.SetSessionDownloadDir(_activeDownloadDir);
        RefreshDownloadDirSelector();
        UpdateDownloadDirWarning();

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await ReloadAsync();
        };

        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;
        Closed += (_, _) =>
        {
            _debounce.Stop();
            ClearArmedRow();
            try { _windowCts.Cancel(); } catch (ObjectDisposedException) { }
            _windowCts.Dispose();
            _iconGate.Dispose();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        JumpBar.BucketClicked += OnJumpBarBucketClicked;
        StateChanged += OnWindowStateChanged;
        UpdateMultiSelectButtons();
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _ready = true;
        await RefreshDatabaseMetadataAsync(Volatile.Read(ref _db));
        if (_isShuttingDown || _shutdownCompleted) return;
        await ReloadAsync();
        if (_isShuttingDown || _shutdownCompleted) return;

        var verify = await Task.Run(() => _downloadEngine.VerifyToolchain());
        if (_isShuttingDown || _shutdownCompleted) return;

        _aria2Ready = verify.Ok;
        if (!_aria2Ready)
        {
            _aria2Notice = Strings.Format(nameof(Strings.Aria2_Disabled_BannerFormat), verify.Message ?? "");
            Aria2NoticeText.Text = _aria2Notice;
            Aria2NoticeBanner.Visibility = Visibility.Visible;
            StatusText.Text = _aria2Notice;
        }
        else
        {
            _aria2Notice = null;
            Aria2NoticeBanner.Visibility = Visibility.Collapsed;
            StatusText.Text = Strings.Format(nameof(Strings.Status_Aria2ReadyFormat), verify.Version ?? "1.37.0");
        }
        UpdateMultiSelectButtons();

        // Run pipe handshake & stale .part cleanup in background without delaying _aria2Ready / double-click enqueue (P2-20).
        _ = RunStartupHelloAsync();
        if (_aria2Ready)
            _ = RunStartupPartCleanupAsync();
    }

    private async Task RunStartupHelloAsync()
    {
        try
        {
            var hello = await Task.Run(() => _pipeClient.HelloAsync(ct: _windowCts.Token), _windowCts.Token);
            if (_isShuttingDown || _shutdownCompleted) return;
            if (hello.Ok)
            {
                _mainAppConnected = true;
                if (hello.Roots.Count > 0)
                    Volatile.Write(ref _knownScanRoots, hello.Roots);
                RefreshDownloadDirSelector();
                UpdateDownloadDirWarning();
            }
        }
        catch
        {
            // Pipe handshake failure or window close must not block UI
        }
    }

    private async Task RunStartupPartCleanupAsync()
    {
        try
        {
            var cleaned = await Task.Run(() =>
            {
                var dl = IndexerSettings.LoadDownloadSettings();
                return DownloadPathPolicy.CleanupExpiredPartFiles(dl.Dir, dl.PartTtlHours);
            }, _windowCts.Token);
            if (_isShuttingDown || _shutdownCompleted) return;
            if (cleaned > 0)
                StatusText.Text = Strings.Format(nameof(Strings.Status_CleanedStalePartsFormat), cleaned);
        }
        catch
        {
            // Stale part cleanup failure must not block UI
        }
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownCompleted) return;
        if (_isShuttingDown)
        {
            e.Cancel = true;
            return;
        }

        var activeCount = _queue.Jobs.Count(j => j.CanCancel);
        if (activeCount > 0 || _downloadEngine.HasActiveDownloads)
        {
            var displayCount = Math.Max(1, activeCount);
            var answer = MessageBox.Show(
                this,
                Strings.Format(nameof(Strings.Window_ConfirmExitActiveDownloadsFormat), displayCount),
                Strings.Window_ConfirmExitCaption,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        var layout = CaptureCurrentLayout();
        var columns = CaptureIndexColumns();

        e.Cancel = true;
        _isShuttingDown = true;
        var baseTitle = Title;
        void SetClosingStep(string step) =>
            Title = Strings.Format(nameof(Strings.Window_Closing_TitleFormat), baseTitle, step);
        try
        {
            SetClosingStep(Strings.Window_Closing_Step_SavingLayout);
            var keepParts = await Task.Run(() =>
            {
                try
                {
                    IndexerSettings.SaveWindowState(layout, _rowHeight, columns);
                }
                catch
                {
                    // 保存布局失败不阻塞关窗
                }
                return IndexerSettings.LoadDownloadSettings().KeepPartFiles;
            });
            SetClosingStep(Strings.Window_Closing_Step_StoppingDownloads);
            await _downloadEngine.ShutdownAsync(keepParts);
            SetClosingStep(Strings.Window_Closing_Step_DisposingEngine);
            await _downloadEngine.DisposeAsync();
        }
        catch
        {
            // 关窗清理异常不阻断退出
        }
        finally
        {
            _shutdownCompleted = true;
            Close();
        }
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Maximized && _rightOverlayOpen)
        {
            _rightOverlayOpen = false;
            ApplyLayout();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (key == Key.L && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ToggleRightPane();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && HandleGlobalShortcut(key))
        {
            e.Handled = true;
        }
    }

    private bool HandleGlobalShortcut(Key key)
    {
        var selected = ResultsGrid.SelectedItem as IndexRow;
        switch (key)
        {
            case Key.G:
                if (selected is null || !selected.IsGroup) return false;
                _ = ToggleGroupExpandAsync(selected);
                return true;
            case Key.Q:
                if (selected is null || selected.IsGroup || !_aria2Ready) return false;
                EnqueueSingleRow(selected);
                return true;
            case Key.S:
                if (!_aria2Ready || _selectedIds.Count == 0) return false;
                _ = EnqueueSelectedRowsAsync();
                return true;
            case Key.C:
                if (selected is null) return false;
                OpenQuickMenu(BuildCopySubmenuOnly(selected));
                return true;
            case Key.N:
                if (selected is null) return false;
                CopyFieldValue(selected.BaseTitle);
                return true;
            case Key.B:
                if (selected is null) return false;
                CopyFieldValue(selected.BundleId);
                return true;
            case Key.V:
                if (selected is null) return false;
                CopyFieldValue(selected.VersionText);
                return true;
            case Key.O:
                if (selected is null) return false;
                CopyFieldValue(selected.MinOsText);
                return true;
            case Key.H:
                OpenQuickMenu(BuildRowHeightMenuOnly());
                return true;
            case Key.Z:
                if (_selectedIds.Count == 0) return false;
                ClearMultiSelection();
                return true;
            default:
                return false;
        }
    }

    private ContextMenu BuildCopySubmenuOnly(IndexRow row)
    {
        var menu = new ContextMenu();
        menu.PreviewKeyDown += OnContextMenuPreviewKeyDown;
        menu.Items.Add(MakeCopyMenuItem(Strings.ContextMenu_CopyName, row.BaseTitle, "N"));
        menu.Items.Add(MakeCopyMenuItem(Strings.ContextMenu_CopyBundleId, row.BundleId, "B"));
        menu.Items.Add(MakeCopyMenuItem(Strings.ContextMenu_CopyVersion, row.VersionText, "V"));
        menu.Items.Add(MakeCopyMenuItem(Strings.ContextMenu_CopyMinOs, row.MinOsText, "O"));
        MenuShortcutIcons.ApplyFromTags(menu);
        return menu;
    }

    private ContextMenu BuildRowHeightMenuOnly()
    {
        var menu = new ContextMenu();
        menu.PreviewKeyDown += OnContextMenuPreviewKeyDown;
        AddRowHeightItems(menu);
        MenuShortcutIcons.ApplyFromTags(menu);
        return menu;
    }

    private void OpenQuickMenu(ContextMenu menu)
    {
        menu.PlacementTarget = ResultsGrid;
        menu.Placement = PlacementMode.Center;
        menu.IsOpen = true;
    }

    private void CopyFieldValue(string? value)
    {
        var text = value ?? "";
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            Clipboard.SetText(text);
            StatusText.Text = Strings.Format(nameof(Strings.Status_CopiedFormat), text);
        }
        catch
        {
            // Ignore clipboard lock
        }
    }

    private void OnContextMenuPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        var tag = key is >= Key.A and <= Key.Z ? key.ToString() : null;
        if (tag is null || sender is not ContextMenu rootMenu) return;

        var container = FindDeepestOpenMenuItemsControl(rootMenu);
        foreach (var obj in container.Items)
        {
            if (obj is not MenuItem item || item.Visibility != Visibility.Visible || !item.IsEnabled) continue;
            if (item.Tag is not string itemTag || !string.Equals(itemTag, tag, StringComparison.OrdinalIgnoreCase)) continue;

            if (item.HasItems)
            {
                item.IsSubmenuOpen = true;
                item.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                {
                    foreach (var child in item.Items)
                    {
                        if (child is MenuItem c && c.Visibility == Visibility.Visible && c.IsEnabled)
                        {
                            c.Focus();
                            break;
                        }
                    }
                });
            }
            else
            {
                rootMenu.IsOpen = false;
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
            }
            e.Handled = true;
            return;
        }
    }

    private static ItemsControl FindDeepestOpenMenuItemsControl(ContextMenu rootMenu)
    {
        ItemsControl current = rootMenu;
        while (true)
        {
            MenuItem? openSub = null;
            foreach (var obj in current.Items)
            {
                if (obj is MenuItem mi && mi.IsSubmenuOpen && mi.HasItems)
                {
                    openSub = mi;
                    break;
                }
            }
            if (openSub is null) return current;
            current = openSub;
        }
    }

    private void OnResultsPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Shift) return;
        var sv = FindDescendant<ScrollViewer>(ResultsGrid);
        if (sv is null) return;
        var step = e.Delta > 0 ? -48.0 : 48.0;
        sv.ScrollToHorizontalOffset(Math.Clamp(sv.HorizontalOffset + step, 0, sv.ScrollableWidth));
        e.Handled = true;
    }

    private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) return null;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) return typed;
            var hit = FindDescendant<T>(child);
            if (hit is not null) return hit;
        }
        return null;
    }

    private void OnJumpBarBucketClicked(int bucketIndex)
    {
        if (bucketIndex < 0 || bucketIndex >= AlphabetBuckets.BucketCount) return;

        if (_currentSort != IndexSort.Title || _descending)
        {
            _pendingJumpBucket = bucketIndex;
            _currentSort = IndexSort.Title;
            _descending = false;
            SyncSortIndicators();
            _ = ReloadAsync();
            return;
        }

        ScrollToBucket(bucketIndex);
    }

    private void ScrollToBucket(int bucketIndex)
    {
        if (bucketIndex < 0 || bucketIndex >= _jumpFirstIndices.Length) return;
        var rowIndex = _jumpFirstIndices[bucketIndex];
        if (rowIndex < 0 || rowIndex >= _rows.Count) return;

        var target = _rows[rowIndex];
        if (!_isMultiSelectMode) ResultsGrid.SelectedItem = target;
        if (FindDescendant<ScrollViewer>(ResultsGrid) is { CanContentScroll: true } sv)
            sv.ScrollToVerticalOffset(rowIndex);
        ResultsGrid.ScrollIntoView(target);
    }

    private void RefreshJumpCountsFromRows()
    {
        AlphabetBuckets.BuildIndex(
            _rows,
            static r => r.BaseTitle,
            static r => !r.IsGroupMember,
            _jumpCounts,
            _jumpFirstIndices);
        JumpBar.UpdateAvailability(_jumpCounts);
    }

    // ── 查询与列表加载 ────────────────────────────────────────────────────────

    internal IReadOnlyList<IndexRow> RowsForTests => _rows;
    internal HashSet<string> SelectedIdsForTests => _selectedIds;
    internal int LoadedSqlOffsetForTests => _loadedSqlOffset;
    internal bool HasMoreRowsForTests => _hasMoreRows;

    internal async Task ReloadAsync()
    {
        if (!_ready) return;
        var version = Interlocked.Increment(ref _reloadVersion);
        _loadedSqlOffset = 0;
        _activeTotal = 0;
        _hasMoreRows = false;
        _loadingMore = false;
        var db = Volatile.Read(ref _db);
        if (db is null)
        {
            ClearArmedRow();
            _expandingGroups.Clear();
            _rows.Clear();
            RefreshJumpCountsFromRows();
            CountText.Text = _mergeSameBundle
                ? Strings.Format(nameof(Strings.Toolbar_CountMergedFormat), 0, 0)
                : Strings.Format(nameof(Strings.Toolbar_CountUnmergedFormat), 0);
            StatusText.Text = Strings.Status_NoIndexHint;
            ClearDetails();
            SyncSortIndicators();
            return;
        }

        var query = BuildQuery();
        StatusText.Text = Strings.Status_Querying;
        try
        {
            var limit = _displayLimit;
            var merge = _mergeSameBundle;
            var fullLoad = limit <= 0;
            var pinyinSort = fullLoad && query.Sort == IndexSort.Title;
            var selectedIdsSnapshot = _isMultiSelectMode && _selectedIds.Count > 0
                ? _selectedIds.ToHashSet(StringComparer.Ordinal)
                : null;

            long totalEntries;
            long totalGroups;
            List<IndexRow> rows;
            var counts = new int[AlphabetBuckets.BucketCount];
            var firstIndices = new int[AlphabetBuckets.BucketCount];

            (totalEntries, totalGroups, rows) = await Task.Run(() =>
            {
                long entriesCount;
                long groupsCount;
                List<IndexRow> loaded;
                if (merge)
                {
                    loaded = db.SearchGrouped(
                        query,
                        limit,
                        offset: 0,
                        includeSourceCount: !fullLoad,
                        skipSqlOrder: pinyinSort);
                    if (fullLoad)
                    {
                        groupsCount = loaded.Count;
                        entriesCount = 0;
                        foreach (var r in loaded)
                            entriesCount += r.IsGroup ? r.GroupCount : 1;
                    }
                    else
                    {
                        (entriesCount, groupsCount) = db.CountGrouped(query);
                    }
                }
                else
                {
                    loaded = db.Search(
                        query,
                        limit,
                        offset: 0,
                        includeSourceCount: !fullLoad,
                        skipSqlOrder: pinyinSort);
                    if (fullLoad)
                    {
                        entriesCount = loaded.Count;
                        groupsCount = loaded.Count;
                    }
                    else
                    {
                        var count = db.Count(query);
                        entriesCount = count;
                        groupsCount = count;
                    }
                }

                if (pinyinSort && loaded.Count > 1)
                    loaded = SortRowsByPinyinName(loaded, query.Descending);

                if (selectedIdsSnapshot is not null)
                {
                    foreach (var r in loaded)
                    {
                        if (!r.IsGroup) r.IsMultiSelected = selectedIdsSnapshot.Contains(r.Id);
                    }
                }

                AlphabetBuckets.BuildIndex(loaded, static r => r.BaseTitle, null, counts, firstIndices);
                return (entriesCount, groupsCount, loaded);
            });

            if (version != Volatile.Read(ref _reloadVersion)) return;

            ClearArmedRow();
            _expandingGroups.Clear();
            foreach (var row in rows)
                row.Visual = row.IsGroup ? DownloadRowVisual.None : _queue.GetVisual(row.Id);
            _rows.ReplaceAll(rows);

            Array.Copy(counts, _jumpCounts, _jumpCounts.Length);
            Array.Copy(firstIndices, _jumpFirstIndices, _jumpFirstIndices.Length);
            JumpBar.UpdateAvailability(_jumpCounts);

            _loadedSqlOffset = rows.Count;
            _activeTotal = merge ? totalGroups : totalEntries;
            _hasMoreRows = limit > 0 && rows.Count > 0 && _loadedSqlOffset < _activeTotal;
            CountText.Text = merge
                ? Strings.Format(nameof(Strings.Toolbar_CountMergedFormat), totalEntries, totalGroups)
                : Strings.Format(nameof(Strings.Toolbar_CountUnmergedFormat), totalEntries);
            StatusText.Text = rows.Count == 0 ? Strings.Status_NoMatches : Strings.Status_Ready;
            ClearDetails();
            DetailHint.Text = Strings.Detail_ReadOnlyHint;
            SyncSortIndicators();

            if (_pendingJumpBucket is int jumpBucket)
            {
                _pendingJumpBucket = null;
                ScrollToBucket(jumpBucket);
            }
        }
        catch (Exception ex)
        {
            if (version == Volatile.Read(ref _reloadVersion))
                StatusText.Text = Strings.Format(nameof(Strings.Status_QueryFailedFormat), ex.Message);
        }
    }

    internal static List<IndexRow> SortRowsByPinyinName(List<IndexRow> rows, bool descending)
    {
        var buckets = new List<IndexRow>[AlphabetBuckets.BucketCount];
        for (var i = 0; i < buckets.Length; i++) buckets[i] = new List<IndexRow>();

        foreach (var r in rows)
        {
            var b = AlphabetBuckets.ClassifyIndex(r.BaseTitle);
            buckets[b].Add(r);
        }

        Parallel.For(0, buckets.Length, i =>
        {
            var list = buckets[i];
            if (list.Count <= 1) return;
            list.Sort((a, b) =>
            {
                var cmp = AlphabetBuckets.CompareNames(a.BaseTitle, b.BaseTitle);
                if (cmp != 0) return descending ? -cmp : cmp;
                return string.Compare(a.Id, b.Id, StringComparison.Ordinal);
            });
        });

        var result = new List<IndexRow>(rows.Count);
        if (descending)
        {
            for (var i = buckets.Length - 1; i >= 0; i--) result.AddRange(buckets[i]);
        }
        else
        {
            for (var i = 0; i < buckets.Length; i++) result.AddRange(buckets[i]);
        }
        return result;
    }

    private void OnResultsScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_ready || _displayLimit <= 0 || !_hasMoreRows || _loadingMore || _isShuttingDown || _shutdownCompleted)
            return;
        if (e.VerticalChange <= 0 || e.ExtentHeight <= 0 || e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 48)
            return;

        _ = LoadMoreRowsAsync();
    }

    internal async Task LoadMoreRowsAsync()
    {
        if (!_ready || _displayLimit <= 0 || !_hasMoreRows || _loadingMore || _isShuttingDown || _shutdownCompleted)
            return;

        var db = Volatile.Read(ref _db);
        if (db is null) return;

        _loadingMore = true;
        var version = Volatile.Read(ref _reloadVersion);
        var offset = _loadedSqlOffset;
        var limit = _displayLimit;
        var merge = _mergeSameBundle;
        var query = BuildQuery();

        try
        {
            var batch = await Task.Run(
                () => merge ? db.SearchGrouped(query, limit, offset) : db.Search(query, limit, offset),
                _windowCts.Token);
            if (_isShuttingDown || _shutdownCompleted || version != Volatile.Read(ref _reloadVersion))
                return;

            foreach (var row in batch)
            {
                row.IsMultiSelected = _isMultiSelectMode && !row.IsGroup && _selectedIds.Contains(row.Id);
                row.Visual = row.IsGroup ? DownloadRowVisual.None : _queue.GetVisual(row.Id);
                _rows.Add(row);
            }

            _loadedSqlOffset += batch.Count;
            _hasMoreRows = batch.Count >= limit && _loadedSqlOffset < _activeTotal;
            RefreshJumpCountsFromRows();
        }
        catch (OperationCanceledException)
        {
            // Window closing
        }
        catch (Exception ex)
        {
            if (!_isShuttingDown && !_shutdownCompleted && version == Volatile.Read(ref _reloadVersion))
                StatusText.Text = Strings.Format(nameof(Strings.Status_QueryFailedFormat), ex.Message);
        }
        finally
        {
            _loadingMore = false;
        }
    }

    private IndexQuery BuildQuery()
    {
        var minOsText = MinOsBox.Text?.Trim() ?? "";
        var minOsNum = 0;
        if (minOsText.Length > 0 && IosVersionCatalog.TryParseVersionNum(minOsText, out var parsedOs))
            minOsNum = parsedOs;

        var opTag = (MinOsOpCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var minOsOp = IosVersionCatalog.ParseOperator(opTag);

        return new IndexQuery(
            SearchBox.Text ?? "",
            TagInt(DeviceFilter),
            minOsNum,
            _currentSort,
            _descending,
            minOsOp,
            _searchScope.Clone(),
            _searchMatchMode,
            _advancedFilter.Clone());
    }

    private static int TagInt(ComboBox combo)
    {
        var text = (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return int.TryParse(text, out var value) ? value : 0;
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnSearchOptionsClick(object sender, RoutedEventArgs e)
    {
        SearchOptionsMenu.PlacementTarget = SearchOptionsButton;
        SearchOptionsMenu.IsOpen = true;
    }

    private void OnSearchScopePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressFilterEvents || e.PropertyName is nameof(IndexSearchScope.IsEmpty) or nameof(IndexSearchScope.Fields))
            return;

        var snapshot = _searchScope.Clone();
        _ = Task.Run(() =>
        {
            try { IndexerSettings.SaveSearchScope(snapshot); } catch { }
        });
        if (_ready) _ = ReloadAsync();
    }

    private void OnSearchModeContains(object sender, RoutedEventArgs e) => SetSearchMatchMode(IndexSearchMatchMode.Contains);
    private void OnSearchModePrefix(object sender, RoutedEventArgs e) => SetSearchMatchMode(IndexSearchMatchMode.Prefix);
    private void OnSearchModeExact(object sender, RoutedEventArgs e) => SetSearchMatchMode(IndexSearchMatchMode.Exact);

    private void SetSearchMatchMode(IndexSearchMatchMode mode)
    {
        _searchMatchMode = mode;
        SyncSearchModeMenuChecks();
        _ = Task.Run(() =>
        {
            try { IndexerSettings.SaveSearchMatchMode(mode); } catch { }
        });
        if (_ready) _ = ReloadAsync();
    }

    private void SyncSearchModeMenuChecks()
    {
        ModeContains.IsChecked = _searchMatchMode == IndexSearchMatchMode.Contains;
        ModePrefix.IsChecked = _searchMatchMode == IndexSearchMatchMode.Prefix;
        ModeExact.IsChecked = _searchMatchMode == IndexSearchMatchMode.Exact;
    }

    private void OnAdvancedFilterToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressFilterEvents) return;
        _advancedFilter.Enabled = AdvancedFilterToggle.IsChecked == true;
        UpdateAdvancedFilterButton();
        var snapshot = _advancedFilter.Clone();
        _ = Task.Run(() =>
        {
            try { IndexerSettings.SaveAdvancedFilter(snapshot); } catch { }
        });
        if (_ready) _ = ReloadAsync();
    }

    private void OnAdvancedFilterClick(object sender, RoutedEventArgs e)
    {
        var dialog = new IndexAdvancedFilterWindow(_advancedFilter, applied =>
        {
            _advancedFilter = applied;
            if (_advancedFilter.Groups.Count > 0 && !_advancedFilter.Enabled)
                _advancedFilter.Enabled = true;
            _suppressFilterEvents = true;
            try
            {
                AdvancedFilterToggle.IsChecked = _advancedFilter.Enabled;
            }
            finally
            {
                _suppressFilterEvents = false;
            }
            UpdateAdvancedFilterButton();
            var snapshot = _advancedFilter.Clone();
            _ = Task.Run(() =>
            {
                try { IndexerSettings.SaveAdvancedFilter(snapshot); } catch { }
            });
            if (_ready) _ = ReloadAsync();
        }, this);
        dialog.ShowDialog();
    }

    private void UpdateAdvancedFilterButton()
    {
        var count = _advancedFilter.ActiveConditionCount;
        AdvancedFilterButton.Content = count > 0
            ? Strings.Format(nameof(Strings.Filter_ButtonCountFormat), count)
            : Strings.Filter_Button;
        AdvancedFilterButton.FontWeight = count > 0 ? FontWeights.Bold : FontWeights.Normal;
    }

    private void OnMinOsOpChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _suppressFilterEvents) return;
        _ = ReloadAsync();
    }

    private void OnMinOsTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateMinOsValidationVisual();
        if (!_ready || _suppressFilterEvents) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void UpdateMinOsValidationVisual()
    {
        var raw = MinOsBox.Text?.Trim() ?? "";
        var valid = raw.Length == 0 || IosVersionCatalog.TryParseVersionNum(raw, out _);
        MinOsBorder.BorderBrush = valid
            ? new SolidColorBrush(Color.FromRgb(0xAB, 0xAD, 0xB3))
            : new SolidColorBrush(Color.FromRgb(0xD9, 0x30, 0x25));
        MinOsBorder.ToolTip = valid ? Strings.MinOs_InputToolTip : Strings.MinOs_InvalidTooltip;
    }

    private void OnMinOsDropdownClick(object sender, RoutedEventArgs e)
    {
        _minOsMenu ??= BuildMinOsMenu();
        _minOsMenu.PlacementTarget = MinOsBorder;
        _minOsMenu.IsOpen = true;
    }

    internal ContextMenu BuildMinOsMenu()
    {
        var menu = new ContextMenu();
        var anyItem = new MenuItem { Header = Strings.MinOs_Any };
        anyItem.Click += (_, _) =>
        {
            MinOsBox.Text = "";
            menu.IsOpen = false;
        };
        menu.Items.Add(anyItem);
        menu.Items.Add(new Separator());

        foreach (var group in IosVersionCatalog.Groups)
        {
            var majorMinor = group.MajorMinor;
            var parent = new MenuItem { Header = majorMinor };
            if (group.Revisions.Count > 0)
            {
                foreach (var rev in group.Revisions)
                {
                    var revCopy = rev;
                    var child = new MenuItem { Header = revCopy };
                    child.Click += (_, _) =>
                    {
                        MinOsBox.Text = revCopy;
                        menu.IsOpen = false;
                    };
                    parent.Items.Add(child);
                }

                parent.MouseEnter += (_, _) =>
                {
                    if (!parent.IsSubmenuOpen) parent.IsSubmenuOpen = true;
                };
                parent.PreviewMouseLeftButtonUp += (_, args) =>
                {
                    if (ReferenceEquals(FindFirstMenuItem(args.OriginalSource), parent))
                    {
                        MinOsBox.Text = majorMinor;
                        menu.IsOpen = false;
                        args.Handled = true;
                    }
                };
            }
            else
            {
                parent.Click += (_, _) =>
                {
                    MinOsBox.Text = majorMinor;
                    menu.IsOpen = false;
                };
            }
            menu.Items.Add(parent);
        }

        return menu;
    }

    private static MenuItem? FindFirstMenuItem(object? source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is MenuItem mi) return mi;
            if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
                current = VisualTreeHelper.GetParent(current);
            else
                current = LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private void OnMergeBundleChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressFilterEvents) return;
        _mergeSameBundle = MergeBundleCheck.IsChecked == true;
        var mergeSnapshot = _mergeSameBundle;
        _ = Task.Run(() =>
        {
            try { IndexerSettings.SaveMergeSameBundle(mergeSnapshot); } catch { }
        });
        if (_ready) _ = ReloadAsync();
    }

    private async void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        await ReloadAsync();
    }

    private async void OnResultsGridSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (!_columnKeys.TryGetValue(e.Column, out var key)) return;
        var targetSort = SortFromColumnKey(key);
        if (targetSort is null) return;

        if (_currentSort == targetSort.Value)
        {
            _descending = !_descending;
        }
        else
        {
            _currentSort = targetSort.Value;
            _descending = targetSort.Value == IndexSort.Size;
        }
        SyncSortIndicators();
        await ReloadAsync();
    }

    private static IndexSort? SortFromColumnKey(string key) => key switch
    {
        IndexGridColumns.Title => IndexSort.Title,
        IndexGridColumns.BundleId => IndexSort.BundleId,
        IndexGridColumns.Version => IndexSort.Version,
        IndexGridColumns.MinOs => IndexSort.MinOs,
        IndexGridColumns.DeviceFamily => IndexSort.DeviceFamily,
        IndexGridColumns.Size => IndexSort.Size,
        IndexGridColumns.Category => IndexSort.Category,
        _ => null,
    };

    private void SyncSortIndicators()
    {
        var direction = _descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        foreach (var column in ResultsGrid.Columns)
        {
            if (!_columnKeys.TryGetValue(column, out var key)) continue;
            var colSort = SortFromColumnKey(key);
            column.SortDirection = colSort == _currentSort ? direction : null;
        }
    }

    // ── 列配置与行高（对齐 LibraryColumns / MainWindow.xaml.cs） ───────────────

    private void ApplyIndexColumns(IReadOnlyList<IndexGridColumnState> states)
    {
        ResultsGrid.Columns.Clear();
        _columnKeys.Clear();

        foreach (var state in states)
        {
            var column = BuildColumn(state.Key);
            if (column is null) continue;
            column.Width = new DataGridLength(state.Width, DataGridLengthUnitType.Pixel);
            column.Visibility = state.Visible ? Visibility.Visible : Visibility.Collapsed;
            var spec = IndexGridColumns.Find(state.Key);
            if (spec is not null) column.MinWidth = spec.MinWidth;
            _columnKeys[column] = state.Key;
            ResultsGrid.Columns.Add(column);
        }

        ApplyIconColumnWidth();
        SyncSortIndicators();
    }

    private DataGridColumn? BuildColumn(string key) => key switch
    {
        IndexGridColumns.Icon => BuildIconColumn(),
        IndexGridColumns.Title => BuildTextColumn(key, Strings.Column_Title, nameof(IndexRow.DisplayTitle), canSort: true),
        IndexGridColumns.BundleId => BuildTextColumn(key, "BundleId", nameof(IndexRow.BundleId), canSort: true),
        IndexGridColumns.Version => BuildTextColumn(key, Strings.Column_Version, nameof(IndexRow.VersionText), canSort: true),
        IndexGridColumns.MinOs => BuildTextColumn(key, Strings.Column_MinOs, nameof(IndexRow.MinOsText), canSort: true),
        IndexGridColumns.DeviceFamily => BuildTextColumn(key, Strings.Column_DeviceFamily, nameof(IndexRow.DeviceFamilyText), canSort: true),
        IndexGridColumns.Size => BuildTextColumn(key, Strings.Column_Size, nameof(IndexRow.SizeText), canSort: true, rightAlign: true),
        IndexGridColumns.Category => BuildTextColumn(key, Strings.Column_Category, nameof(IndexRow.CategoryText), canSort: true),
        _ => null,
    };

    private DataGridColumn BuildIconColumn() => new DataGridTemplateColumn
    {
        Header = Strings.Column_Icon,
        CellTemplate = (DataTemplate)FindResource("IconCellTemplate"),
        CanUserSort = false,
        CanUserResize = false,
        SortMemberPath = IndexGridColumns.Icon,
    };

    private DataGridTextColumn BuildTextColumn(string key, string header, string path, bool canSort, bool rightAlign = false)
    {
        var baseStyle = (Style)FindResource("IndexTextStyle");
        var style = new Style(typeof(TextBlock), baseStyle);
        style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(path) { Mode = BindingMode.OneWay }));
        if (key == IndexGridColumns.Title)
        {
            var indentTrigger = new DataTrigger { Binding = new Binding(nameof(IndexRow.IsGroupMember)), Value = true };
            indentTrigger.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(18, 0, 4, 0)));
            style.Triggers.Add(indentTrigger);
        }
        if (rightAlign)
        {
            style.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Right));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0)));
        }

        return new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(path) { Mode = BindingMode.OneWay },
            SortMemberPath = key,
            CanUserSort = canSort,
            ElementStyle = style,
        };
    }

    private void ApplyIconColumnWidth()
    {
        foreach (var column in ResultsGrid.Columns)
        {
            if (!_columnKeys.TryGetValue(column, out var key) || key != IndexGridColumns.Icon) continue;
            var width = Math.Max(column.MinWidth, _rowHeight + 6);
            column.Width = new DataGridLength(width, DataGridLengthUnitType.Pixel);
        }
    }

    private void SetRowHeight(double value)
    {
        _rowHeight = IndexGridColumns.ClampRowHeight(value);
        ResultsGrid.RowHeight = _rowHeight;
        ApplyIconColumnWidth();
    }

    private IReadOnlyList<IndexGridColumnState> CaptureIndexColumns()
    {
        var entries = new List<IndexGridColumnEntry>(ResultsGrid.Columns.Count);
        foreach (var column in ResultsGrid.Columns)
        {
            if (!_columnKeys.TryGetValue(column, out var key)) continue;
            var width = double.IsNaN(column.ActualWidth) || column.ActualWidth <= 0
                ? column.Width.DisplayValue
                : column.ActualWidth;
            entries.Add(new IndexGridColumnEntry(key, column.DisplayIndex, Math.Round(width, 1),
                column.Visibility == Visibility.Visible));
        }
        return IndexGridColumns.OrderForSave(entries);
    }

    // ── 双击入队 / 多选拖动 / 右键菜单（复制自 MainWindow.xaml.cs） ───────────

    private void OnResultsPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindRowFromSource(e.OriginalSource);
        if (row is null) return;

        if (_isMultiSelectMode)
        {
            ClearArmedRow();
            ResultsGrid.SelectedItem = null;
            if (row.IsGroup)
            {
                _ = ToggleGroupExpandAsync(row);
                e.Handled = true;
                return;
            }
            _dragCandidateRow = row;
            _dragStartPoint = e.GetPosition(ResultsGrid);
            _dragMoved = false;
            _baseDragSelection = _rows.Where(r => r.IsMultiSelected).ToHashSet();
            ResultsGrid.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (row.IsGroup)
        {
            ClearArmedRow();
            ResultsGrid.SelectedItem = row;
            _ = ToggleGroupExpandAsync(row);
            e.Handled = true;
            return;
        }

        var column = FindColumnFromSource(e.OriginalSource);
        var now = Environment.TickCount64;
        if (ReferenceEquals(row, _armedRow) && ReferenceEquals(column, _armedColumn) &&
            DoubleClickState.IsSecondClick(_armedAtMs, now))
        {
            ClearArmedRow();
            EnqueueSingleRow(row);
            e.Handled = true;
            return;
        }

        ArmRow(row, column, now);
    }

    private void OnResultsPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidateRow is null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(ResultsGrid);
        if (!_dragMoved)
        {
            if (Math.Abs(pos.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            _dragMoved = true;
        }

        var current = FindRowAtPoint(pos)?.Item as IndexRow;
        if (current is not null &&
            MultiSelectRange.Apply(_rows, _dragCandidateRow, current, _baseDragSelection, static r => r.IsGroup))
        {
            SyncSelectedIdsFromVisibleRows();
            UpdateMultiSelectButtons();
        }
        e.Handled = true;
    }

    private void OnResultsPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var candidate = _dragCandidateRow;
        var moved = _dragMoved;
        _dragCandidateRow = null;
        _dragMoved = false;
        _baseDragSelection.Clear();
        if (ResultsGrid.IsMouseCaptured) ResultsGrid.ReleaseMouseCapture();
        if (candidate is null) return;
        if (!moved) ToggleRowMultiSelect(candidate);
        ResultsGrid.SelectedItem = null;
        e.Handled = true;
    }

    private void OnResultsLostMouseCapture(object sender, MouseEventArgs e)
    {
        _dragCandidateRow = null;
        _dragMoved = false;
        _baseDragSelection.Clear();
    }

    private void OnResultsPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindRowFromSource(e.OriginalSource);
        if (row is null)
        {
            ResultsGrid.ContextMenu = null;
            return;
        }

        if (_isMultiSelectMode)
        {
            if (!row.IsGroup && !row.IsMultiSelected) ToggleRowMultiSelect(row);
        }
        else if (!ReferenceEquals(ResultsGrid.SelectedItem, row))
        {
            ResultsGrid.SelectedItem = row;
        }

        ResultsGrid.ContextMenu = BuildRowContextMenu(row);
        ResultsGrid.ContextMenu.PlacementTarget = ResultsGrid;
        ResultsGrid.ContextMenu.IsOpen = true;
        e.Handled = true;
    }

    private ContextMenu BuildRowContextMenu(IndexRow row)
    {
        var menu = new ContextMenu();
        menu.PreviewKeyDown += OnContextMenuPreviewKeyDown;

        if (row.IsGroup)
        {
            var toggleGroupItem = new MenuItem
            {
                Header = row.IsExpanded ? Strings.ContextMenu_CollapseGroup : Strings.ContextMenu_ExpandGroup,
                Tag = "G",
            };
            toggleGroupItem.Click += (_, _) => _ = ToggleGroupExpandAsync(row);
            menu.Items.Add(toggleGroupItem);
        }
        else
        {
            var enqueueItem = new MenuItem
            {
                Header = Strings.ContextMenu_Enqueue,
                Tag = "Q",
                IsEnabled = _aria2Ready,
            };
            enqueueItem.Click += (_, _) => EnqueueSingleRow(row);
            menu.Items.Add(enqueueItem);
        }

        if (_isMultiSelectMode || _selectedIds.Count > 0)
        {
            var batchItem = new MenuItem
            {
                Header = Strings.Format(nameof(Strings.ContextMenu_EnqueueSelectedFormat), _selectedIds.Count),
                Tag = "S",
                IsEnabled = _aria2Ready && _selectedIds.Count > 0,
            };
            batchItem.Click += (_, _) => _ = EnqueueSelectedRowsAsync();
            menu.Items.Add(batchItem);
        }

        menu.Items.Add(new Separator());
        var copyMenu = new MenuItem
        {
            Header = Strings.ContextMenu_Copy,
            Tag = "C",
        };
        copyMenu.Items.Add(MakeCopyMenuItem(Strings.ContextMenu_CopyName, row.BaseTitle, "N"));
        copyMenu.Items.Add(MakeCopyMenuItem(Strings.ContextMenu_CopyBundleId, row.BundleId, "B"));
        copyMenu.Items.Add(MakeCopyMenuItem(Strings.ContextMenu_CopyVersion, row.VersionText, "V"));
        copyMenu.Items.Add(MakeCopyMenuItem(Strings.ContextMenu_CopyMinOs, row.MinOsText, "O"));
        menu.Items.Add(copyMenu);

        menu.Items.Add(new Separator());
        var rowHeightMenu = new MenuItem
        {
            Header = Strings.ContextMenu_RowHeight,
            Tag = "H",
        };
        AddRowHeightItems(rowHeightMenu);
        menu.Items.Add(rowHeightMenu);

        if (_selectedIds.Count > 0)
        {
            menu.Items.Add(new Separator());
            var clearItem = new MenuItem
            {
                Header = Strings.ContextMenu_ClearSelection,
                Tag = "Z",
            };
            clearItem.Click += (_, _) => ClearMultiSelection();
            menu.Items.Add(clearItem);
        }

        MenuShortcutIcons.ApplyFromTags(menu);
        return menu;
    }

    private void AddRowHeightItems(ItemsControl parent)
    {
        var small = new MenuItem { Header = Strings.RowHeight_Small, Tag = "S", IsChecked = Math.Abs(_rowHeight - IndexGridColumns.RowHeightSmall) < 0.5 };
        small.Click += (_, _) => SetRowHeight(IndexGridColumns.RowHeightSmall);
        var medium = new MenuItem { Header = Strings.RowHeight_Medium, Tag = "M", IsChecked = Math.Abs(_rowHeight - IndexGridColumns.RowHeightMedium) < 0.5 };
        medium.Click += (_, _) => SetRowHeight(IndexGridColumns.RowHeightMedium);
        var large = new MenuItem { Header = Strings.RowHeight_Large, Tag = "L", IsChecked = Math.Abs(_rowHeight - IndexGridColumns.RowHeightLarge) < 0.5 };
        large.Click += (_, _) => SetRowHeight(IndexGridColumns.RowHeightLarge);
        parent.Items.Add(small);
        parent.Items.Add(medium);
        parent.Items.Add(large);
    }

    internal async Task ToggleGroupExpandAsync(IndexRow groupRow)
    {
        if (!groupRow.IsGroup || string.IsNullOrWhiteSpace(groupRow.GroupKey)) return;

        var groupIdx = _rows.IndexOf(groupRow);
        if (groupIdx < 0) return;

        if (groupRow.IsExpanded)
        {
            while (groupIdx + 1 < _rows.Count &&
                   _rows[groupIdx + 1].IsGroupMember &&
                   string.Equals(_rows[groupIdx + 1].GroupKey, groupRow.GroupKey, StringComparison.OrdinalIgnoreCase))
            {
                _rows.RemoveAt(groupIdx + 1);
            }
            groupRow.IsExpanded = false;
            groupRow.IsGroupEnd = true;
            RefreshJumpCountsFromRows();
            return;
        }

        if (!_expandingGroups.Add(groupRow.GroupKey)) return;

        var db = Volatile.Read(ref _db);
        if (db is null)
        {
            _expandingGroups.Remove(groupRow.GroupKey);
            return;
        }

        var version = Volatile.Read(ref _reloadVersion);
        var query = BuildQuery();
        try
        {
            var members = await Task.Run(() => db.GetGroupMembers(query, groupRow.GroupKey), _windowCts.Token);
            if (_isShuttingDown || _shutdownCompleted || version != Volatile.Read(ref _reloadVersion))
                return;

            var insertBase = _rows.IndexOf(groupRow);
            if (insertBase < 0 || groupRow.IsExpanded) return;

            for (var i = 0; i < members.Count; i++)
            {
                var m = members[i];
                m.IsMultiSelected = _isMultiSelectMode && _selectedIds.Contains(m.Id);
                m.Visual = _queue.GetVisual(m.Id);
                m.IsGroupEnd = i == members.Count - 1;
                _rows.Insert(insertBase + 1 + i, m);
            }
            groupRow.IsExpanded = true;
            groupRow.IsGroupEnd = members.Count == 0;
            RefreshJumpCountsFromRows();
        }
        catch (OperationCanceledException)
        {
            // Window closing
        }
        catch (Exception ex)
        {
            if (!_isShuttingDown && !_shutdownCompleted)
                StatusText.Text = Strings.Format(nameof(Strings.Status_QueryFailedFormat), ex.Message);
        }
        finally
        {
            _expandingGroups.Remove(groupRow.GroupKey);
        }
    }

    private MenuItem MakeCopyMenuItem(string header, string? value, string? keyTag = null)
    {
        var text = value ?? "";
        var item = new MenuItem
        {
            Header = header,
            Tag = keyTag,
            IsEnabled = !string.IsNullOrWhiteSpace(text),
        };
        item.Click += (_, _) => CopyFieldValue(text);
        return item;
    }

    private void OnToggleMultiSelectClick(object sender, RoutedEventArgs e)
    {
        ClearArmedRow();
        _isMultiSelectMode = !_isMultiSelectMode;
        if (_isMultiSelectMode)
        {
            ResultsGrid.SelectedItem = null;
        }
        else
        {
            ClearMultiSelection();
        }
        UpdateMultiSelectButtons();
    }

    private void OnDownloadSelectedClick(object sender, RoutedEventArgs e) => _ = EnqueueSelectedRowsAsync();

    private void OnClearSelectionClick(object sender, RoutedEventArgs e) => ClearMultiSelection();

    private void ToggleRowMultiSelect(IndexRow row)
    {
        if (row.IsGroup) return;
        row.IsMultiSelected = !row.IsMultiSelected;
        if (row.IsMultiSelected) _selectedIds.Add(row.Id);
        else _selectedIds.Remove(row.Id);
        UpdateMultiSelectButtons();
    }

    private void SyncSelectedIdsFromVisibleRows()
    {
        foreach (var row in _rows)
        {
            if (row.IsGroup) continue;
            if (row.IsMultiSelected) _selectedIds.Add(row.Id);
            else _selectedIds.Remove(row.Id);
        }
    }

    private void ClearMultiSelection()
    {
        _selectedIds.Clear();
        foreach (var row in _rows) row.IsMultiSelected = false;
        UpdateMultiSelectButtons();
    }

    private void UpdateMultiSelectButtons()
    {
        var count = _selectedIds.Count;
        MultiSelectButton.Content = _isMultiSelectMode
            ? Strings.Format(nameof(Strings.Toolbar_MultiSelect_OnFormat), count)
            : Strings.Toolbar_MultiSelect_Off;
        MultiSelectButton.FontWeight = _isMultiSelectMode ? FontWeights.Bold : FontWeights.Normal;
        DownloadSelectedButton.Content = Strings.Format(nameof(Strings.Toolbar_DownloadSelectedFormat), count);
        DownloadSelectedButton.IsEnabled = _aria2Ready && count > 0;
        ClearSelectionButton.IsEnabled = count > 0;
    }

    private void ArmRow(IndexRow row, DataGridColumn? column, long now)
    {
        ClearArmedRow();
        _armedRow = row;
        _armedColumn = column;
        _armedAtMs = now;
        row.IsArmed = true;

        _armedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DoubleClickState.WindowMs) };
        _armedTimer.Tick += (_, _) => ClearArmedRow();
        _armedTimer.Start();
    }

    private void ClearArmedRow()
    {
        if (_armedTimer is not null)
        {
            _armedTimer.Stop();
            _armedTimer = null;
        }
        if (_armedRow is not null) _armedRow.IsArmed = false;
        _armedRow = null;
        _armedColumn = null;
    }

    private static IndexRow? FindRowFromSource(object? source)
    {
        var current = source as DependencyObject;
        while (current is not null and not DataGridRow)
            current = VisualTreeHelper.GetParent(current);
        return (current as DataGridRow)?.Item as IndexRow;
    }

    private static DataGridColumn? FindColumnFromSource(object? source)
    {
        var current = source as DependencyObject;
        while (current is not null and not DataGridCell)
            current = VisualTreeHelper.GetParent(current);
        return (current as DataGridCell)?.Column;
    }

    private DataGridRow? FindRowAtPoint(Point point)
    {
        var current = ResultsGrid.InputHitTest(point) as DependencyObject;
        while (current is not null and not DataGridRow)
            current = VisualTreeHelper.GetParent(current);
        return current as DataGridRow;
    }

    // ── 下载队列操作与右侧布局（两栏 + 可配置队列/详情堆叠 + 最大化浮层） ─────

    private bool EnsureAria2ReadyForEnqueue()
    {
        if (_aria2Ready) return true;
        if (!string.IsNullOrWhiteSpace(_aria2Notice))
            StatusText.Text = _aria2Notice;
        return false;
    }

    private void EnqueueSingleRow(IndexRow row)
    {
        if (row.IsGroup) return;
        if (!EnsureAria2ReadyForEnqueue()) return;
        var (_, enqueued) = _queue.Enqueue(row, sourceSummary: row.SourceCount > 0 ? $"{row.SourceCount}" : null);
        _queue.SyncRowVisuals(_rows);
        RefreshQueueSummary();
        StatusText.Text = enqueued
            ? Strings.Format(nameof(Strings.Status_EnqueuedFormat), row.BaseTitle)
            : Strings.Format(nameof(Strings.Status_AlreadyQueuedFormat), row.BaseTitle);
    }

    internal async Task<List<IndexRow>> ResolveSelectedRowsForEnqueueAsync(
        IndexDatabase db,
        IReadOnlyList<string> idsSnapshot)
    {
        var byVisibleId = new Dictionary<string, IndexRow>(StringComparer.Ordinal);
        foreach (var r in _rows)
        {
            if (!r.IsGroup)
                byVisibleId.TryAdd(r.Id, r);
        }

        var selectedRows = new List<IndexRow>(idsSnapshot.Count);
        var missingIds = new List<string>();
        foreach (var id in idsSnapshot)
        {
            if (byVisibleId.TryGetValue(id, out var visible))
                selectedRows.Add(visible);
            else
                missingIds.Add(id);
        }

        if (missingIds.Count > 0)
        {
            var loaded = await Task.Run(() => db.GetByIds(missingIds), _windowCts.Token);
            if (_isShuttingDown || _shutdownCompleted) return selectedRows;
            selectedRows.AddRange(loaded);
        }
        return selectedRows;
    }

    private async Task EnqueueSelectedRowsAsync()
    {
        var db = Volatile.Read(ref _db);
        if (!EnsureAria2ReadyForEnqueue() || _selectedIds.Count == 0 || db is null) return;

        try
        {
            var idsSnapshot = _selectedIds.ToList();
            var selectedRows = await ResolveSelectedRowsForEnqueueAsync(db, idsSnapshot);
            if (_isShuttingDown || _shutdownCompleted) return;

            var (enqueued, skipped) = _queue.EnqueueMany(selectedRows);
            _queue.SyncRowVisuals(_rows);
            RefreshQueueSummary();
            StatusText.Text = Strings.Format(nameof(Strings.Status_BatchEnqueuedFormat), enqueued, skipped);
        }
        catch (OperationCanceledException)
        {
            // Window closing
        }
        catch (Exception ex)
        {
            if (!_isShuttingDown && !_shutdownCompleted)
                StatusText.Text = Strings.Format(nameof(Strings.Status_QueryFailedFormat), ex.Message);
        }
    }

    private void OnDownloadProgress(DownloadProgressSnapshot snapshot)
    {
        var job = _queue.Jobs.FirstOrDefault(j => string.Equals(j.Id, snapshot.EntryId, StringComparison.Ordinal));
        if (job is null) return;
        if (snapshot.Generation != 0 && job.Generation != 0 && snapshot.Generation != job.Generation)
            return;

        if (job.Status == DownloadJobStatus.Cancelled &&
            snapshot.Status is DownloadJobStatus.Downloading or DownloadJobStatus.Verifying or DownloadJobStatus.Importing)
        {
            return;
        }

        job.ApplySnapshot(snapshot);
        _queue.SyncRowVisuals(_rows);
        RefreshQueueSummary();

        if (snapshot.Status == DownloadJobStatus.Failed && !string.IsNullOrWhiteSpace(snapshot.Note))
        {
            StatusText.Text = snapshot.Note;
        }
        else if (snapshot.Status == DownloadJobStatus.Succeeded)
        {
            StatusText.Text = snapshot.Note ?? Strings.Format(nameof(Strings.Download_Note_SavedToFormat), snapshot.TargetPath ?? "");
            if (snapshot.OpenFolderIfConfigured && !string.IsNullOrWhiteSpace(snapshot.TargetPath))
            {
                try
                {
                    OpenFolderAction(snapshot.TargetPath);
                }
                catch
                {
                    // Folder reveal failure must not crash UI
                }
            }
        }
    }

    private async Task CancelEngineJobSafeAsync(string entryId)
    {
        try
        {
            await _downloadEngine.CancelJobAsync(entryId);
        }
        catch (Exception ex)
        {
            StatusText.Text = Strings.Format(nameof(Strings.Download_Error_FailedFormat), ex.Message);
        }
    }

    private async Task SetEngineSuspendedSafeAsync(bool suspended)
    {
        try
        {
            await _downloadEngine.SetSuspendedAsync(suspended);
        }
        catch (Exception ex)
        {
            StatusText.Text = Strings.Format(nameof(Strings.Download_Error_FailedFormat), ex.Message);
        }
    }

    internal static async Task<string?> ResolvePostDownloadImportNoteAsync(
        MainAppPipeClient pipeClient,
        DownloadCompletedInfo info,
        DownloadSettings settings,
        IReadOnlyList<string> cachedRoots,
        string programDir,
        CancellationToken ct)
    {
        if (!settings.AutoImport) return null;

        var hello = await pipeClient.HelloAsync(ct: ct).ConfigureAwait(false);
        if (string.Equals(hello.Error, "protocol_mismatch", StringComparison.Ordinal))
            return Strings.Ipc_Note_ProtocolMismatch;

        if (!hello.Ok)
        {
            var defaultLib = DownloadSettings.DefaultDir(programDir);
            var standaloneRoots = new List<string>(cachedRoots.Count + 1) { defaultLib };
            standaloneRoots.AddRange(cachedRoots);
            return MainAppPipeClient.IsPathInsideAnyRoot(info.FilePath, standaloneRoots)
                ? Strings.Ipc_Note_StandaloneLibraryFallback
                : Strings.Ipc_Note_NotInScanRoot;
        }

        if (!MainAppPipeClient.IsPathInsideAnyRoot(info.FilePath, hello.Roots))
            return Strings.Ipc_Note_NotInScanRoot;

        var imported = await pipeClient.ImportAsync(info.FilePath, ct).ConfigureAwait(false);
        if (imported.Ok)
        {
            var displayName = string.IsNullOrWhiteSpace(imported.Name) ? info.Title : imported.Name;
            return Strings.Format(nameof(Strings.Ipc_Note_AutoImportedFormat), displayName);
        }

        if (settings.AutoRefreshFallback &&
            !string.Equals(imported.Error, "path_outside_root", StringComparison.Ordinal))
        {
            var refreshed = await pipeClient.RefreshAsync(ct).ConfigureAwait(false);
            if (refreshed.Ok) return Strings.Ipc_Note_RefreshedFallback;
        }

        return Strings.Ipc_Note_NextScanFallback;
    }

    private void CancelQueueJob(DownloadJobRow job)
    {
        if (_queue.Cancel(job.Id))
        {
            _queue.SyncRowVisuals(_rows);
            RefreshQueueSummary();
            StatusText.Text = Strings.Format(nameof(Strings.Status_QueueCancelledFormat), job.DisplayNameText);
        }
        QueueCancelButton.IsEnabled = (QueueList.SelectedItem as DownloadJobRow)?.CanCancel == true;
    }

    private void OnQueueSuspendClick(object sender, RoutedEventArgs e)
    {
        var suspended = _queue.ToggleSuspend();
        _queue.SyncRowVisuals(_rows);
        RefreshQueueSummary();
        StatusText.Text = suspended ? Strings.Status_QueueSuspended : Strings.Status_QueueResumed;
    }

    private void OnQueueCancelClick(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is DownloadJobRow job) CancelQueueJob(job);
    }

    private void OnQueueClearClick(object sender, RoutedEventArgs e)
    {
        var removed = _queue.ClearFinished();
        _queue.SyncRowVisuals(_rows);
        RefreshQueueSummary();
        StatusText.Text = Strings.Format(nameof(Strings.Status_QueueClearedFormat), removed);
    }

    private void OnQueueSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        QueueCancelButton.IsEnabled = (QueueList.SelectedItem as DownloadJobRow)?.CanCancel == true;
    }

    private void OnQueuePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var current = e.OriginalSource as DependencyObject;
        while (current is not null and not ListBoxItem)
            current = VisualTreeHelper.GetParent(current);
        if ((current as ListBoxItem)?.DataContext is not DownloadJobRow job) return;

        var now = Environment.TickCount64;
        if (ReferenceEquals(job, _queueArmedJob) && DoubleClickState.IsSecondClick(_queueArmedAtMs, now))
        {
            _queueArmedJob = null;
            CancelQueueJob(job);
            e.Handled = true;
            return;
        }

        _queueArmedJob = job;
        _queueArmedAtMs = now;
    }

    private void RefreshQueueSummary()
    {
        QueueSuspendButton.Content = _queue.IsSuspended ? Strings.Queue_Resume : Strings.Queue_Suspend;
        QueueSummaryText.Text = _queue.FormatSummary();
        QueueCancelButton.IsEnabled = (QueueList.SelectedItem as DownloadJobRow)?.CanCancel == true;
    }

    private void ApplyRightSections()
    {
        RightSectionsGrid.Children.Clear();
        RightSectionsGrid.RowDefinitions.Clear();

        var visible = IndexLayoutStore.OrderedVisibleSections(_layout.RightSections);
        if (visible.Count == 0)
        {
            DownloadQueuePanel.Visibility = Visibility.Collapsed;
            DetailsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (visible.Count == 1)
        {
            RightSectionsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var panel = PanelFor(visible[0]);
            var other = visible[0] == RightSection.Queue ? DetailsPanel : DownloadQueuePanel;
            other.Visibility = Visibility.Collapsed;
            panel.Visibility = Visibility.Visible;
            Grid.SetRow(panel, 0);
            RightSectionsGrid.Children.Add(panel);
            return;
        }

        var split = IndexLayoutStore.NormalizePair(_layout.RightSplit, IndexLayoutState.Default.RightSplit);
        RightSectionsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(split[0], GridUnitType.Star), MinHeight = 140 });
        RightSectionsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RightSectionsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(split[1], GridUnitType.Star), MinHeight = 140 });

        var topPanel = PanelFor(visible[0]);
        var bottomPanel = PanelFor(visible[1]);
        topPanel.Visibility = Visibility.Visible;
        bottomPanel.Visibility = Visibility.Visible;

        Grid.SetRow(topPanel, 0);
        Grid.SetRow(_rightSectionSplitter, 1);
        Grid.SetRow(bottomPanel, 2);

        RightSectionsGrid.Children.Add(topPanel);
        RightSectionsGrid.Children.Add(_rightSectionSplitter);
        RightSectionsGrid.Children.Add(bottomPanel);
    }

    private GroupBox PanelFor(RightSection section) => section switch
    {
        RightSection.Queue => DownloadQueuePanel,
        RightSection.Details => DetailsPanel,
        _ => DownloadQueuePanel,
    };

    private void ApplyLayout()
    {
        var visibleCount = IndexLayoutStore.OrderedVisibleSections(_layout.RightSections).Count;
        var hasSections = visibleCount > 0;
        var effectiveHidden = _layout.RightHidden || !hasSections;

        var columns = IndexLayoutStore.NormalizePair(_layout.Columns, IndexLayoutState.Default.Columns);
        LeftColumn.Width = new GridLength(columns[0], GridUnitType.Star);
        RightColumn.Width = effectiveHidden ? new GridLength(0) : new GridLength(columns[1], GridUnitType.Star);
        RightColumn.MinWidth = effectiveHidden ? 0 : IndexLayoutStore.MinRightWidth;

        MainSplitter.Visibility = effectiveHidden ? Visibility.Collapsed : Visibility.Visible;

        if (_rightOverlayOpen && hasSections)
        {
            if (!ReferenceEquals(RightOverlayContentHost.Content, RightSectionsGrid))
            {
                RightColumnHost.Content = null;
                RightOverlayContentHost.Content = RightSectionsGrid;
            }
            RightOverlayBorder.Width = Math.Clamp(_layout.RightOverlayWidth, IndexLayoutStore.MinOverlayWidth, IndexLayoutStore.MaxOverlayWidth);
            RightOverlayBorder.Visibility = Visibility.Visible;
            RightColumnHost.Visibility = Visibility.Collapsed;
        }
        else
        {
            if (!ReferenceEquals(RightColumnHost.Content, RightSectionsGrid))
            {
                RightOverlayContentHost.Content = null;
                RightColumnHost.Content = RightSectionsGrid;
            }
            RightOverlayBorder.Visibility = Visibility.Collapsed;
            RightColumnHost.Visibility = effectiveHidden ? Visibility.Collapsed : Visibility.Visible;
        }

        RightToggleButton.IsEnabled = hasSections;
        RightToggleButton.Content = _rightOverlayOpen || !effectiveHidden
            ? Strings.Toolbar_HideRight
            : Strings.Toolbar_ShowRight;

        var minWidth = effectiveHidden
            ? IndexLayoutStore.MinLeftWidth + 36
            : IndexLayoutStore.MinLeftWidth + IndexLayoutStore.MinRightWidth + IndexLayoutStore.SplitterSize + 36;
        MinWidth = minWidth;
    }

    private void OnToggleRightPaneClick(object sender, RoutedEventArgs e) => ToggleRightPane();

    private void ToggleRightPane()
    {
        if (IndexLayoutStore.OrderedVisibleSections(_layout.RightSections).Count == 0) return;

        var current = CaptureCurrentLayout();
        if (WindowState == WindowState.Maximized && current.RightHidden)
        {
            _layout = current;
            _rightOverlayOpen = !_rightOverlayOpen;
            ApplyLayout();
            return;
        }

        _rightOverlayOpen = false;
        _layout = current with { RightHidden = !current.RightHidden };
        ApplyLayout();
    }

    private void OnMainSplitterDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _layout = _layout with { Columns = IndexLayoutState.Default.Columns };
        ApplyLayout();
        e.Handled = true;
    }

    private void OnRightSplitterDoubleClick(object? sender, MouseButtonEventArgs e)
    {
        _layout = _layout with { RightSplit = IndexLayoutState.Default.RightSplit };
        ApplyRightSections();
        e.Handled = true;
    }

    private void OnOverlayResizeDragDelta(object sender, DragDeltaEventArgs e)
    {
        var next = Math.Clamp(
            _layout.RightOverlayWidth - e.HorizontalChange,
            IndexLayoutStore.MinOverlayWidth,
            IndexLayoutStore.MaxOverlayWidth);
        _layout = _layout with { RightOverlayWidth = next };
        RightOverlayBorder.Width = next;
    }

    private IndexLayoutState CaptureCurrentLayout()
    {
        var cols = _layout.RightHidden || IndexLayoutStore.OrderedVisibleSections(_layout.RightSections).Count == 0
            ? _layout.Columns
            : new[] { StarValue(LeftColumn), StarValue(RightColumn) };

        var split = _layout.RightSplit;
        if (RightSectionsGrid.RowDefinitions.Count == 3)
        {
            var r0 = RowStarValue(RightSectionsGrid.RowDefinitions[0]);
            var r2 = RowStarValue(RightSectionsGrid.RowDefinitions[2]);
            if (r0 > 0 && r2 > 0) split = [r0, r2];
        }

        return _layout with
        {
            Columns = IndexLayoutStore.NormalizePair(cols, IndexLayoutState.Default.Columns),
            RightSplit = IndexLayoutStore.NormalizePair(split, IndexLayoutState.Default.RightSplit),
        };
    }

    private static double StarValue(ColumnDefinition column) => column.Width.IsStar
        ? column.Width.Value
        : column.ActualWidth > 0 ? column.ActualWidth : column.Width.Value;

    private static double RowStarValue(RowDefinition row) => row.Height.IsStar
        ? row.Height.Value
        : row.ActualHeight > 0 ? row.ActualHeight : row.Height.Value;

    // ── 详情与图标（保留原逻辑） ──────────────────────────────────────────────

    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isMultiSelectMode)
        {
            if (ResultsGrid.SelectedItem is not null) ResultsGrid.SelectedItem = null;
            return;
        }

        if (ResultsGrid.SelectedItem is not IndexRow row) return;
        var db = Volatile.Read(ref _db);
        if (db is null) return;
        var version = Interlocked.Increment(ref _detailVersion);
        try
        {
            var (descriptions, sources) = await Task.Run(() =>
                (db.GetDescriptions(row.Id), db.GetSources(row.Id)));
            if (version != Volatile.Read(ref _detailVersion) || !ReferenceEquals(ResultsGrid.SelectedItem, row))
                return;

            var sourceCount = row.SourceCount > 0 ? row.SourceCount : sources.Count;
            DetailTitle.Text = row.DisplayTitle;
            DetailMeta.Text = row.IsGroup
                ? Strings.Format(
                    nameof(Strings.Detail_GroupMetaFormat),
                    row.BundleId, row.GroupCount, row.VersionText, row.MinOsText, row.DeviceFamilyText, row.SizeText, sourceCount)
                : Strings.Format(
                    nameof(Strings.Detail_MetaFormat),
                    row.BundleId, row.Version, row.MinOsText, row.DeviceFamilyText, row.SizeText, sourceCount);
            DetailCategory.Text = string.Join(" · ", new[] { row.CategoryText, row.Developer }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            DetailDescription.Text = ChooseDescription(descriptions, UiLanguage.IsCurrentChinese);
            SourcesList.ItemsSource = sources;
        }
        catch (Exception ex)
        {
            if (version == Volatile.Read(ref _detailVersion))
            {
                DetailDescription.Text = Strings.Format(nameof(Strings.Detail_LoadFailedFormat), ex.Message);
                SourcesList.ItemsSource = null;
            }
        }
    }

    internal static string ChooseDescription(IReadOnlyList<DescriptionRow> descriptions, bool preferChinese)
    {
        if (descriptions.Count == 0)
            return Strings.Detail_NoDescription;
        var order = preferChinese
            ? new[] { "zh-Hans", "en", "und" }
            : new[] { "en", "und", "zh-Hans" };
        foreach (var lang in order)
        {
            var hit = descriptions.FirstOrDefault(d => d.Language == lang);
            if (hit is not null) return hit.Text;
        }
        return descriptions[0].Text;
    }

    private void ClearDetails()
    {
        Interlocked.Increment(ref _detailVersion);
        DetailTitle.Text = "";
        DetailMeta.Text = "";
        DetailCategory.Text = "";
        DetailDescription.Text = "";
        DetailHint.Text = "";
        SourcesList.ItemsSource = null;
    }

    private void OnLoadingRow(object sender, DataGridRowEventArgs e)
    {
        if (e.Row.Item is not IndexRow row) return;
        if (row.Icon is not null || string.IsNullOrWhiteSpace(row.IconPath)) return;
        if (!_iconPending.Add(row)) return;
        QueueIconLoad(row);
    }

    private async void QueueIconLoad(IndexRow row)
    {
        try
        {
            await LoadIconAsync(row);
        }
        catch (OperationCanceledException)
        {
            // Window closed
        }
        catch (ObjectDisposedException)
        {
            // Window closed
        }
        catch (Exception ex)
        {
            _iconFailures++;
            if (_iconFailures == 1 && string.Equals(StatusText.Text, Strings.Status_Ready, StringComparison.Ordinal))
                StatusText.Text = Strings.Format(nameof(Strings.Status_IconLoadFailedFormat), ex.Message);
        }
        finally
        {
            _iconPending.Remove(row);
        }
    }

    private async Task LoadIconAsync(IndexRow row)
    {
        var path = ResolveIconPath(row.IconPath);
        if (path is null) return;

        CancellationToken ct;
        try
        {
            ct = _windowCts.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        await _iconGate.WaitAsync(ct);
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            var image = new BitmapImage();
            using var stream = new MemoryStream(bytes);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            row.Icon = image;
        }
        finally
        {
            try { _iconGate.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(
            OnSettingsApplied,
            Volatile.Read(ref _db)?.Path,
            path => _ = SwitchDatabaseAsync(path))
        {
            Owner = this,
        };
        dialog.ShowDialog();
    }

    private void OnImportIndexClick(object sender, RoutedEventArgs e)
    {
        var currentDb = Volatile.Read(ref _db)?.Path;
        var defaultPath = IndexImporter.DefaultDbPath();
        var isExternal = !string.IsNullOrWhiteSpace(currentDb) &&
                         !string.Equals(Path.GetFullPath(currentDb), Path.GetFullPath(defaultPath), StringComparison.OrdinalIgnoreCase);

        var dlg = new IndexImportWindow(
            AppContext.BaseDirectory,
            currentDb,
            isExternal,
            onImported: path => _ = SwitchDatabaseAsync(path),
            owner: this);
        dlg.ShowDialog();
    }

    private async Task SwitchDatabaseAsync(string newDbPath)
    {
        if (_isShuttingDown || _shutdownCompleted || string.IsNullOrWhiteSpace(newDbPath)) return;

        var fullPath = Path.GetFullPath(newDbPath);
        Interlocked.Increment(ref _reloadVersion);
        Interlocked.Increment(ref _detailVersion);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var nextDb = new IndexDatabase(fullPath);
        Volatile.Write(ref _db, nextDb);

        await RefreshDatabaseMetadataAsync(nextDb);
        if (_isShuttingDown || _shutdownCompleted) return;
        await ReloadAsync();
        if (!_isShuttingDown && !_shutdownCompleted)
            StatusText.Text = Strings.Format(nameof(Strings.Status_IndexImportedFormat), fullPath);
    }

    private async Task RefreshDatabaseMetadataAsync(IndexDatabase? db)
    {
        if (db is null)
        {
            MissingDbBanner.Visibility = Visibility.Visible;
            PathText.Text = Strings.Status_NoIndexLoaded;
            MetaText.Text = "";
            return;
        }

        MissingDbBanner.Visibility = Visibility.Collapsed;
        PathText.Text = db.Path;
        try
        {
            var schema = await Task.Run(() => db.GetMeta("schema_version") ?? "?");
            if (!_isShuttingDown && !_shutdownCompleted && ReferenceEquals(Volatile.Read(ref _db), db))
                MetaText.Text = Strings.Format(nameof(Strings.Status_MetaFormat), schema);
        }
        catch (Exception ex)
        {
            if (!_isShuttingDown && !_shutdownCompleted && ReferenceEquals(Volatile.Read(ref _db), db))
            {
                MetaText.Text = Strings.Status_MetaFailed;
                StatusText.Text = Strings.Format(nameof(Strings.Status_MetaFailedFormat), ex.Message);
            }
        }
    }

    private void OnSettingsApplied(IndexSettingsDraft applied)
    {
        if (_isShuttingDown || _shutdownCompleted) return;

        var prevLang = UiLanguage.CurrentLanguage;
        var resolvedLang = UiLanguage.Apply(null, applied.UiLanguageSetting);

        var limitChanged = _displayLimit != applied.DisplayLimit;
        _displayLimit = applied.DisplayLimit;
        _openLinkProgram = applied.OpenLinkProgram;

        _layout = _layout with { RightSections = applied.RightSections };
        ApplyRightSections();
        ApplyLayout();

        _queue.ApplyColumns(applied.QueueColumns);
        RefreshQueueSummary();

        _allowOutsideWrite = applied.AllowOutsideProgramDirWrite;
        _rememberLastDir = applied.Download.RememberLastDir;
        _activeDownloadDir = applied.Download.Dir;
        _downloadEngine.SetSessionDownloadDir(_activeDownloadDir);
        RefreshDownloadDirSelector();
        UpdateDownloadDirWarning();

        _ = ApplyLiveEngineSettingsSafeAsync(applied.Download);

        if (!string.Equals(prevLang, resolvedLang, StringComparison.OrdinalIgnoreCase))
        {
            UpdateMultiSelectButtons();
            StatusText.Text = Strings.Status_LanguageChangedHint;
        }
        if (limitChanged && _ready)
        {
            _ = ReloadAsync();
        }
    }

    private async Task ApplyLiveEngineSettingsSafeAsync(DownloadSettings settings)
    {
        try
        {
            await _downloadEngine.ApplySettingsToRunningDaemonAsync(settings, _windowCts.Token);
        }
        catch
        {
            // Ignore when daemon isn't started yet or window is closing
        }
    }

    internal sealed record DownloadDirOption(string FullPath, string DisplayText);

    private void OnDownloadDirSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDownloadDirChanged) return;
        if (DownloadDirCombo.SelectedItem is not DownloadDirOption chosen || string.IsNullOrWhiteSpace(chosen.FullPath))
            return;
        SelectActiveDownloadDir(chosen.FullPath);
    }

    private void OnBrowseDownloadDirClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            InitialDirectory = AppContext.BaseDirectory,
        };
        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            SelectActiveDownloadDir(dialog.FolderName);
    }

    private void SelectActiveDownloadDir(string rawDir)
    {
        var normalized = DownloadSettings.NormalizeDir(rawDir, DownloadSettings.DefaultDir());
        _activeDownloadDir = normalized;
        _downloadEngine.SetSessionDownloadDir(normalized);
        RefreshDownloadDirSelector();
        UpdateDownloadDirWarning();

        if (_rememberLastDir)
        {
            _ = Task.Run(() =>
            {
                try { IndexerSettings.RecordLastDownloadDir(normalized); } catch { }
            });
        }
    }

    private void RefreshDownloadDirSelector()
    {
        var choices = BuildDownloadDirChoices(_activeDownloadDir, Volatile.Read(ref _knownScanRoots), AppContext.BaseDirectory);
        var options = choices
            .Select(c => new DownloadDirOption(c, FormatDownloadDirDisplay(c, AppContext.BaseDirectory)))
            .ToList();
        _suppressDownloadDirChanged = true;
        try
        {
            DownloadDirCombo.ItemsSource = options;
            var match = options.FirstOrDefault(c => string.Equals(c.FullPath, _activeDownloadDir, StringComparison.OrdinalIgnoreCase));
            DownloadDirCombo.SelectedItem = match ?? options.FirstOrDefault();
        }
        finally
        {
            _suppressDownloadDirChanged = false;
        }
    }

    internal static string FormatDownloadDirDisplay(string fullPath, string? programDir = null)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return "";
        try
        {
            var baseDir = string.IsNullOrWhiteSpace(programDir) ? AppContext.BaseDirectory : programDir.Trim();
            var normBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDir));
            if (normBase.Length == 2 && normBase[1] == ':')
                normBase += Path.DirectorySeparatorChar;

            var normPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath.Trim()));
            if (normPath.Length == 2 && normPath[1] == ':')
                normPath += Path.DirectorySeparatorChar;

            if (string.Equals(normPath, normBase, StringComparison.OrdinalIgnoreCase))
                return "\\";

            var prefix = Path.EndsInDirectorySeparator(normBase) ? normBase : normBase + Path.DirectorySeparatorChar;
            if (normPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var rel = normPath[prefix.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return rel.Length == 0 ? "\\" : "\\" + rel;
            }
            return normPath;
        }
        catch
        {
            return fullPath;
        }
    }

    private void UpdateDownloadDirWarning()
    {
        var warning = EvaluateDownloadDirWarning(
            _activeDownloadDir,
            Volatile.Read(ref _knownScanRoots),
            _mainAppConnected,
            _allowOutsideWrite,
            AppContext.BaseDirectory);

        if (string.IsNullOrWhiteSpace(warning))
        {
            DownloadDirWarningBanner.Visibility = Visibility.Collapsed;
            DownloadDirWarningText.Text = "";
        }
        else
        {
            DownloadDirWarningText.Text = warning;
            DownloadDirWarningBanner.Visibility = Visibility.Visible;
        }
    }

    internal static IReadOnlyList<string> BuildDownloadDirChoices(
        string activeDir,
        IReadOnlyList<string> knownScanRoots,
        string programDir)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
                if (full.Length == 2 && full[1] == ':')
                    full += Path.DirectorySeparatorChar;
                if (seen.Add(full)) list.Add(full);
            }
            catch
            {
                // Ignore malformed root paths
            }
        }

        Add(DownloadSettings.DefaultDir(programDir));
        foreach (var root in knownScanRoots)
            Add(root);
        Add(activeDir);
        return list;
    }

    internal static string? EvaluateDownloadDirWarning(
        string targetDir,
        IReadOnlyList<string> knownScanRoots,
        bool mainAppConnected,
        bool allowOutsideWrite,
        string programDir)
    {
        if (string.IsNullOrWhiteSpace(targetDir))
            return Strings.DownloadDir_Warning_OutsideBlocked;

        string fullTarget;
        string fullProgram;
        try
        {
            fullTarget = Path.GetFullPath(targetDir.Trim());
            fullProgram = Path.GetFullPath(string.IsNullOrWhiteSpace(programDir) ? AppContext.BaseDirectory : programDir);
        }
        catch
        {
            return Strings.DownloadDir_Warning_OutsideBlocked;
        }

        var isUnc = fullTarget.StartsWith(@"\\", StringComparison.Ordinal) ||
                    (Uri.TryCreate(fullTarget, UriKind.Absolute, out var uri) && uri.IsUnc);
        var insideProgram = !isUnc && DownloadPathPolicy.IsUnderDirectory(fullTarget, fullProgram);
        if (!insideProgram && !allowOutsideWrite)
            return Strings.DownloadDir_Warning_OutsideBlocked;

        if ((mainAppConnected || knownScanRoots.Count > 0) &&
            !IsDirectoryInAnyRoot(fullTarget, knownScanRoots))
        {
            return Strings.DownloadDir_Warning_NotInScanRoot;
        }

        return null;
    }

    private static bool IsDirectoryInAnyRoot(string fullTargetDir, IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            try
            {
                var normRoot = Path.GetFullPath(root.Trim());
                if (DownloadPathPolicy.IsUnderDirectory(fullTargetDir, normRoot))
                    return true;
            }
            catch
            {
                // Ignore malformed root path
            }
        }
        return false;
    }

    private void OnSourcesPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var current = e.OriginalSource as DependencyObject;
        while (current is not null and not ListBoxItem)
            current = VisualTreeHelper.GetParent(current);
        if (current is ListBoxItem item)
            item.IsSelected = true;
    }

    private void OnSourcesContextMenuOpened(object sender, RoutedEventArgs e)
    {
        var source = SourcesList.SelectedItem as SourceRow;
        var hasSafeUrl = TryCreateSafeHttpUri(source?.Url, out _);
        SourceCopyLinkMenuItem.IsEnabled = source is not null && !string.IsNullOrWhiteSpace(source.Url);
        SourceOpenLinkMenuItem.IsEnabled = hasSafeUrl;
        SourceOpenWithMenuItem.IsEnabled = hasSafeUrl;
        SourceOpenPageMenuItem.IsEnabled = TryCreateSafeHttpUri(source?.Page, out _);
    }

    private void OnSourceCopyMenuClick(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is SourceRow source)
            CopySourceUrl(source);
    }

    private void OnSourceOpenMenuClick(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is SourceRow source)
            _ = OpenSourceUrlAsync(source.Url, _openLinkProgram, promptSaveIfCustom: false);
    }

    private void OnSourceOpenWithMenuClick(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is SourceRow source)
            OpenSourceWithChooser(source.Url);
    }

    private void OnSourceOpenPageMenuClick(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is SourceRow source)
            _ = OpenSourceUrlAsync(source.Page, _openLinkProgram, promptSaveIfCustom: false);
    }

    private void OnSourceCopyClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SourceRow source)
            CopySourceUrl(source);
    }

    private void OnSourceOpenClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SourceRow source)
            _ = OpenSourceUrlAsync(source.Url, _openLinkProgram, promptSaveIfCustom: false);
    }

    private void OnSourceOpenWithClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SourceRow source)
            OpenSourceWithChooser(source.Url);
    }

    private void CopySourceUrl(SourceRow source)
    {
        if (string.IsNullOrWhiteSpace(source.Url)) return;
        try
        {
            Clipboard.SetText(source.Url);
            StatusText.Text = Strings.Format(nameof(Strings.Status_CopiedFormat), source.Url);
        }
        catch
        {
            // Ignore clipboard contention
        }
    }

    private void OpenSourceWithChooser(string? rawUrl)
    {
        if (!TryCreateSafeHttpUri(rawUrl, out _))
        {
            StatusText.Text = Strings.Status_SourceLinkInvalid;
            return;
        }

        var initialDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(_openLinkProgram))
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(_openLinkProgram));
                if (!string.IsNullOrWhiteSpace(dir)) initialDir = dir;
            }
            catch
            {
                // Ignore invalid path
            }
        }

        var dialog = new OpenFileDialog
        {
            Title = Strings.Links_ChooseProgramDialogTitle,
            Filter = Strings.Links_ExecutableFilter,
            CheckFileExists = true,
            InitialDirectory = initialDir,
        };
        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FileName))
            return;

        _ = OpenSourceUrlAsync(rawUrl, dialog.FileName, promptSaveIfCustom: true);
    }

    private async Task OpenSourceUrlAsync(string? rawUrl, string? programPath, bool promptSaveIfCustom)
    {
        if (!TryCreateSafeHttpUri(rawUrl, out _))
        {
            StatusText.Text = Strings.Status_SourceLinkInvalid;
            return;
        }

        var trimmedProg = (programPath ?? "").Trim();
        var result = await Task.Run(() =>
        {
            var built = ExternalLinkOpener.TryBuildStartInfo(rawUrl, trimmedProg);
            if (!built.Ok || built.StartInfo is null) return (built, (string?)null);
            try
            {
                Process.Start(built.StartInfo);
                return (built, (string?)null);
            }
            catch (Exception ex)
            {
                return (built, ex.Message);
            }
        });

        if (_isShuttingDown || _shutdownCompleted) return;
        var (launch, startErr) = result;
        if (!launch.Ok)
        {
            StatusText.Text = launch.ErrorMessage ?? Strings.Status_SourceLinkInvalid;
            return;
        }
        if (!string.IsNullOrEmpty(startErr))
        {
            StatusText.Text = Strings.Format(nameof(Strings.Download_Error_FailedFormat), startErr);
            return;
        }

        StatusText.Text = Strings.Format(nameof(Strings.Status_SourceLinkOpenedFormat), launch.SafeUrl ?? "");

        var normalizedChosen = "";
        if (trimmedProg.Length > 0)
        {
            try { normalizedChosen = Path.GetFullPath(trimmedProg); } catch { normalizedChosen = trimmedProg; }
        }
        var normalizedCurrent = "";
        if (!string.IsNullOrWhiteSpace(_openLinkProgram))
        {
            try { normalizedCurrent = Path.GetFullPath(_openLinkProgram.Trim()); } catch { normalizedCurrent = _openLinkProgram.Trim(); }
        }

        if (promptSaveIfCustom &&
            normalizedChosen.Length > 0 &&
            !string.Equals(normalizedChosen, normalizedCurrent, StringComparison.OrdinalIgnoreCase))
        {
            var answer = MessageBox.Show(
                this,
                Strings.Format(nameof(Strings.Links_SaveDefaultPromptFormat), normalizedChosen),
                Strings.Links_SaveDefaultTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes && !_isShuttingDown && !_shutdownCompleted)
            {
                _openLinkProgram = normalizedChosen;
                _ = Task.Run(() =>
                {
                    try { IndexerSettings.SaveOpenLinkProgram(normalizedChosen); } catch { }
                });
            }
        }
    }

    internal static bool TryCreateSafeHttpUri(string? raw, out Uri? uri)
        => ExternalLinkOpener.TryCreateSafeHttpUri(raw, out uri);

    private string? ResolveIconPath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var db = Volatile.Read(ref _db);
        if (db is null) return null;
        try
        {
            var dbDir = Path.GetDirectoryName(Path.GetFullPath(db.Path));
            if (string.IsNullOrWhiteSpace(dbDir)) dbDir = AppContext.BaseDirectory;
            var iconRoot = Path.GetFullPath(Path.Combine(dbDir, "icons"));
            var candidate = Path.GetFullPath(Path.Combine(dbDir, relative));
            var prefix = Path.EndsInDirectorySeparator(iconRoot) ? iconRoot : iconRoot + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            var extension = Path.GetExtension(candidate);
            if (!string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase))
                return null;
            return File.Exists(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(
        IntPtr pidlFolder, uint cidl, [In] IntPtr[] apidl, uint dwFlags);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    private static void OpenFolderInExplorer(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        var thread = new Thread(() =>
        {
            try
            {
                var fullPath = Path.GetFullPath(filePath);
                var folder = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrWhiteSpace(folder)) folder = Path.GetPathRoot(fullPath);
                if (string.IsNullOrWhiteSpace(folder)) return;

                if (File.Exists(fullPath))
                {
                    var folderPidl = IntPtr.Zero;
                    var filePidl = IntPtr.Zero;
                    try
                    {
                        if (SHParseDisplayName(folder, IntPtr.Zero, out folderPidl, 0, out _) == 0 && folderPidl != IntPtr.Zero &&
                            SHParseDisplayName(fullPath, IntPtr.Zero, out filePidl, 0, out _) == 0 && filePidl != IntPtr.Zero)
                        {
                            var child = ILFindLastID(filePidl);
                            if (SHOpenFolderAndSelectItems(folderPidl, 1, [child], 0) == 0)
                                return;
                        }
                    }
                    finally
                    {
                        if (filePidl != IntPtr.Zero) ILFree(filePidl);
                        if (folderPidl != IntPtr.Zero) ILFree(folderPidl);
                    }
                }

                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType is null) return;
                var shell = Activator.CreateInstance(shellType);
                if (shell is null) return;
                shellType.InvokeMember("Explore", System.Reflection.BindingFlags.InvokeMethod, null, shell, [folder]);
            }
            catch
            {
                // Ignore shell launch failures
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }
}
