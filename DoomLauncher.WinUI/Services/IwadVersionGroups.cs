namespace DoomLauncher.WinUI.Services;

// Presentation only. Identity and chronological rank come from the verified catalog,
// never from title similarity, filenames or a numerical version comparison.
internal static class IwadVersionGroups
{
    internal static bool Eligible(LibraryCatalogEntry entry) => entry.Category == "IWAD"
        && entry.VersionGroup.Length > 0 && entry.ReleaseOrder is > 0;

    internal static IReadOnlyDictionary<string, LibraryCatalogEntry[]> Build(IEnumerable<LibraryCatalogEntry> entries)
        => entries.Where(Eligible).GroupBy(e => e.VersionGroup, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).ToDictionary(g => g.Key,
                g => g.OrderByDescending(e => e.ReleaseOrder).ThenBy(e => e.GameFileId).ToArray(), StringComparer.Ordinal);
}
