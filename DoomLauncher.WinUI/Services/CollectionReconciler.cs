using System.Collections.ObjectModel;

namespace DoomLauncher.WinUI.Services;

internal static class CollectionReconciler
{
    // Keep the ItemsSource stable and retain containers/selection for unchanged items.
    public static void Apply<T>(ObservableCollection<T> target, IReadOnlyList<T> desired) where T : class
    {
        // WinUI processes every Move/Insert synchronously, even for unrealized
        // rows. Publish large changes once; retain incremental containers for
        // small edits and emit nothing at all for unchanged views.
        if (target.Count == desired.Count && target.Zip(desired).All(pair => ReferenceEquals(pair.First, pair.Second))) return;
        var wanted = desired.ToHashSet(ReferenceEqualityComparer.Instance);
        var present = target.ToHashSet(ReferenceEqualityComparer.Instance);
        var changed = target.Count(item => !wanted.Contains(item)) + desired.Count(item => !present.Contains(item));
        // Compare surviving items independently of insertions/removals so a
        // single edit near the front does not reset thousands of containers.
        var next = 0;
        foreach (var item in target)
        {
            if (changed > 128) break;
            if (!wanted.Contains(item)) continue;
            while (next < desired.Count && !present.Contains(desired[next])) next++;
            if (next < desired.Count && !ReferenceEquals(item, desired[next++])) changed++;
        }
        if (changed > 128 && target is ReconciledCollection<T> batched)
        {
            batched.ReplaceAll(desired);
            return;
        }
        var updating = target as ReconciledCollection<T>;
        updating?.BeginUpdate();
        try
        {
            for (var i = target.Count - 1; i >= 0; i--)
                if (!wanted.Contains(target[i])) target.RemoveAt(i);
            for (var i = 0; i < desired.Count; i++)
            {
                if (i < target.Count && ReferenceEquals(target[i], desired[i])) continue;
                var oldIndex = -1;
                if (present.Contains(desired[i]))
                    for (var j = i; j < target.Count; j++)
                        if (ReferenceEquals(target[j], desired[i])) { oldIndex = j; break; }
                if (oldIndex >= 0) target.Move(oldIndex, i);
                else { target.Insert(i, desired[i]); present.Add(desired[i]); }
            }
        }
        finally { updating?.EndUpdate(); }
    }
}
