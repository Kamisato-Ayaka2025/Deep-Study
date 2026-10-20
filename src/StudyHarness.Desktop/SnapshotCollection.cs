using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace StudyHarness.Desktop;

// Publish one coherent UI snapshot; observers never see a partially rebuilt graph.
public sealed class SnapshotCollection<T> : ObservableCollection<T>
{
    public void Replace(IEnumerable<T> values)
    {
        var snapshot = values.ToArray();
        if (this.SequenceEqual(snapshot)) return;
        CheckReentrancy();
        Items.Clear();
        foreach (var value in snapshot) Items.Add(value);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
