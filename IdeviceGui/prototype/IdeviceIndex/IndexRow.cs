using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using IdeviceIndex.Resources;

namespace IdeviceIndex;

public enum DownloadRowVisual
{
    None,
    Waiting,
    Active,
    Paused,
    Failed,
    Succeeded,
}

public sealed class IndexRow : INotifyPropertyChanged, IMultiSelectRow
{
    public required string Id { get; init; }
    public required string BundleId { get; init; }
    public string Title { get; init; } = "";
    public string Version { get; init; } = "";
    public string MinOs { get; init; } = "";
    public long Size { get; init; }
    public bool SizeExact { get; init; }
    public int DeviceMask { get; init; }
    public int SourceCount { get; init; }
    public string IconPath { get; init; } = "";
    public string Category { get; init; } = "";
    public string Subgenre { get; init; } = "";
    public string Developer { get; init; } = "";

    public bool IsGroup { get; init; }
    public bool IsGroupMember { get; init; }
    public string GroupKey { get; init; } = "";
    public int GroupCount { get; init; }
    public string? RangeVersionText { get; init; }
    public string? RangeMinOsText { get; init; }
    public string? RangeSizeText { get; init; }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayTitle));
        }
    }

    private bool _isGroupEnd;
    public bool IsGroupEnd
    {
        get => _isGroupEnd;
        set
        {
            if (_isGroupEnd == value) return;
            _isGroupEnd = value;
            OnPropertyChanged();
        }
    }

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            OnPropertyChanged();
        }
    }

    private bool _isArmed;
    public bool IsArmed
    {
        get => _isArmed;
        set
        {
            if (_isArmed == value) return;
            _isArmed = value;
            OnPropertyChanged();
        }
    }

    private bool _isMultiSelected;
    public bool IsMultiSelected
    {
        get => _isMultiSelected;
        set
        {
            if (_isMultiSelected == value) return;
            _isMultiSelected = value;
            OnPropertyChanged();
        }
    }

    private DownloadRowVisual _visual;
    public DownloadRowVisual Visual
    {
        get => _visual;
        set
        {
            if (_visual == value) return;
            _visual = value;
            OnPropertyChanged();
        }
    }

    public string BaseTitle => string.IsNullOrWhiteSpace(Title) ? BundleId : Title;

    public string DisplayTitle => IsGroup
        ? $"{(IsExpanded ? "▾" : "▸")} {BaseTitle} ({GroupCount})"
        : BaseTitle;

    public string VersionText => IsGroup && !string.IsNullOrWhiteSpace(RangeVersionText)
        ? RangeVersionText
        : Version;

    public string MinOsText => IsGroup && !string.IsNullOrWhiteSpace(RangeMinOsText)
        ? RangeMinOsText
        : (string.IsNullOrWhiteSpace(MinOs) ? Strings.Common_Unknown : MinOs);

    public string DeviceFamilyText
    {
        get
        {
            var parts = new List<string>(3);
            if ((DeviceMask & 2) != 0) parts.Add("iPhone");
            if ((DeviceMask & 4) != 0) parts.Add("iPad");
            if ((DeviceMask & 8) != 0) parts.Add("AppleTV");
            return parts.Count == 0 ? Strings.Common_Unknown : string.Join(" / ", parts);
        }
    }

    public string SizeText => IsGroup && !string.IsNullOrWhiteSpace(RangeSizeText)
        ? RangeSizeText
        : FormatSize(Size, SizeExact);

    internal static string FormatSize(long size, bool sizeExact)
    {
        if (size <= 0) return Strings.Common_Unknown;
        var prefix = sizeExact ? "" : "≈";
        if (size >= 1024L * 1024 * 1024)
            return $"{prefix}{size / 1024.0 / 1024 / 1024:F2} GB";
        if (size >= 1024L * 1024)
            return $"{prefix}{size / 1024.0 / 1024:F1} MB";
        return $"{prefix}{size / 1024.0:F0} KB";
    }

    public string CategoryText => string.IsNullOrWhiteSpace(Subgenre)
        ? (string.IsNullOrWhiteSpace(Category) ? "" : Category)
        : $"{Category} / {Subgenre}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
