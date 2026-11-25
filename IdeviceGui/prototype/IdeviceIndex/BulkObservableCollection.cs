using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace IdeviceIndex;

/// <summary>
/// 支持整表一次性替换（仅发一次 Reset 通知）的观察集合，避免 20 万行逐条 Add 阻塞 UI 线程；
/// 同时保留 Insert/Remove 能力供组行局部展开/收起快路径复用。
/// </summary>
internal sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotification;

    public void ReplaceAll(IReadOnlyList<T> items)
    {
        CheckReentrancy();
        _suppressNotification = true;
        try
        {
            Items.Clear();
            if (Items is List<T> backingList) backingList.EnsureCapacity(items.Count);
            for (var i = 0; i < items.Count; i++) Items.Add(items[i]);
        }
        finally
        {
            _suppressNotification = false;
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotification) base.OnCollectionChanged(e);
    }
}
