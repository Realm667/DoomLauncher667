namespace DoomLauncher.WinUI.Services;

internal static class FeaturedModSelector
{
    public static int? Choose(IReadOnlyList<int> favorites, IReadOnlyList<int> mods,
        int? previous, bool rotate, Random? random = null)
    {
        var candidates = (favorites.Count > 0 ? favorites : mods).Distinct().ToArray();
        if (candidates.Length == 0) return null;
        if (!rotate && previous.HasValue && candidates.Contains(previous.Value)) return previous;
        var eligible = candidates.Length > 1
            ? candidates.Where(id => id != previous).ToArray() : candidates;
        return eligible[(random ?? Random.Shared).Next(eligible.Length)];
    }
}
