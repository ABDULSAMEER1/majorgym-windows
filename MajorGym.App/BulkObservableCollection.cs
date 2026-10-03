using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace MajorGym.App;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can swap its whole contents with ONE change notification.
/// The old pattern (Clear() then Add() per row) raised a notification for every single row, and each one made
/// the list control re-measure and re-layout - the main reason typing in a search box stuttered on long lists.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Replaces everything with <paramref name="items"/> and raises a single Reset.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
