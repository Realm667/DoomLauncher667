using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DoomLauncher.WinUI.Services;

internal sealed class ReconciledCollection<T> : ObservableCollection<T> where T : class
{
    public event Action? Updating;
    public event Action? Updated;

    internal void BeginUpdate() => Updating?.Invoke();
    internal void EndUpdate() => Updated?.Invoke();

    internal void ReplaceAll(IReadOnlyList<T> desired)
    {
        CheckReentrancy();
        BeginUpdate();
        Items.Clear();
        foreach (var item in desired) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        EndUpdate();
    }
}
