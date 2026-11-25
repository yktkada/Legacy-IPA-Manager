using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using IdeviceIndex.Resources;

namespace IdeviceIndex;

internal enum DownloadJobStatus
{
    Pending,
    Downloading,
    Verifying,
    Importing,
    Paused,
    Succeeded,
    Failed,
    Cancelled,
}

internal static class DownloadJobStatusText
{
    public static string ToText(this DownloadJobStatus status) => status switch
    {
        DownloadJobStatus.Pending => Strings.Queue_Status_Pending,
        DownloadJobStatus.Downloading => Strings.Queue_Status_Downloading,
        DownloadJobStatus.Verifying => Strings.Queue_Status_Verifying,
        DownloadJobStatus.Importing => Strings.Queue_Status_Importing,
        DownloadJobStatus.Paused => Strings.Queue_Status_Paused,
        DownloadJobStatus.Succeeded => Strings.Queue_Status_Succeeded,
        DownloadJobStatus.Failed => Strings.Queue_Status_Failed,
        DownloadJobStatus.Cancelled => Strings.Queue_Status_Cancelled,
        _ => Strings.Common_Unknown,
    };
}

/// <summary>
/// 下载队列行（对照适配自 <c>IdeviceGui\ViewModels\JobViewModel.cs</c>）。
/// </summary>
internal sealed class DownloadJobRow : INotifyPropertyChanged
{
    private static readonly Brush WaitingBrush = FrozenBrush(0xB4, 0xD2, 0xF0);
    private static readonly Brush ActiveBrush = FrozenBrush(0x7F, 0xA9, 0xDC);
    private static readonly Brush PausedBrush = FrozenBrush(0xD0, 0xD0, 0xD0);
    private static readonly Brush FailedBrush = FrozenBrush(0xFF, 0xD9, 0xD9);
    private static readonly Brush SucceededBrush = FrozenBrush(0xD9, 0xF2, 0xD9);

    private DownloadJobStatus _status;
    private double _percent;
    private string _speedText = "";
    private string? _note;
    private IReadOnlyList<DownloadQueueColumn> _displayColumns;

    public DownloadJobRow(
        IndexRow row,
        IReadOnlyList<DownloadQueueColumn> displayColumns,
        string? sourceSummary = null,
        string? targetPath = null,
        string? note = null)
    {
        Id = row.Id;
        _displayColumns = displayColumns;
        BundleId = row.BundleId;
        Title = row.Title;
        Version = row.Version;
        SizeText = row.SizeText;
        MinOsText = row.MinOsText;
        DeviceFamilyText = row.DeviceFamilyText;
        SourceSummary = sourceSummary ?? "";
        TargetPath = targetPath ?? "";
        _status = DownloadJobStatus.Pending;
        _note = note;
    }

    public string Id { get; }
    public long Generation { get; private set; }
    public string BundleId { get; private set; }
    public string Title { get; private set; }
    public string Version { get; private set; }
    public string SizeText { get; private set; }
    public string MinOsText { get; private set; }
    public string DeviceFamilyText { get; private set; }
    public string SourceSummary { get; private set; }
    public string TargetPath { get; private set; }

    public string DisplayNameText => string.IsNullOrWhiteSpace(Title) ? BundleId : Title;
    public string VersionText => string.IsNullOrWhiteSpace(Version) ? "—" : Version;
    public string SourceSummaryText => string.IsNullOrWhiteSpace(SourceSummary) ? "—" : SourceSummary;
    public string TargetPathText => string.IsNullOrWhiteSpace(TargetPath) ? "—" : TargetPath;

    public IReadOnlyList<DownloadQueueColumn> DisplayColumns
    {
        get => _displayColumns;
        set
        {
            _displayColumns = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Display));
        }
    }

    public DownloadJobStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            NotifyStatusChanged();
        }
    }

    public double Percent
    {
        get => _percent;
        set
        {
            if (Math.Abs(_percent - value) < 0.01) return;
            _percent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayPercentText));
            OnPropertyChanged(nameof(Display));
        }
    }

    public string SpeedText
    {
        get => _speedText;
        set
        {
            if (string.Equals(_speedText, value, StringComparison.Ordinal)) return;
            _speedText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayPercentText));
            OnPropertyChanged(nameof(Display));
        }
    }

    public string? Note
    {
        get => _note;
        set
        {
            if (string.Equals(_note, value, StringComparison.Ordinal)) return;
            _note = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Display));
        }
    }

    public string StatusText => _status.ToText();

    public string DisplayPercentText
    {
        get
        {
            if (_status != DownloadJobStatus.Downloading || _percent <= 0 || _percent >= 100) return "";
            return string.IsNullOrWhiteSpace(_speedText)
                ? $" {_percent:0}%"
                : $" {_percent:0}% ({_speedText})";
        }
    }

    public DownloadRowVisual Visual => _status switch
    {
        DownloadJobStatus.Pending => DownloadRowVisual.Waiting,
        DownloadJobStatus.Downloading or DownloadJobStatus.Verifying or DownloadJobStatus.Importing => DownloadRowVisual.Active,
        DownloadJobStatus.Paused => DownloadRowVisual.Paused,
        DownloadJobStatus.Failed => DownloadRowVisual.Failed,
        DownloadJobStatus.Succeeded => DownloadRowVisual.Succeeded,
        _ => DownloadRowVisual.None,
    };

    public Brush RowBrush => Visual switch
    {
        DownloadRowVisual.Waiting => WaitingBrush,
        DownloadRowVisual.Active => ActiveBrush,
        DownloadRowVisual.Paused => PausedBrush,
        DownloadRowVisual.Failed => FailedBrush,
        DownloadRowVisual.Succeeded => SucceededBrush,
        _ => Brushes.Transparent,
    };

    public bool CanCancel => _status is DownloadJobStatus.Pending
        or DownloadJobStatus.Downloading
        or DownloadJobStatus.Verifying
        or DownloadJobStatus.Importing
        or DownloadJobStatus.Paused;

    public string Display => DownloadQueueColumns.Format(this, _displayColumns);

    public override string ToString() => Display;

    internal long BumpGeneration() => ++Generation;

    public void UpdateFrom(
        IndexRow row,
        string? sourceSummary = null,
        string? targetPath = null,
        string? note = null,
        DownloadJobStatus initialStatus = DownloadJobStatus.Pending)
    {
        BundleId = row.BundleId;
        Title = row.Title;
        Version = row.Version;
        SizeText = row.SizeText;
        MinOsText = row.MinOsText;
        DeviceFamilyText = row.DeviceFamilyText;
        SourceSummary = sourceSummary ?? "";
        TargetPath = targetPath ?? "";
        _percent = 0;
        _speedText = "";
        _status = initialStatus;
        _note = note;
        OnPropertyChanged(nameof(BundleId));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(DisplayNameText));
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(MinOsText));
        OnPropertyChanged(nameof(DeviceFamilyText));
        OnPropertyChanged(nameof(SourceSummary));
        OnPropertyChanged(nameof(SourceSummaryText));
        OnPropertyChanged(nameof(TargetPath));
        OnPropertyChanged(nameof(TargetPathText));
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(Note));
        NotifyStatusChanged();
    }

    internal void ApplySnapshot(DownloadProgressSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.TargetPath) &&
            !string.Equals(TargetPath, snapshot.TargetPath, StringComparison.Ordinal))
        {
            TargetPath = snapshot.TargetPath;
            OnPropertyChanged(nameof(TargetPath));
            OnPropertyChanged(nameof(TargetPathText));
        }

        if (!string.IsNullOrWhiteSpace(snapshot.SourceSummary) &&
            !string.Equals(SourceSummary, snapshot.SourceSummary, StringComparison.Ordinal))
        {
            SourceSummary = snapshot.SourceSummary;
            OnPropertyChanged(nameof(SourceSummary));
            OnPropertyChanged(nameof(SourceSummaryText));
        }

        Percent = snapshot.Percent;
        SpeedText = snapshot.SpeedText;
        Note = snapshot.Note;
        Status = snapshot.Status;
    }

    private void NotifyStatusChanged()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DisplayPercentText));
        OnPropertyChanged(nameof(Visual));
        OnPropertyChanged(nameof(RowBrush));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(Display));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static SolidColorBrush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
