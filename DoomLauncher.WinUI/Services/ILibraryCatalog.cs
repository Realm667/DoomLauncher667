namespace DoomLauncher.WinUI.Services;

public interface ILibraryCatalog
{
    Task<LibraryCatalogResult> LoadAsync(CancellationToken cancellationToken = default);
}

public sealed record LibraryCatalogResult(
    IReadOnlyList<LibraryCatalogEntry> Entries,
    string Source,
    int PageSize,
    int HomeItemsPerGroup,
    IReadOnlySet<string> ConfiguredIwads,
    IReadOnlySet<string> CollectionNames);

public sealed record LibraryCatalogEntry(
    int GameFileId,
    string FileName,
    string Title,
    string Author,
    string Category,
    string Year,
    string ReleaseDate,
    string Maps,
    string Rating,
    string Downloaded,
    string ArtworkPath,
    string DetailArtworkPath,
    bool UsesDoomPixelAspect,
    IReadOnlyList<string> ScreenshotPaths,
    string Description,
    string SourcePort,
    string Iwad,
    string Playtime,
    string LastPlayed,
    int MinutesPlayed,
    DateTime? LastPlayedAt,
    DateTime? ReleaseDateAt,
    DateTime? DownloadedAt,
    int MapCount,
    double RatingValue,
    int? IdGamesId,
    bool IsFinished,
    bool IsIdGamesDownload,
    bool IsDownloaded,
    IReadOnlyList<string> Tags);
