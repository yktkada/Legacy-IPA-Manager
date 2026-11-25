using System.Collections.ObjectModel;
using System.Globalization;
using IdeviceIndex.Resources;

namespace IdeviceIndex;

/// <summary>
/// 内存下载队列管理：按 <see cref="IndexRow.Id"/> 单行复用，并供 I2 下载引擎接入回调。
/// </summary>
internal sealed class DownloadQueueStore
{
    private readonly Dictionary<string, DownloadJobRow> _byId = new(StringComparer.Ordinal);
    private IReadOnlyList<DownloadQueueColumn> _visibleColumns = DownloadQueueColumns.OrderedVisible(DownloadQueueColumns.Default);

    public ObservableCollection<DownloadJobRow> Jobs { get; } = new();

    public bool IsSuspended { get; private set; }

    public Action<DownloadJobRow>? OnEnqueued { get; set; }
    public Action<DownloadJobRow>? OnCancelled { get; set; }
    public Action<bool>? OnSuspendChanged { get; set; }

    public (DownloadJobRow Row, bool Enqueued) Enqueue(IndexRow row, string? sourceSummary = null, string? targetPath = null)
    {
        if (_byId.TryGetValue(row.Id, out var existing))
        {
            if (IsActiveOrWaiting(existing.Status))
                return (existing, false);

            var reuseStatus = IsSuspended ? DownloadJobStatus.Paused : DownloadJobStatus.Pending;
            var reuseNote = IsSuspended ? Strings.Queue_Note_Suspended : Strings.Queue_Note_Waiting;
            existing.UpdateFrom(row, sourceSummary, targetPath, reuseNote, reuseStatus);
            existing.BumpGeneration();
            OnEnqueued?.Invoke(existing);
            return (existing, true);
        }

        var status = IsSuspended ? DownloadJobStatus.Paused : DownloadJobStatus.Pending;
        var note = IsSuspended ? Strings.Queue_Note_Suspended : Strings.Queue_Note_Waiting;
        var created = new DownloadJobRow(row, _visibleColumns, sourceSummary, targetPath, note)
        {
            Status = status,
        };
        created.BumpGeneration();
        _byId[row.Id] = created;
        Jobs.Add(created);
        OnEnqueued?.Invoke(created);
        return (created, true);
    }

    public (int Enqueued, int Skipped) EnqueueMany(IEnumerable<IndexRow> rows)
    {
        var enqueued = 0;
        var skipped = 0;
        foreach (var row in rows)
        {
            var sourceSummary = row.SourceCount > 0 ? row.SourceCount.ToString(CultureInfo.InvariantCulture) : null;
            var (_, added) = Enqueue(row, sourceSummary: sourceSummary);
            if (added) enqueued++;
            else skipped++;
        }
        return (enqueued, skipped);
    }

    public bool Cancel(string id)
    {
        if (!_byId.TryGetValue(id, out var row) || !row.CanCancel) return false;
        row.Percent = 0;
        row.SpeedText = "";
        row.Note = null;
        row.Status = DownloadJobStatus.Cancelled;
        OnCancelled?.Invoke(row);
        return true;
    }

    public bool ToggleSuspend()
    {
        IsSuspended = !IsSuspended;
        foreach (var job in Jobs)
        {
            if (IsSuspended)
            {
                if (job.Status is DownloadJobStatus.Pending or DownloadJobStatus.Downloading)
                {
                    job.Note = Strings.Queue_Note_Suspended;
                    job.Status = DownloadJobStatus.Paused;
                }
            }
            else if (job.Status == DownloadJobStatus.Paused)
            {
                job.Note = Strings.Queue_Note_Waiting;
                job.Status = DownloadJobStatus.Pending;
            }
        }
        OnSuspendChanged?.Invoke(IsSuspended);
        return IsSuspended;
    }

    public int ClearFinished()
    {
        var removed = 0;
        for (var i = Jobs.Count - 1; i >= 0; i--)
        {
            var job = Jobs[i];
            if (!IsFinished(job.Status)) continue;
            Jobs.RemoveAt(i);
            _byId.Remove(job.Id);
            removed++;
        }
        return removed;
    }

    public void ApplyColumns(IReadOnlyList<DownloadQueueColumnSetting> settings)
    {
        _visibleColumns = DownloadQueueColumns.OrderedVisible(settings);
        foreach (var job in Jobs)
            job.DisplayColumns = _visibleColumns;
    }

    public DownloadRowVisual GetVisual(string id)
        => _byId.TryGetValue(id, out var job) ? job.Visual : DownloadRowVisual.None;

    public void SyncRowVisuals(IEnumerable<IndexRow> rows)
    {
        foreach (var row in rows)
            row.Visual = GetVisual(row.Id);
    }

    public string FormatSummary()
    {
        var queued = 0;
        var active = 0;
        var done = 0;
        var failed = 0;
        foreach (var job in Jobs)
        {
            switch (job.Status)
            {
                case DownloadJobStatus.Pending or DownloadJobStatus.Paused:
                    queued++;
                    break;
                case DownloadJobStatus.Downloading or DownloadJobStatus.Verifying or DownloadJobStatus.Importing:
                    active++;
                    break;
                case DownloadJobStatus.Succeeded:
                    done++;
                    break;
                case DownloadJobStatus.Failed:
                    failed++;
                    break;
            }
        }
        return Strings.Format(nameof(Strings.Queue_SummaryFormat), queued, active, done, failed);
    }

    private static bool IsActiveOrWaiting(DownloadJobStatus status) => status is DownloadJobStatus.Pending
        or DownloadJobStatus.Downloading
        or DownloadJobStatus.Verifying
        or DownloadJobStatus.Importing
        or DownloadJobStatus.Paused;

    private static bool IsFinished(DownloadJobStatus status) => status is DownloadJobStatus.Succeeded
        or DownloadJobStatus.Failed
        or DownloadJobStatus.Cancelled;
}
