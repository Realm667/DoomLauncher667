using System.Collections.ObjectModel;

namespace DoomLauncher.WinUI.Services;

internal static class CollectionReconciler
{
    // Keep the ItemsSource stable and retain containers/selection for unchanged items.
    public static void Apply<T>(ObservableCollection<T> target, IReadOnlyList<T> desired) where T : class
    {
        var wanted = desired.ToHashSet(ReferenceEqualityComparer.Instance);
        for (var i = target.Count - 1; i >= 0; i--)
            if (!wanted.Contains(target[i])) target.RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i])) continue;
            var oldIndex = target.IndexOf(desired[i]);
            if (oldIndex >= 0) target.Move(oldIndex, i);
            else target.Insert(i, desired[i]);
        }
    }
}
