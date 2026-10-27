using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace TablePlsu.App.Features.Results;

/// <summary>How <see cref="BulkObservableCollection{T}.AddRange"/> notifies.</summary>
public enum BulkNotifyMode
{
    /// <summary>One <see cref="NotifyCollectionChangedAction.Reset"/> per AddRange. Simple, but WPF regenerates the visible rows.</summary>
    Reset,

    /// <summary>
    /// One Add notification per item, raised back to back after the whole range is in the list (WPF's CollectionView
    /// rejects multi-item Add). Appending below the viewport then costs almost nothing and keeps selection and scroll.
    /// </summary>
    ItemAdds,
}

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that appends batches (<see cref="AddRange"/>) or replaces everything
/// (<see cref="ReplaceAll"/>) with one burst of notifications. UI-thread only, like any collection bound to WPF.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    private static readonly PropertyChangedEventArgs CountChanged = new(nameof(Count));
    private static readonly PropertyChangedEventArgs IndexerChanged = new("Item[]");
    private static readonly NotifyCollectionChangedEventArgs ResetArgs = new(NotifyCollectionChangedAction.Reset);

    public BulkObservableCollection() { }

    public BulkObservableCollection(IEnumerable<T> items) : base(items) { }

    /// <summary>Notification mode for <see cref="AddRange"/>. Default <see cref="BulkNotifyMode.ItemAdds"/> (measured faster for streaming).</summary>
    public BulkNotifyMode Mode { get; set; } = BulkNotifyMode.ItemAdds;

    /// <summary>Above this many items in one AddRange, a single Reset is raised whatever <see cref="Mode"/> says.</summary>
    public int ItemAddsThreshold { get; set; } = 2000;

    /// <summary>Appends <paramref name="items"/> and raises the notifications once for the whole range.</summary>
    public void AddRange(IEnumerable<T> items)
    {
        CheckReentrancy();
        var start = Items.Count;
        if (Items is List<T> list) list.AddRange(items);
        else foreach (var item in items) Items.Add(item);
        var added = Items.Count - start;
        if (added == 0) return;

        OnPropertyChanged(CountChanged);
        OnPropertyChanged(IndexerChanged);
        if (Mode == BulkNotifyMode.Reset || added > ItemAddsThreshold || (start == 0 && added > 1))
        {
            OnCollectionChanged(ResetArgs);
            return;
        }
        for (var i = start; i < start + added; i++)
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, Items[i], i));
    }

    /// <summary>Replaces the whole content with one Reset.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        if (Items is List<T> list) list.AddRange(items);
        else foreach (var item in items) Items.Add(item);
        OnPropertyChanged(CountChanged);
        OnPropertyChanged(IndexerChanged);
        OnCollectionChanged(ResetArgs);
    }
}
