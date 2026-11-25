using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using IdeviceGui.Resources;
using IdeviceGui.Services;
using IdeviceGui.Services.Data;
using IdeviceGui.Services.Scanning;
using IdeviceGui.ViewModels;
using IdeviceGui.ViewModels.Library;
using Microsoft.Win32;

namespace IdeviceGui.Views;

/// <summary>
/// 设置窗口（2026-11-02 用户定，docs/01 §2.4）：六个设置分组，每组一个「恢复默认」，另有只读英文「关于」页；
/// **窗口内即时预览，点「确定 / 应用」才写库**；「取消 / 关窗」回滚到打开窗口时的值。
/// </summary>
internal partial class SettingsWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly MainWindow _owner;

    private SettingsDraft _initial;
    private SettingsDraft _draft;
    private bool _loading = true;   // XAML 加载期间 Slider 的 Min/Max 赋值会触发 ValueChanged，先挡住
    private bool _applied;
    private bool _applying;
    private bool _closed;           // 审查 N4：窗口已关，async void 根操作不要再弹框 / 碰控件
    private bool _dirty;            // 审查 N6：没改过就不要在关窗时"回滚一次"（会白跳一下）
    private readonly System.Windows.Threading.DispatcherTimer _previewTimer;   // 审查 N8：文本框防抖 300 ms

    private readonly List<(LibrarySortField Field, string Text)> _sortOptions = new()
    {
        (LibrarySortField.AppSize, Strings.Main_Column_AppSize),
        (LibrarySortField.Name, Strings.Main_Column_Name),
        (LibrarySortField.Version, Strings.Main_Column_Version),
        (LibrarySortField.MinOs, Strings.Main_Column_MinOs),
        (LibrarySortField.DeviceFamily, Strings.Main_Column_DeviceFamily),
        (LibrarySortField.BundleId, "BundleId"),
        (LibrarySortField.Arch, Strings.Main_Column_Arch),
        (LibrarySortField.FileSize, Strings.Main_Column_FileSize),
        (LibrarySortField.State, Strings.Main_Column_State),
    };

    private readonly List<(SearchMatchMode Mode, string Text)> _matchOptions = new()
    {
        (SearchMatchMode.Contains, Strings.Main_Search_ModeContains),
        (SearchMatchMode.Prefix, Strings.Main_Search_ModePrefix),
        (SearchMatchMode.Exact, Strings.Main_Search_ModeExact),
    };

    private readonly ObservableCollection<FieldItem> _fields = new();
    private readonly ObservableCollection<QueueFieldItem> _queueFields = new();
    private readonly List<(SearchField Field, System.Windows.Controls.CheckBox Box)> _scopeBoxes = new();
    private readonly Dictionary<string, bool> _initialColumnVisibility = new(StringComparer.Ordinal);
    private readonly List<(string Key, System.Windows.Controls.CheckBox Box)> _columnBoxes = new();

    public SettingsWindow(MainViewModel vm, MainWindow owner)
    {
        InitializeComponent();
        _vm = vm;
        _owner = owner;

        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        AboutVersionText.Text = version is null ? "Version: unknown" : $"Version: {version.ToString(3)}";

        // 取消回滚基线 = 当前视图；默认排序/搜索范围显示 = 已保存的默认值（用户 2026-11-08）。
        _initial = vm.CaptureSettings();
        _initial.LogHidden = owner.SettingsLogHidden;
        _initial.LogOverlayWidth = owner.SettingsLogOverlayWidth;
        _draft = vm.CaptureDefaultSettings();
        _draft.LogHidden = _initial.LogHidden;
        _draft.LogOverlayWidth = _initial.LogOverlayWidth;

        BuildStaticChoices();
        BuildColumnChoices();
        LoadDraftToControls();
        ReloadRoots();
        _loading = false;

        _previewTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            if (!_loading && !_closed) Preview();
        };
    }

    // ── 构建静态选项 ─────────────────────────────────────────────────────────

    private void BuildStaticChoices()
    {
        SortFieldCombo.ItemsSource = _sortOptions.Select(o => o.Text).ToList();
        MatchModeCombo.ItemsSource = _matchOptions.Select(o => o.Text).ToList();

        foreach (var field in Enum.GetValues<SearchField>())
        {
            var box = new System.Windows.Controls.CheckBox
            {
                Content = ScopeName(field),
                Margin = new Thickness(0, 2, 12, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            box.Click += OnAnyChanged;
            _scopeBoxes.Add((field, box));
            ScopePanel.Children.Add(box);
        }
    }

    private void BuildColumnChoices()
    {
        ColumnsPanel.Children.Clear();
        _columnBoxes.Clear();
        _initialColumnVisibility.Clear();
        foreach (var spec in LibraryColumns.Defaults)
        {
            var visible = _owner.IsLibraryColumnVisible(spec.Key) ?? true;
            _initialColumnVisibility[spec.Key] = visible;
            var box = new System.Windows.Controls.CheckBox
            {
                Content = LibraryColumnHeader(spec.Key, spec.Header),
                IsChecked = visible,
                Margin = new Thickness(0, 2, 12, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var key = spec.Key;
            box.Click += (_, _) =>
            {
                if (_loading) return;
                _owner.SetLibraryColumnVisible(key, box.IsChecked == true);
                _dirty = true;
            };
            _columnBoxes.Add((key, box));
            ColumnsPanel.Children.Add(box);
        }
    }

    private void RestoreColumnVisibility()
    {
        foreach (var (key, box) in _columnBoxes)
        {
            var visible = !_initialColumnVisibility.TryGetValue(key, out var value) || value;
            box.IsChecked = visible;
            _owner.SetLibraryColumnVisible(key, visible);
        }
    }

    private async Task SaveColumnVisibilityAsync()
    {
        var columns = _owner.CaptureLibraryColumns();
        await _vm.SaveGridColumnsAsync(columns);
        foreach (var (key, box) in _columnBoxes) _initialColumnVisibility[key] = box.IsChecked == true;
    }

    private static string LibraryColumnHeader(string key, string fallback) => key switch
    {
        LibraryColumns.Installed => Strings.Settings_Library_ColumnInstalled,
        LibraryColumns.Icon => Strings.Main_Column_Icon,
        LibraryColumns.Name => Strings.Main_Column_Name,
        LibraryColumns.Origin => Strings.Main_Column_Origin,
        LibraryColumns.AppSize => Strings.Main_Column_AppSize,
        LibraryColumns.Version => Strings.Main_Column_Version,
        LibraryColumns.MinOs => "iOS",
        LibraryColumns.DeviceFamily => Strings.Main_Column_DeviceFamily,
        LibraryColumns.BundleId => "BundleId",
        LibraryColumns.Arch => Strings.Main_Column_Arch,
        LibraryColumns.FileSize => Strings.Main_Column_FileSize,
        LibraryColumns.State => Strings.Main_Column_State,
        LibraryColumns.FilePath => Strings.Main_Column_FilePath,
        _ => fallback,
    };

    private static string ScopeName(SearchField field) => field switch
    {
        SearchField.Name => Strings.Main_Column_Name,
        SearchField.BundleId => "BundleId",
        SearchField.FileName => Strings.Main_Library_SearchFieldFileName,
        SearchField.Version => Strings.Main_Column_Version,
        SearchField.MinOs => Strings.Main_Column_MinOs,
        SearchField.DeviceFamily => Strings.Main_Column_DeviceFamily,
        SearchField.Origin => Strings.Main_Column_Origin,
        SearchField.Arch => Strings.Main_Column_Arch,
        SearchField.FilePath => Strings.Main_Column_FilePath,
        SearchField.State => Strings.Main_Column_State,
        SearchField.AppSize => Strings.Main_Column_AppSize,
        SearchField.FileSize => Strings.Main_Column_FileSize,
        _ => field.ToString(),
    };

    private void ReloadRoots()
    {
        var entries = _vm.ScanRootEntries()
            .Select(e => new RootItem(e.Row, e.Label, e.Packages))
            .ToList();
        RootsList.ItemsSource = entries;
        RootsHint.Text = entries.Count == 0
            ? Strings.Settings_Scan_NoRoots
            : Strings.Format(nameof(Strings.Settings_Scan_RootsCountFormat), entries.Count);
    }

    // ── 草稿 ↔ 控件 ─────────────────────────────────────────────────────────

    private void LoadDraftToControls()
    {
        _loading = true;
        try
        {
            RowHeightSlider.Value = _draft.RowHeight;
            RowHeightBox.Text = LibraryPrefs.FormatRowHeight(_draft.RowHeight);
            SearchRowsBox.Text = _draft.SearchDebounceRows.ToString(CultureInfo.InvariantCulture);
            SearchDelayBox.Text = _draft.SearchDebounceMs.ToString(CultureInfo.InvariantCulture);

            var sortIndex = _sortOptions.FindIndex(o => o.Field == _draft.Sort.Field);
            SortFieldCombo.SelectedIndex = sortIndex >= 0 ? sortIndex : 0;
            SortDescendingCheck.IsChecked = _draft.Sort.Descending;

            var matchIndex = _matchOptions.FindIndex(o => o.Mode == _draft.MatchMode);
            MatchModeCombo.SelectedIndex = matchIndex >= 0 ? matchIndex : 0;

            foreach (var (field, box) in _scopeBoxes) box.IsChecked = _draft.Scope.Includes(field);

            MergeCheck.IsChecked = _draft.MergeSameBundle;
            MissingCheck.IsChecked = _draft.ShowMissingFiles;
            DeviceFamilyRetinaCheck.IsChecked = _draft.DeviceFamilyRetinaText;

            BatchSizeBox.Text = _draft.ScanBatchSize.ToString(CultureInfo.InvariantCulture);
            SkipStartupScanCheck.IsChecked = _draft.SkipStartupScan;

            CacheMaxBox.Text = _draft.IconCacheMaxMb.ToString(CultureInfo.InvariantCulture);
            IconMatchScaleCheck.IsChecked = _draft.IconMatchDisplayScale;
            IconDownscaleCheck.IsChecked = _draft.IconDownscaleCache;
            IconBucketHint.Text = Strings.Settings_Icons_BucketHint;

            LogToFileCheck.IsChecked = _draft.LogToFile;
            LogDetailCheck.IsChecked = _draft.LogDetailMode;

            LogHiddenCheck.IsChecked = _draft.LogHidden;
            OverlayWidthBox.Text = _draft.LogOverlayWidth.ToString("0", CultureInfo.InvariantCulture);

            var normalizedLang = UiLanguage.NormalizeSetting(_draft.UiLanguage);
            var langIndex = 0;
            for (var i = 0; i < LanguageCombo.Items.Count; i++)
            {
                if (LanguageCombo.Items[i] is System.Windows.Controls.ComboBoxItem item &&
                    string.Equals(item.Tag as string, normalizedLang, StringComparison.Ordinal))
                {
                    langIndex = i;
                    break;
                }
            }
            LanguageCombo.SelectedIndex = langIndex;

            PollSecondsBox.Text = _draft.PollSeconds.ToString(CultureInfo.InvariantCulture);
            StableSecondsBox.Text = _draft.ReconnectStableSeconds.ToString(CultureInfo.InvariantCulture);
            AutoRetryAttemptsBox.Text = _draft.AutoRetryAttempts.ToString(CultureInfo.InvariantCulture);
            AutoResumeCheck.IsChecked = _draft.AutoResume;

            _fields.Clear();
            foreach (var setting in _draft.DeviceFields.OrderBy(f => f.Order))
                _fields.Add(new FieldItem(setting.Field, setting.Visible));
            FieldsList.ItemsSource = _fields;

            _queueFields.Clear();
            foreach (var setting in _draft.QueueColumns.OrderBy(f => f.Order))
                _queueFields.Add(new QueueFieldItem(setting.Column, setting.Visible));
            QueueFieldsList.ItemsSource = _queueFields;

            AllowOutsideWriteCheck.IsChecked = _draft.AllowOutsideProgramDirWrite;
        }
        finally
        {
            _loading = false;
        }

        UpdateDerivedHints();
    }

    private void ReadControlsToDraft()
    {
        _draft.RowHeight = LibraryPrefs.ParseRowHeight(RowHeightBox.Text);
        _draft.SearchDebounceRows = ParseInt(SearchRowsBox.Text, _draft.SearchDebounceRows,
            LibraryPrefs.MinSearchDebounceRows, LibraryPrefs.MaxSearchDebounceRows);
        _draft.SearchDebounceMs = ParseInt(SearchDelayBox.Text, _draft.SearchDebounceMs,
            LibraryPrefs.MinSearchDebounceMs, LibraryPrefs.MaxSearchDebounceMs);

        var sortField = _sortOptions[Math.Max(0, SortFieldCombo.SelectedIndex)].Field;
        _draft.Sort = new LibrarySort(sortField, SortDescendingCheck.IsChecked == true);

        _draft.MatchMode = _matchOptions[Math.Max(0, MatchModeCombo.SelectedIndex)].Mode;

        var scope = new SearchScope();
        foreach (var (field, box) in _scopeBoxes) scope.Set(field, box.IsChecked == true);
        _draft.Scope = scope;

        _draft.MergeSameBundle = MergeCheck.IsChecked == true;
        _draft.ShowMissingFiles = MissingCheck.IsChecked == true;
        _draft.DeviceFamilyRetinaText = DeviceFamilyRetinaCheck.IsChecked == true;

        _draft.ScanBatchSize = ParseInt(BatchSizeBox.Text, _draft.ScanBatchSize, 1, ScanOptions.MaxBatchSize);
        _draft.SkipStartupScan = SkipStartupScanCheck.IsChecked == true;

        _draft.IconCacheMaxMb = ParseInt(CacheMaxBox.Text, _draft.IconCacheMaxMb, 64, 20480);
        _draft.IconMatchDisplayScale = IconMatchScaleCheck.IsChecked == true;
        _draft.IconDownscaleCache = IconDownscaleCheck.IsChecked == true;

        _draft.LogToFile = LogToFileCheck.IsChecked == true;
        _draft.LogDetailMode = LogDetailCheck.IsChecked == true;

        _draft.LogHidden = LogHiddenCheck.IsChecked == true;
        _draft.LogOverlayWidth = ParseInt(OverlayWidthBox.Text, (int)_draft.LogOverlayWidth, 260, 900);
        _draft.UiLanguage = UiLanguage.NormalizeSetting(
            (LanguageCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string);

        _draft.PollSeconds = ParseInt(PollSecondsBox.Text, _draft.PollSeconds, 1, 10);
        _draft.ReconnectStableSeconds = ParseInt(StableSecondsBox.Text, _draft.ReconnectStableSeconds, 1, 10);
        _draft.AutoRetryAttempts = ParseInt(AutoRetryAttemptsBox.Text, _draft.AutoRetryAttempts, 2, 5);
        _draft.AutoResume = AutoResumeCheck.IsChecked == true;

        _draft.DeviceFields = _fields
            .Select((item, index) => new DeviceInfoFieldSetting(item.Field, item.Visible, index))
            .ToList();
        _draft.QueueColumns = _queueFields
            .Select((item, index) => new QueueColumnSetting(item.Column, item.Visible, index))
            .ToList();

        _draft.AllowOutsideProgramDirWrite = AllowOutsideWriteCheck.IsChecked == true;
    }

    private static int ParseInt(string? raw, int fallback, int min, int max)
    {
        if (!int.TryParse(raw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return fallback;
        return Math.Clamp(value, min, max);
    }

    /// <summary>
    /// 数值框失焦：把非法/越界输入规范成实际生效值并回填（审查 N5：之前输入 `abc` 会静默用旧值、文本还留着）。
    /// </summary>
    private void OnNumericLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || _closed) return;
        ReadControlsToDraft();
        _dirty = true;
        LoadDraftToControls();   // 用规范化后的草稿回填所有控件（含滑杆/下拉，简单且一致）
        SchedulePreview();
    }

    private void UpdateDerivedHints()
    {
        if (RootsList.ItemsSource is IEnumerable<RootItem> items)
        {
            var count = items.Count();
            RootsHint.Text = count == 0
                ? Strings.Settings_Scan_NoRoots
                : Strings.Format(nameof(Strings.Settings_Scan_RootsCountFormat), count);
        }
    }

    // ── 事件 ────────────────────────────────────────────────────────────────

    /// <summary>行高滑杆：先把值同步到输入框，再走统一的"改草稿"路径（输入框是唯一事实来源）。</summary>
    private void OnRowHeightSliderChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _closed) return;
        RowHeightBox.Text = LibraryPrefs.FormatRowHeight(RowHeightSlider.Value);
        Changed();
    }

    private void OnAnyChanged(object sender, RoutedEventArgs e) => Changed();

    private void OnAnyChanged(object sender, EventArgs e) => Changed();

    private void OnFieldCheckChanged(object sender, RoutedEventArgs e) => Changed();

    private void OnAllowOutsideWriteClick(object sender, RoutedEventArgs e)
    {
        if (_loading || _closed) return;
        if (AllowOutsideWriteCheck.IsChecked == true && !ConfirmAllowOutsideWrite())
        {
            _loading = true;
            try { AllowOutsideWriteCheck.IsChecked = false; }
            finally { _loading = false; }
            return;
        }

        Changed();
    }

    /// <summary>
    /// 开启「允许程序在程序目录外写入」时的独立声明弹窗（docs/12 §3.4.2）：
    /// 不放任何勾选框；默认焦点在「取消」。
    /// </summary>
    private bool ConfirmAllowOutsideWrite()
    {
        var cancelBtn = new System.Windows.Controls.Button
        {
            Content = Strings.Settings_Security_ConfirmCancel,
            Width = 76,
            Padding = new Thickness(0, 4, 0, 4),
            IsCancel = true,
            IsDefault = true,
        };
        var enableBtn = new System.Windows.Controls.Button
        {
            Content = Strings.Settings_Security_ConfirmEnable,
            Width = 76,
            Padding = new Thickness(0, 4, 0, 4),
            Margin = new Thickness(8, 0, 0, 0),
        };

        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        buttons.Children.Add(cancelBtn);
        buttons.Children.Add(enableBtn);

        var body = new System.Windows.Controls.TextBlock
        {
            Text = Strings.Settings_Security_ConfirmBody,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
        };

        var root = new System.Windows.Controls.DockPanel { Margin = new Thickness(16) };
        System.Windows.Controls.DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(body);

        var dialog = new Window
        {
            Title = Strings.Settings_Security_ConfirmTitle,
            Owner = this,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = root,
        };
        enableBtn.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => cancelBtn.Focus();
        return dialog.ShowDialog() == true;
    }

    /// <summary>任何设置改动：读草稿 + 置 dirty + 300 ms 防抖后预览（审查 N8：文本框每键重建扫描器太浪费）。</summary>
    private void Changed()
    {
        if (_loading || _closed) return;
        ReadControlsToDraft();
        _dirty = true;
        SchedulePreview();
    }

    private void SchedulePreview()
    {
        if (_loading || _closed) return;
        _previewTimer.Stop();
        _previewTimer.Start();
        UpdateDerivedHints();
    }

    private void Preview()
    {
        _vm.ApplySettings(_draft, persist: false);
        _owner.ApplySettingsLayout(_draft.LogHidden, _draft.LogOverlayWidth);
    }

    private void OnManageGroupsSettingsClick(object sender, RoutedEventArgs e)
    {
        var manager = new GroupManagerWindow(_vm) { Owner = this };
        manager.ShowDialog();
    }

    private void OnResetGroupClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (!Enum.TryParse<SettingsGroup>(tag, out var group)) return;

        _draft.ResetGroup(group);
        LoadDraftToControls();
        _dirty = true;
        Preview();
    }

    private void OnResetColumnsClick(object sender, RoutedEventArgs e)
    {
        if (_closed) return;
        // 审查 N2：立即生效的动作（不进草稿），界面上已标注"不受取消影响"
        _owner.ResetSettingsLayoutColumns();
    }

    private void OnAddRootClick(object sender, RoutedEventArgs e)
        => SafeFireAndForget.RunHandler(async () =>
        {
            if (_closed) return;
            var dialog = new OpenFolderDialog { Title = Strings.Settings_Scan_AddRootDialogTitle };
            if (dialog.ShowDialog(this) != true) return;

            var (ok, error) = await _vm.AddScanRootAsync(dialog.FolderName);

            // 审查 N4：await 期间用户可能已关窗 → 只写日志/状态栏，不再弹 MessageBox / 碰控件
            if (_closed) return;
            if (!ok)
            {
                MessageBox.Show(this,
                    Strings.Format(nameof(Strings.Settings_Scan_AddRootFailedFormat), error ?? Strings.Settings_Scan_UnknownError),
                    Strings.Settings_Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ReloadRoots();
        }, "添加扫描根");

    private void OnDeleteRootClick(object sender, RoutedEventArgs e)
        => SafeFireAndForget.RunHandler(async () =>
        {
            if (_closed) return;
            if (RootsList.SelectedItem is not RootItem item) return;

            var answer = MessageBox.Show(this,
                Strings.Format(nameof(Strings.Settings_Scan_DeleteRootConfirmFormat), item.Display, item.Packages),
                Strings.Settings_Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;

            var (ok, error) = await _vm.DeleteScanRootAsync(item.Row);

            if (_closed) return;   // 审查 N4：关窗后不再弹框/碰控件
            if (!ok)
            {
                MessageBox.Show(this,
                    Strings.Format(nameof(Strings.Settings_Scan_DeleteRootFailedFormat), error ?? Strings.Settings_Scan_UnknownError),
                    Strings.Settings_Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ReloadRoots();
        }, "删除扫描根");

    private void OnFieldUpClick(object sender, RoutedEventArgs e) => MoveField(-1);

    private void OnFieldDownClick(object sender, RoutedEventArgs e) => MoveField(1);

    private void MoveField(int delta)
    {
        var index = FieldsList.SelectedIndex;
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _fields.Count) return;

        var item = _fields[index];
        _fields.Move(index, target);
        FieldsList.SelectedIndex = target;
        ReadControlsToDraft();
        _dirty = true;
        SchedulePreview();
    }

    private void OnQueueFieldCheckChanged(object sender, RoutedEventArgs e) => Changed();

    private void OnQueueFieldUpClick(object sender, RoutedEventArgs e) => MoveQueueField(-1);

    private void OnQueueFieldDownClick(object sender, RoutedEventArgs e) => MoveQueueField(1);

    private void MoveQueueField(int delta)
    {
        var index = QueueFieldsList.SelectedIndex;
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _queueFields.Count) return;

        var item = _queueFields[index];
        _queueFields.Move(index, target);
        QueueFieldsList.SelectedIndex = target;
        ReadControlsToDraft();
        _dirty = true;
        SchedulePreview();
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
        => SafeFireAndForget.RunHandler(async () =>
        {
            if (_closed || _applying) return;
            _applying = true;
            SetActionButtonsEnabled(false);
            try
            {
                await ApplyAllAsync(persist: true);
                _applied = true;
                _closed = true;
                if (IsVisible) Close();
            }
            finally
            {
                _applying = false;
                if (!_closed) SetActionButtonsEnabled(true);
            }
        }, "确定设置");

    private void OnApplyClick(object sender, RoutedEventArgs e)
        => SafeFireAndForget.RunHandler(async () =>
        {
            if (_closed || _applying) return;
            _applying = true;
            SetActionButtonsEnabled(false);
            try
            {
                await ApplyAllAsync(persist: true);
            }
            finally
            {
                _applying = false;
                if (!_closed) SetActionButtonsEnabled(true);
            }
        }, "应用设置");

    private void SetActionButtonsEnabled(bool enabled)
    {
        OkButton.IsEnabled = enabled;
        ApplyButton.IsEnabled = enabled;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private async Task ApplyAllAsync(bool persist)
    {
        if (_closed) return;
        _previewTimer.Stop();
        ReadControlsToDraft();
        _vm.ApplySettings(_draft, persist);
        _owner.ApplySettingsLayout(_draft.LogHidden, _draft.LogOverlayWidth);

        if (persist)
        {
            await SaveColumnVisibilityAsync();
            // 审查 N7：一次事务写完再返回（之前每键 fire-and-forget，关程序可能丢写）
            await _vm.FlushSettingsAsync();

            // 审查 N1：应用之后**取消**不该把界面回滚到"打开窗口时"（库已是新值）；
            // 验收基线改为当前草稿——之后取消只会撤销"应用之后又改的那部分"。
            _initial = _draft.Clone();
            _dirty = false;
        }

        UpdateDerivedHints();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _previewTimer.Stop();

        // 审查 N6：没改过就别回滚（否则行高/布局/列宽会白跳一下）；
        // 审查 N1：改过且没点确定/应用 → 回滚到"最后一次持久化的基线"（初始值或上次应用的值）。
        if (!_applied && _dirty)
        {
            _vm.ApplySettings(_initial, persist: false);
            _owner.ApplySettingsLayout(_initial.LogHidden, _initial.LogOverlayWidth);
            RestoreColumnVisibility();
        }

        base.OnClosed(e);
    }

    // ── 列表项模型 ──────────────────────────────────────────────────────────

    private sealed class RootItem
    {
        public RootItem(ScanRootRow row, string label, int packages)
        {
            Row = row;
            Label = label;
            Packages = packages;
        }

        public ScanRootRow Row { get; }
        public string Label { get; }
        public int Packages { get; }
        public string Display => Strings.Format(nameof(Strings.Settings_Scan_RootItemFormat), Label, Row.Path, Packages);

        public override string ToString() => Display;
    }

    private sealed class FieldItem : ObservableObject
    {
        private bool _visible;

        public FieldItem(DeviceInfoField field, bool visible)
        {
            Field = field;
            _visible = visible;
        }

        public DeviceInfoField Field { get; }

        public string Name => Field switch
        {
            DeviceInfoField.Name => Strings.Settings_Device_FieldName,
            DeviceInfoField.Product => Strings.Settings_Device_FieldProduct,
            DeviceInfoField.Version => Strings.Settings_Device_FieldVersion,
            DeviceInfoField.Capacity => Strings.Settings_Device_FieldCapacity,
            DeviceInfoField.Color => Strings.Settings_Device_FieldColor,
            DeviceInfoField.BatteryHealth => Strings.Settings_Device_FieldBatteryHealth,
            DeviceInfoField.Ecid => "ECID",
            _ => Field.ToString(),
        };

        public bool Visible
        {
            get => _visible;
            set => SetProperty(ref _visible, value);
        }
    }

    private void OnOpenLicensesClick(object sender, RoutedEventArgs e)
    {
        var text =
            "Legacy IPA Manager for Windows\r\n" +
            "License: GNU GPL v2 or later (LICENSE / COPYING).\r\n\r\n" +
            "Bundled third-party components:\r\n" +
            "· aria2 1.37.0 — GNU GPL v2; see aria2-LICENSE.txt, aria2-LICENSE-OpenSSL.txt and aria2-SOURCE.txt in the program directory.\r\n" +
            "· libimobiledevice / libusbmuxd / libplist / OpenSSL / libzip / zlib / libbz2 / liblzma / libzstd — see the notices shipped with the toolkit.\r\n\r\n" +
            "This is an unofficial third-party tool, not affiliated with Apple Inc.";
        MessageBox.Show(this, text, "Third-party licenses", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private sealed class QueueFieldItem : ObservableObject
    {
        private bool _visible;

        public QueueFieldItem(QueueColumn column, bool visible)
        {
            Column = column;
            _visible = visible;
        }

        public QueueColumn Column { get; }
        public string Name => QueueColumnHeader(Column);

        private static string QueueColumnHeader(QueueColumn column) => column switch
        {
            QueueColumn.Status => Strings.Settings_Queue_ColStatus,
            QueueColumn.Note => Strings.Settings_Queue_ColNote,
            QueueColumn.Name => Strings.Main_Column_Name,
            QueueColumn.Version => Strings.Main_Column_Version,
            QueueColumn.InstalledSize => Strings.Settings_Queue_ColInstalledSize,
            QueueColumn.BundleId => "BundleId",
            QueueColumn.FilePath => Strings.Main_Column_FilePath,
            QueueColumn.Origin => Strings.Main_Column_Origin,
            QueueColumn.MinOs => Strings.Main_Column_MinOs,
            QueueColumn.DeviceFamily => Strings.Main_Column_DeviceFamily,
            _ => column.ToString(),
        };

        public bool Visible
        {
            get => _visible;
            set => SetProperty(ref _visible, value);
        }
    }
}
