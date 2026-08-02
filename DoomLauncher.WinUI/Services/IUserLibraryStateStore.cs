namespace DoomLauncher.WinUI.Services;

public interface IUserLibraryStateStore
{
    Task<UserLibraryState> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(UserLibraryState state, CancellationToken cancellationToken = default);
}

public sealed record UserLibraryState(
    IReadOnlySet<int> FavoriteGameFileIds,
    IReadOnlySet<int> FinishedGameFileIds,
    string Theme,
    string Language,
    IReadOnlyList<string> VisibleColumns,
    IReadOnlyList<string> CollectionVisibleColumns,
    string ListDensity,
    string AccordionDensity,
    IReadOnlyList<string> LibraryFilterTags,
    IReadOnlySet<string> TestedThemes,
    int OriginalIwadLaunches,
    int ImportedCollectionCount,
    bool AchievementNotificationsInitialized,
    IReadOnlySet<string> NotifiedAchievementKeys,
    IReadOnlySet<string> UnseenAchievementKeys,
    IReadOnlySet<string> CollapsedCollectionNames,
    IReadOnlyDictionary<string, string> CollectionArtworkPaths,
    int? WindowWidth,
    int? WindowHeight)
{
    public static IReadOnlyList<string> DefaultVisibleColumns { get; } =
    [
        "Artwork",
        "Title",
        "Author",
        "ReleaseDate",
        "Maps",
        "Rating",
        "Downloaded",
        "SourcePort",
        "Playtime",
        "Finished",
        "Favorites",
    ];

    public static UserLibraryState Empty { get; } = new(
        new HashSet<int>(),
        new HashSet<int>(),
        "Dark",
        "en-US",
        DefaultVisibleColumns,
        DefaultVisibleColumns,
        "Normal",
        "Normal",
        [],
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Dark" },
        0,
        0,
        false,
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        null,
        null);
}
