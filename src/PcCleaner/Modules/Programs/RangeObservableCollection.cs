using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace PcCleaner.Modules.Programs;

/// <summary>
/// ObservableCollection that can replace all items with a single Reset notification.
/// Keeping one collection (instead of assigning a new one) preserves the DataGrid's sorting.
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
