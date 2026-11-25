using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using IdeviceIndex.Resources;
using Microsoft.Win32;

namespace IdeviceIndex;

public partial class SettingsWindow : Window
{
    private readonly Action<IndexSettingsDraft>? _onApplied;
    private readonly Action<string>? _onIndexImported;
    private readonly string _programDir;
    private readonly string? _settingsPath;
    private readonly ObservableCollection<SectionItem> _sectionItems = [];
    private readonly ObservableCollection<QueueColumnItem> _queueColumnItems = [];
    private string? _currentDbPath;
    private IndexSettingsDraft _draft;
    private bool _loading;
    private bool _applying;
    private bool _closed;

    internal SettingsWindow(
        Action<IndexSettingsDraft>? onApplied = null,
        string? currentDbPath = null,
        Action<string>? onIndexImported = null,
        string? settingsPath = null,
        string? programDir = null)
    {
        InitializeComponent();
        _onApplied = onApplied;
        _currentDbPath = string.IsNullOrWhiteSpace(currentDbPath) ? null : currentDbPath;
        _onIndexImported = onIndexImported;
        _settingsPath = settingsPath;
        _programDir = string.IsNullOrWhiteSpace(programDir) ? AppContext.BaseDirectory : programDir;
        _draft = IndexSettingsDraft.Load(_settingsPath, _programDir);

        RightSectionsList.ItemsSource = _sectionItems;
        QueueColumnsList.ItemsSource = _queueColumnItems;

        LoadDraftToControls();
        Closing += OnWindowClosing;
        Closed += (_, _) => _closed = true;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_applying && !_closed) e.Cancel = true;
    }

    private void LoadDraftToControls()
    {
        _loading = true;
        try
        {
            SelectComboTag(LanguageCombo, UiLanguage.NormalizeSetting(_draft.UiLanguageSetting));
            DisplayLimitBox.Text = _draft.DisplayLimit.ToString(CultureInfo.InvariantCulture);
            OpenLinkProgramBox.Text = _draft.OpenLinkProgram;
            CurrentDbPathBox.Text = string.IsNullOrWhiteSpace(_currentDbPath)
                ? Strings.Status_NoIndexLoaded
                : _currentDbPath;

            _sectionItems.Clear();
            foreach (var sec in _draft.RightSections.OrderBy(s => s.Order))
                _sectionItems.Add(new SectionItem(sec.Section, sec.Visible));

            _queueColumnItems.Clear();
            foreach (var col in _draft.QueueColumns.OrderBy(c => c.Order))
                _queueColumnItems.Add(new QueueColumnItem(col.Column, col.Visible));

            var dl = _draft.Download;
            DownloadDirBox.Text = dl.Dir;
            RememberLastDirCheck.IsChecked = dl.RememberLastDir;
            ConnectionsBox.Text = dl.Connections.ToString(CultureInfo.InvariantCulture);
            SplitsBox.Text = dl.Splits.ToString(CultureInfo.InvariantCulture);
            MinSplitSizeBox.Text = dl.MinSplitSize;
            ConcurrencyBox.Text = dl.Concurrency.ToString(CultureInfo.InvariantCulture);
            SpeedLimitBox.Text = dl.SpeedLimit;
            PerTaskSpeedLimitBox.Text = dl.PerTaskSpeedLimit;
            RetryTimesBox.Text = dl.RetryTimes.ToString(CultureInfo.InvariantCulture);
            RetryWaitBox.Text = dl.RetryWait.ToString(CultureInfo.InvariantCulture);
            TimeoutBox.Text = dl.Timeout.ToString(CultureInfo.InvariantCulture);
            ContinueCheck.IsChecked = dl.Continue;

            SelectComboTag(ProxyModeCombo, dl.ProxyMode);
            ProxyUrlBox.Text = dl.ProxyUrl;
            ProxyBypassBox.Text = dl.ProxyBypass;
            UserAgentBox.Text = dl.UserAgent;

            SelectComboTag(SessionDirModeCombo, dl.SessionDirMode);
            SessionDirCustomBox.Text = dl.SessionDirCustom;
            SelectComboTag(ControlFileModeCombo, dl.ControlFileMode);

            KeepPartFilesCheck.IsChecked = dl.KeepPartFiles;
            PartTtlHoursBox.Text = dl.PartTtlHours.ToString(CultureInfo.InvariantCulture);
            SpaceMarginMbBox.Text = dl.SpaceMarginMB.ToString(CultureInfo.InvariantCulture);
            MaxFileSizeMbBox.Text = dl.MaxFileSizeMB.ToString(CultureInfo.InvariantCulture);

            AutoImportCheck.IsChecked = dl.AutoImport;
            AutoRefreshFallbackCheck.IsChecked = dl.AutoRefreshFallback;
            OpenFolderWhenDoneCheck.IsChecked = dl.OpenFolderWhenDone;

            AllowOutsideWriteCheck.IsChecked = _draft.AllowOutsideProgramDirWrite;
        }
        finally
        {
            _loading = false;
        }

        UpdateDependentControlsState();
    }

    private void ReadControlsToDraft()
    {
        _draft.UiLanguageSetting = UiLanguage.NormalizeSetting(GetSelectedComboTag(LanguageCombo, UiLanguage.SystemSetting));
        _draft.DisplayLimit = IndexerSettings.ParseDisplayLimit(DisplayLimitBox.Text);
        _draft.OpenLinkProgram = (OpenLinkProgramBox.Text ?? "").Trim();

        var sections = new List<RightSectionSetting>(_sectionItems.Count);
        for (var i = 0; i < _sectionItems.Count; i++)
            sections.Add(new RightSectionSetting(_sectionItems[i].Section, _sectionItems[i].Visible, i));
        _draft.RightSections = sections;

        var cols = new List<DownloadQueueColumnSetting>(_queueColumnItems.Count);
        for (var i = 0; i < _queueColumnItems.Count; i++)
            cols.Add(new DownloadQueueColumnSetting(_queueColumnItems[i].Column, _queueColumnItems[i].Visible, i));
        _draft.QueueColumns = cols;

        _draft.Download = IndexSettingsDraft.NormalizeFromInputs(
            dir: DownloadDirBox.Text,
            rememberLastDir: RememberLastDirCheck.IsChecked == true,
            connectionsText: ConnectionsBox.Text,
            splitsText: SplitsBox.Text,
            minSplitSize: MinSplitSizeBox.Text,
            concurrencyText: ConcurrencyBox.Text,
            speedLimit: SpeedLimitBox.Text,
            perTaskSpeedLimit: PerTaskSpeedLimitBox.Text,
            retryTimesText: RetryTimesBox.Text,
            retryWaitText: RetryWaitBox.Text,
            timeoutText: TimeoutBox.Text,
            continueDownload: ContinueCheck.IsChecked == true,
            proxyMode: GetSelectedComboTag(ProxyModeCombo, DownloadSettings.ProxySystem),
            proxyUrl: ProxyUrlBox.Text,
            proxyBypass: ProxyBypassBox.Text,
            userAgent: UserAgentBox.Text,
            sessionDirMode: GetSelectedComboTag(SessionDirModeCombo, DownloadSettings.SessionProgram),
            sessionDirCustom: SessionDirCustomBox.Text,
            controlFileMode: GetSelectedComboTag(ControlFileModeCombo, DownloadSettings.ControlSameAsPart),
            keepPartFiles: KeepPartFilesCheck.IsChecked == true,
            partTtlHoursText: PartTtlHoursBox.Text,
            spaceMarginMbText: SpaceMarginMbBox.Text,
            maxFileSizeMbText: MaxFileSizeMbBox.Text,
            autoImport: AutoImportCheck.IsChecked == true,
            autoRefreshFallback: AutoRefreshFallbackCheck.IsChecked == true,
            openFolderWhenDone: OpenFolderWhenDoneCheck.IsChecked == true);

        _draft.AllowOutsideProgramDirWrite = AllowOutsideWriteCheck.IsChecked == true;
    }

    private void UpdateDependentControlsState()
    {
        var proxyMode = GetSelectedComboTag(ProxyModeCombo, DownloadSettings.ProxySystem);
        ProxyUrlBox.IsEnabled = string.Equals(proxyMode, DownloadSettings.ProxyManual, StringComparison.Ordinal);

        var sessionMode = GetSelectedComboTag(SessionDirModeCombo, DownloadSettings.SessionProgram);
        var isCustomSession = string.Equals(sessionMode, DownloadSettings.SessionCustom, StringComparison.Ordinal);
        SessionDirCustomBox.IsEnabled = isCustomSession;
        BrowseSessionDirButton.IsEnabled = isCustomSession;
    }

    private void OnProxyModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) UpdateDependentControlsState();
    }

    private void OnSessionDirModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) UpdateDependentControlsState();
    }

    private void OnAllowOutsideWriteClick(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (AllowOutsideWriteCheck.IsChecked != true) return;

        var dialog = new ConfirmOutsideWriteWindow { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            _loading = true;
            try
            {
                AllowOutsideWriteCheck.IsChecked = false;
            }
            finally
            {
                _loading = false;
            }
        }
    }

    private void OnBrowseDownloadDirClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            InitialDirectory = string.IsNullOrWhiteSpace(DownloadDirBox.Text) ? AppContext.BaseDirectory : DownloadDirBox.Text,
        };
        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            DownloadDirBox.Text = dialog.FolderName;
    }

    private void OnBrowseSessionDirClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            InitialDirectory = string.IsNullOrWhiteSpace(SessionDirCustomBox.Text) ? AppContext.BaseDirectory : SessionDirCustomBox.Text,
        };
        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            SessionDirCustomBox.Text = dialog.FolderName;
    }

    private void OnBrowseOpenLinkProgramClick(object sender, RoutedEventArgs e)
    {
        var initialDir = AppContext.BaseDirectory;
        var current = (OpenLinkProgramBox.Text ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(current))
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(current));
                if (!string.IsNullOrWhiteSpace(dir)) initialDir = dir;
            }
            catch
            {
                // Keep default directory
            }
        }

        var dialog = new OpenFileDialog
        {
            Title = Strings.Links_ChooseProgramDialogTitle,
            Filter = Strings.Links_ExecutableFilter,
            CheckFileExists = true,
            InitialDirectory = initialDir,
        };
        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FileName))
            OpenLinkProgramBox.Text = dialog.FileName;
    }

    private void OnClearOpenLinkProgramClick(object sender, RoutedEventArgs e)
    {
        OpenLinkProgramBox.Text = "";
    }

    private void OnImportIndexClick(object sender, RoutedEventArgs e)
    {
        var defaultPath = IndexImporter.DefaultDbPath(_programDir);
        var isExternal = !string.IsNullOrWhiteSpace(_currentDbPath) &&
                         !string.Equals(Path.GetFullPath(_currentDbPath), Path.GetFullPath(defaultPath), StringComparison.OrdinalIgnoreCase);
        var dlg = new IndexImportWindow(
            _programDir,
            _currentDbPath,
            isExternal,
            _settingsPath,
            importedPath =>
            {
                _currentDbPath = importedPath;
                CurrentDbPathBox.Text = importedPath;
                _onIndexImported?.Invoke(importedPath);
            },
            owner: this);
        dlg.ShowDialog();
    }

    private void OnOpenMainLicenseClick(object sender, RoutedEventArgs e)
        => _ = OpenProgramLicenseFileAsync("LICENSE");

    private void OnOpenAria2LicenseClick(object sender, RoutedEventArgs e)
        => _ = OpenProgramLicenseFileAsync("aria2-LICENSE.txt");

    private async Task OpenProgramLicenseFileAsync(string fileName)
    {
        LicenseStatusText.Text = "";
        try
        {
            var (found, fullPath, error) = await Task.Run(() =>
            {
                var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, fileName));
                if (!File.Exists(path))
                    return (false, path, (string?)null);
                try
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    return (true, path, (string?)null);
                }
                catch (Exception ex)
                {
                    return (true, path, ex.Message);
                }
            });

            if (_closed) return;
            if (!found)
                LicenseStatusText.Text = Strings.Format(nameof(Strings.Settings_License_FileMissingFormat), fileName);
            else if (!string.IsNullOrEmpty(error))
                LicenseStatusText.Text = Strings.Format(nameof(Strings.Download_Error_FailedFormat), error);
            else
                LicenseStatusText.Text = Strings.Format(nameof(Strings.Settings_License_OpenedFormat), fullPath);
        }
        catch (Exception ex)
        {
            if (!_closed)
                LicenseStatusText.Text = Strings.Format(nameof(Strings.Download_Error_FailedFormat), ex.Message);
        }
    }

    private void OnSectionMoveUpClick(object sender, RoutedEventArgs e) => MoveListItem(_sectionItems, RightSectionsList, -1);

    private void OnSectionMoveDownClick(object sender, RoutedEventArgs e) => MoveListItem(_sectionItems, RightSectionsList, 1);

    private void OnQueueColumnMoveUpClick(object sender, RoutedEventArgs e) => MoveListItem(_queueColumnItems, QueueColumnsList, -1);

    private void OnQueueColumnMoveDownClick(object sender, RoutedEventArgs e) => MoveListItem(_queueColumnItems, QueueColumnsList, 1);

    private static void MoveListItem<T>(ObservableCollection<T> items, ListBox listBox, int delta)
    {
        var index = listBox.SelectedIndex;
        var target = index + delta;
        if (index < 0 || target < 0 || target >= items.Count) return;

        items.Move(index, target);
        listBox.SelectedIndex = target;
    }

    private void OnResetGroupClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string tag) return;
        if (!Enum.TryParse<IndexSettingsGroup>(tag, ignoreCase: true, out var group)) return;

        ReadControlsToDraft();
        _draft.ResetGroup(group);
        LoadDraftToControls();
    }

    private async void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (_closed || _applying) return;
        _applying = true;
        SetButtonsEnabled(false);
        try
        {
            await ApplyAndSaveAsync();
            _applying = false;
            _closed = true;
            if (IsVisible) Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _applying = false;
            if (!_closed) SetButtonsEnabled(true);
        }
    }

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (_closed || _applying) return;
        _applying = true;
        SetButtonsEnabled(false);
        try
        {
            await ApplyAndSaveAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _applying = false;
            if (!_closed) SetButtonsEnabled(true);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private async Task ApplyAndSaveAsync()
    {
        if (_closed) return;
        ReadControlsToDraft();
        var snapshot = _draft.Clone();
        await Task.Run(() => snapshot.Save(_settingsPath, _programDir));
        if (_closed) return;

        _draft = snapshot;
        LoadDraftToControls();
        _onApplied?.Invoke(snapshot.Clone());
    }

    private void SetButtonsEnabled(bool enabled)
    {
        OkButton.IsEnabled = enabled;
        ApplyButton.IsEnabled = enabled;
    }

    private static void SelectComboTag(ComboBox combo, string tag)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static string GetSelectedComboTag(ComboBox combo, string fallback)
        => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    private sealed class SectionItem(RightSection section, bool visible)
    {
        public RightSection Section { get; } = section;
        public bool Visible { get; set; } = visible;
        public string Label => IndexLayoutStore.Header(Section);
    }

    private sealed class QueueColumnItem(DownloadQueueColumn column, bool visible)
    {
        public DownloadQueueColumn Column { get; } = column;
        public bool Visible { get; set; } = visible;
        public string Label => DownloadQueueColumns.Header(Column);
    }
}
