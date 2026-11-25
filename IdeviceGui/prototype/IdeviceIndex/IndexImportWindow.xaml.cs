using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using IdeviceIndex.Resources;
using Microsoft.Win32;

namespace IdeviceIndex;

internal partial class IndexImportWindow : Window
{
    private readonly string _programDir;
    private readonly string? _currentDbPath;
    private readonly bool _currentIsExternal;
    private readonly string? _settingsPath;
    private readonly Action<string>? _onImported;

    private CancellationTokenSource? _cts;
    private bool _running;
    private bool _closed;
    private bool _userTouchedJsonMode;

    public string? ImportedDbPath { get; private set; }

    public IndexImportWindow(
        string? programDir = null,
        string? currentDbPath = null,
        bool currentIsExternal = false,
        string? settingsPath = null,
        Action<string>? onImported = null,
        Window? owner = null)
    {
        InitializeComponent();
        Owner = owner;
        _programDir = string.IsNullOrWhiteSpace(programDir) ? AppContext.BaseDirectory : programDir;
        _currentDbPath = string.IsNullOrWhiteSpace(currentDbPath) ? null : currentDbPath;
        _currentIsExternal = currentIsExternal;
        _settingsPath = settingsPath;
        _onImported = onImported;

        // Avoid synchronous File.Exists on the UI thread (P2-index-new-1); refine after Loaded in Task.Run.
        var hasBaseHint = !string.IsNullOrWhiteSpace(_currentDbPath);
        JsonIncrementalRadio.IsChecked = hasBaseHint;
        JsonFullReplaceRadio.IsChecked = !hasBaseHint;

        JsonIncrementalRadio.Checked += (_, _) => _userTouchedJsonMode = true;
        JsonFullReplaceRadio.Checked += (_, _) => _userTouchedJsonMode = true;

        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;
        Closed += (_, _) =>
        {
            _closed = true;
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            _cts?.Dispose();
            _cts = null;
        };

        UpdateModePanels();
        UpdateRemoteButtonState();
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        var savedUrlTask = Task.Run(() => IndexerSettings.LoadUpdateUrl(_settingsPath));
        var dbExistsTask = !string.IsNullOrWhiteSpace(_currentDbPath)
            ? Task.Run(() =>
            {
                try { return File.Exists(_currentDbPath); } catch { return false; }
            })
            : Task.FromResult(false);

        await Task.WhenAll(savedUrlTask, dbExistsTask);
        if (_closed) return;

        if (string.IsNullOrWhiteSpace(UpdateUrlBox.Text) && !string.IsNullOrWhiteSpace(savedUrlTask.Result))
            UpdateUrlBox.Text = savedUrlTask.Result;

        if (!_userTouchedJsonMode && !_running)
        {
            var hasBaseDb = dbExistsTask.Result;
            JsonIncrementalRadio.IsChecked = hasBaseDb;
            JsonFullReplaceRadio.IsChecked = !hasBaseDb;
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_running) return;
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        e.Cancel = true;
    }

    private void OnFilePathChanged(object sender, TextChangedEventArgs e) => UpdateModePanels();

    private void OnUpdateUrlChanged(object sender, TextChangedEventArgs e) => UpdateRemoteButtonState();

    private void UpdateModePanels()
    {
        var path = (FilePathBox.Text ?? "").Trim();
        var isDb = path.EndsWith(".db", StringComparison.OrdinalIgnoreCase);
        DbModePanel.Visibility = isDb ? Visibility.Visible : Visibility.Collapsed;
        JsonModePanel.Visibility = isDb ? Visibility.Collapsed : Visibility.Visible;
        ImportButton.IsEnabled = !_running && path.Length > 0;
    }

    private void UpdateRemoteButtonState()
    {
        var url = (UpdateUrlBox.Text ?? "").Trim();
        DownloadUpdateButton.IsEnabled = !_running && url.Length > 0;
    }

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        var currentText = FilePathBox.Text;
        var initialDir = await Task.Run(() => ResolveInitialBrowseDirectory(currentText, _programDir));
        if (_closed || _running) return;

        var dialog = new OpenFileDialog
        {
            Title = Strings.Import_DialogTitle,
            Filter = Strings.Import_FileFilter,
            CheckFileExists = true,
            InitialDirectory = initialDir,
        };
        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FileName))
            FilePathBox.Text = dialog.FileName;
    }

    private static string ResolveInitialBrowseDirectory(string? currentText, string programDir)
    {
        if (!string.IsNullOrWhiteSpace(currentText))
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(currentText.Trim()));
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    return dir;
            }
            catch
            {
                // Fall back to program directory
            }
        }
        try
        {
            return Directory.Exists(programDir) ? programDir : AppContext.BaseDirectory;
        }
        catch
        {
            return AppContext.BaseDirectory;
        }
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        if (_running || _closed) return;
        var sourcePath = (FilePathBox.Text ?? "").Trim();
        if (sourcePath.Length == 0) return;

        await RunImportTaskAsync(async (progress, ct) =>
        {
            if (sourcePath.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            {
                var copy = DbCopyRadio.IsChecked == true;
                var validation = await IndexImporter.ImportDatabaseFileAsync(
                    sourcePath,
                    _programDir,
                    copy,
                    _settingsPath,
                    ct);

                var resultingDbPath = copy
                    ? IndexImporter.DefaultDbPath(_programDir)
                    : Path.GetFullPath(sourcePath);

                return (
                    resultingDbPath,
                    Strings.Format(nameof(Strings.Import_SuccessDbFormat), validation.EntryCount));
            }

            var mode = JsonIncrementalRadio.IsChecked == true
                ? IndexImportMode.Incremental
                : IndexImportMode.FullReplace;
            var options = new IndexImportOptions(
                mode,
                MergeSources: MergeSourcesCheck.IsChecked == true,
                VerifyBaseRevision: VerifyBaseRevisionCheck.IsChecked == true);

            var targetDbPath = IndexImporter.DefaultDbPath(_programDir);
            var externalSeed = mode == IndexImportMode.Incremental && _currentIsExternal
                ? _currentDbPath
                : null;

            var result = await IndexImporter.ImportJsonAsync(
                sourcePath,
                targetDbPath,
                options,
                externalSeed,
                progress,
                ct);

            await Task.Run(() => IndexerSettings.SaveCustomDbPath("", _settingsPath));
            return (
                result.TargetDbPath,
                Strings.Format(
                    nameof(Strings.Import_SuccessJsonFormat),
                    result.TotalEntries,
                    result.UpsertedCount,
                    result.DeletedCount));
        });
    }

    private async void OnDownloadUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_running || _closed) return;
        var url = (UpdateUrlBox.Text ?? "").Trim();
        if (url.Length == 0) return;

        var mode = JsonIncrementalRadio.IsChecked == true
            ? IndexImportMode.Incremental
            : IndexImportMode.FullReplace;
        var mergeSources = MergeSourcesCheck.IsChecked == true;

        await Task.Run(() =>
        {
            try { IndexerSettings.SaveUpdateUrl(url, _settingsPath); } catch { }
        });

        await RunImportTaskAsync(async (progress, ct) =>
        {
            var result = await IndexImporter.DownloadAndApplyUpdateAsync(
                url,
                _programDir,
                _currentDbPath,
                mode,
                mergeSources,
                _settingsPath,
                progress,
                ct);

            return (
                result.TargetDbPath,
                Strings.Format(
                    nameof(Strings.Import_SuccessJsonFormat),
                    result.TotalEntries,
                    result.UpsertedCount,
                    result.DeletedCount));
        });
    }

    private async Task RunImportTaskAsync(
        Func<IProgress<IndexImportProgress>, CancellationToken, Task<(string DbPath, string Summary)>> work)
    {
        _running = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        SetBusyUi(true);
        ImportProgressBar.IsIndeterminate = true;
        ImportProgressBar.Value = 0;

        var progress = new Progress<IndexImportProgress>(p =>
        {
            if (_closed) return;
            if (!string.IsNullOrWhiteSpace(p.Message))
                ImportStatusText.Text = p.Message;
            else if (p.TotalCount is > 0)
            {
                ImportProgressBar.IsIndeterminate = false;
                ImportProgressBar.Value = Math.Min(99d, 100d * p.ProcessedCount / p.TotalCount.Value);
                ImportStatusText.Text = Strings.Format(
                    nameof(Strings.Import_ProgressFormat), p.ProcessedCount, p.TotalCount.Value);
            }
            else
            {
                ImportProgressBar.IsIndeterminate = true;
                ImportStatusText.Text = Strings.Format(
                    nameof(Strings.Import_ProgressCountFormat), p.ProcessedCount);
            }
        });

        try
        {
            var (resultingDbPath, summary) = await work(progress, _cts.Token);
            if (_closed) return;

            ImportProgressBar.IsIndeterminate = false;
            ImportProgressBar.Value = 100;
            ImportStatusText.Text = summary;

            ImportedDbPath = resultingDbPath;
            _running = false;
            _onImported?.Invoke(resultingDbPath);
            if (IsVisible)
            {
                try { DialogResult = true; } catch (InvalidOperationException) { }
                Close();
            }
        }
        catch (OperationCanceledException)
        {
            ImportProgressBar.IsIndeterminate = false;
            ImportProgressBar.Value = 0;
            ImportStatusText.Text = Strings.Import_Cancelled;
        }
        catch (Exception ex)
        {
            ImportProgressBar.IsIndeterminate = false;
            ImportProgressBar.Value = 0;
            ImportStatusText.Text = ex.Message;
        }
        finally
        {
            _running = false;
            if (!_closed)
                SetBusyUi(false);
        }
    }

    private void OnCancelOrCloseClick(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            CancelCloseButton.IsEnabled = false;
            return;
        }
        Close();
    }

    private void SetBusyUi(bool busy)
    {
        FilePathBox.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        UpdateUrlBox.IsEnabled = !busy;
        DbModePanel.IsEnabled = !busy;
        JsonModePanel.IsEnabled = !busy;
        ImportButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(FilePathBox.Text);
        DownloadUpdateButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(UpdateUrlBox.Text);
        CancelCloseButton.IsEnabled = true;
        CancelCloseButton.Content = busy ? Strings.Import_CancelButton : Strings.Import_CloseButton;
    }
}
