using DoomLauncher.WinUI.Models;

namespace DoomLauncher.WinUI.Services;

public interface INativeLibraryService
{
    Task<GameEditData> LoadGameAsync(
        int gameFileId,
        CancellationToken cancellationToken = default);

    Task UpdateGameAsync(
        GameEditData game,
        CancellationToken cancellationToken = default);

    Task<LauncherSettingsData> LoadSettingsAsync(
        CancellationToken cancellationToken = default);

    Task UpdateSettingsAsync(
        LauncherSettingsData settings,
        CancellationToken cancellationToken = default);

    Task<NativeImportResult> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);

    Task<NativeImportResult> ImportAsync(
        string sourcePath,
        ImportFileConflictResolution conflictResolution,
        CancellationToken cancellationToken = default);

    Task<NativeImportConflict?> FindImportConflictAsync(
        string originalFileName,
        CancellationToken cancellationToken = default);

    Task<GameCollectionsData> LoadGameCollectionsAsync(
        int gameFileId,
        CancellationToken cancellationToken = default);

    Task SaveGameCollectionsAsync(
        int gameFileId,
        IReadOnlySet<int> tagIds,
        string? newCollectionName,
        CancellationToken cancellationToken = default);

    Task CreateCollectionAsync(
        string name,
        CancellationToken cancellationToken = default);

    Task DeleteCollectionAsync(
        int tagId,
        CancellationToken cancellationToken = default);

    Task SetGameFinishedAsync(
        int gameFileId,
        bool isFinished,
        CancellationToken cancellationToken = default);

    Task MigrateFinishedStateAsync(
        IReadOnlySet<int> gameFileIds,
        CancellationToken cancellationToken = default);

    Task<NativeImportResult> ImportIdGamesAsync(
        IdGamesItem item,
        string downloadedPath,
        CancellationToken cancellationToken = default);

    Task<NativeImportResult> ImportIdGamesAsync(
        IdGamesItem item,
        string downloadedPath,
        ImportFileConflictResolution conflictResolution,
        CancellationToken cancellationToken = default);

    Task UpdateGameFromIdGamesAsync(
        int gameFileId,
        IdGamesItem item,
        CancellationToken cancellationToken = default);

    Task<bool> TryImportTitlePicAsync(
        int gameFileId,
        string archivePath,
        CancellationToken cancellationToken = default);

    Task<int> CleanupDerivedThumbnailsAsync(
        CancellationToken cancellationToken = default);

    Task<GameMediaData> LoadGameMediaAsync(
        int gameFileId,
        CancellationToken cancellationToken = default);

    Task SetTitleArtworkAsync(
        int gameFileId,
        string sourcePath,
        CancellationToken cancellationToken = default);

    Task RemoveTitleArtworkAsync(
        int gameFileId,
        CancellationToken cancellationToken = default);

    Task AddScreenshotsAsync(
        int gameFileId,
        IReadOnlyList<string> sourcePaths,
        CancellationToken cancellationToken = default);

    Task RemoveScreenshotAsync(
        int gameFileId,
        int screenshotFileId,
        CancellationToken cancellationToken = default);

    Task SetScreenshotOrderAsync(
        int gameFileId,
        IReadOnlyList<int> screenshotFileIds,
        CancellationToken cancellationToken = default);

    Task SetScreenshotAsTitleArtworkAsync(
        int gameFileId,
        int screenshotFileId,
        CancellationToken cancellationToken = default);

    Task<string?> ResolveManagedGameFileAsync(
        int gameFileId,
        CancellationToken cancellationToken = default);

    Task<LauncherDefinitionsData> LoadLauncherDefinitionsAsync(
        CancellationToken cancellationToken = default);

    Task SaveSourcePortAsync(
        NativeSourcePortDefinition definition,
        CancellationToken cancellationToken = default);

    Task DeleteSourcePortAsync(
        int sourcePortId,
        bool deletePhysicalFiles = false,
        CancellationToken cancellationToken = default);

    Task SaveIwadAsync(
        NativeIwadDefinition definition,
        CancellationToken cancellationToken = default);

    Task DeleteIwadAsync(
        int iwadId,
        bool deletePhysicalFiles = false,
        CancellationToken cancellationToken = default);

    Task DeleteGameAsync(
        int gameFileId,
        bool deletePhysicalFiles = false,
        CancellationToken cancellationToken = default);

    Task<IwadVersionDetection> DetectIwadVersionAsync(
        string archiveFileName,
        string internalFileName,
        CancellationToken cancellationToken = default);

    Task<LibraryStatisticsData> LoadStatisticsAsync(
        CancellationToken cancellationToken = default);

    Task<int> BackfillMapMetadataAsync(
        CancellationToken cancellationToken = default);

    Task<DatabaseHealthReport> CheckDatabaseHealthAsync(
        bool repair,
        CancellationToken cancellationToken = default);

    Task<DuplicateConsolidationResult> ConsolidateGeneratedNameDuplicatesAsync(
        CancellationToken cancellationToken = default);

    Task ExportPortableBundleAsync(
        IReadOnlyCollection<int> gameFileIds,
        string destinationPath,
        PortableBundleExportOptions options,
        CancellationToken cancellationToken = default);

    Task<PortableBundleInspection> InspectPortableBundleAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);

    Task<PortableBundleImportResult> ImportPortableBundleAsync(
        string sourcePath,
        PortableBundleImportOptions options,
        CancellationToken cancellationToken = default);
}

public sealed record GameEditData(
    int GameFileId,
    string FileName,
    string Title,
    string Author,
    string Description,
    int? SourcePortId,
    int? IwadId,
    IReadOnlyList<NativeChoice> SourcePorts,
    IReadOnlyList<NativeChoice> Iwads);

public sealed record LauncherSettingsData(
    string GameFileDirectory,
    int? DefaultSourcePortId,
    int? DefaultIwadId,
    bool ShowPlayDialog,
    bool ImportScreenshots,
    int ItemsPerPage,
    int HomeItemsPerGroup,
    string ColorTheme,
    string PlaceholderArtworkStyle,
    IReadOnlyList<NativeChoice> SourcePorts,
    IReadOnlyList<NativeChoice> Iwads);

public sealed record NativeChoice(int? Id, string Name);

public sealed record NativeImportResult(
    int GameFileId,
    string FileName,
    string DestinationPath,
    bool WasSkipped = false,
    bool ReusedExisting = false);

public enum ImportFileConflictResolution
{
    Fail,
    Skip,
    Overwrite,
}

public sealed record NativeImportConflict(
    string OriginalFileName,
    int? ExistingGameFileId,
    bool PhysicalFileExists);

public sealed record NativeTag(int TagId, string Name);

public sealed record GameCollectionsData(
    IReadOnlyList<NativeTag> Collections,
    IReadOnlySet<int> SelectedTagIds);

public sealed record NativeMediaFile(
    int FileId,
    string FileName,
    string FullPath,
    int FileOrder);

public sealed record GameMediaData(
    NativeMediaFile? TitleArtwork,
    IReadOnlyList<NativeMediaFile> Screenshots);

public sealed record LauncherDefinitionsData(
    IReadOnlyList<NativeSourcePortDefinition> SourcePorts,
    IReadOnlyList<NativeIwadDefinition> Iwads);

public sealed record NativeSourcePortDefinition(
    int? SourcePortId,
    string Name,
    string Directory,
    string Executable,
    string SupportedExtensions,
    string FileOption,
    string ExtraParameters,
    string Version = "",
    string ScreenshotSupport = "Auto",
    string ScreenshotDirectories = "",
    string ScreenshotExtensions = ".png,.jpg,.jpeg,.bmp",
    string ScreenshotArgument = "",
    string StatisticsAdapter = "None",
    string StatisticsDirectories = "",
    string SaveGameExtensions = ".zds")
{
    public string DisplayLabel => string.IsNullOrWhiteSpace(Version)
        ? Name
        : $"{Name} · {Version}";
    public string VersionSuffix => string.IsNullOrWhiteSpace(Version)
        ? string.Empty
        : $" · {Version}";
}

public sealed record NativeIwadDefinition(
    int? IwadId,
    string Name,
    string ArchiveFileName,
    string InternalFileName,
    string Version = "",
    string Md5 = "",
    long FileSize = 0,
    string CatalogLabel = "")
{
    public string DisplayLabel => string.IsNullOrWhiteSpace(Version)
        ? Name
        : $"{Name} · {Version}";
}

public sealed record IwadVersionDetection(
    bool IsKnown,
    string Version,
    string Edition,
    string Md5,
    long FileSize,
    string CatalogLabel)
{
    public string DisplayVersion => string.IsNullOrWhiteSpace(Edition)
        ? Version
        : $"{Version} · {Edition}";
}

public sealed record IwadLibraryStatistic(
    string Iwad,
    int Entries,
    int Maps);

public sealed record SessionStatisticsData(
    int Maps,
    int Kills,
    int TotalKills,
    int Secrets,
    int TotalSecrets,
    int Items,
    int TotalItems,
    int DistinctSkills);

public sealed record LibraryStatisticsData(
    int Played,
    int Unplayed,
    int Finished,
    int TotalMinutesPlayed,
    int IdGamesDownloads,
    IReadOnlyList<IwadLibraryStatistic> ByIwad,
    SessionStatisticsData Sessions);

public sealed record DatabaseHealthReport(
    bool IsHealthy,
    bool WasRepaired,
    string IntegrityResult,
    int OrphanedFileRows,
    int OrphanedTagMappings,
    int MissingManagedFiles,
    string BackupPath,
    IReadOnlyList<string> Messages);

public sealed record DuplicateConsolidationResult(
    int RemovedEntries,
    int RenamedEntries,
    IReadOnlyDictionary<int, int> RemovedToKeptGameFileIds,
    IReadOnlyList<string> RemovedFileNames,
    IReadOnlyList<string> RenamedFileNames);

public sealed record PortableBundleExportOptions(
    bool IncludeGeneralMetadata,
    bool IncludePersonalMetadata,
    bool IncludeScreenshots,
    bool IncludeTitleArtwork,
    bool IncludeCollections,
    IReadOnlySet<int> FavoriteGameFileIds,
    IReadOnlyDictionary<string, string> CollectionArtworkPaths,
    IReadOnlySet<string> LibraryFilterCollections);

public sealed record PortableBundleImportOptions(
    bool IncludeGeneralMetadata,
    bool IncludePersonalMetadata,
    bool IncludeScreenshots,
    bool IncludeTitleArtwork,
    bool IncludeCollections,
    IReadOnlyDictionary<string, ImportFileConflictResolution>? ConflictResolutions = null);

public sealed record PortableBundleInspection(
    int FormatVersion,
    int EntryCount,
    bool ContainsGeneralMetadata,
    bool ContainsPersonalMetadata,
    bool ContainsScreenshots,
    bool ContainsTitleArtwork,
    bool ContainsCollections,
    IReadOnlyList<PortableBundleEntryInspection> Entries);

public sealed record PortableBundleEntryInspection(
    string FileName,
    string Title,
    NativeImportConflict? Conflict);

public sealed record PortableBundleImportResult(
    int ImportedEntries,
    int ImportedMediaFiles,
    IReadOnlyList<string> Collections,
    IReadOnlySet<int> FavoriteGameFileIds,
    IReadOnlyDictionary<string, string> CollectionArtworkPaths,
    IReadOnlySet<string> LibraryFilterCollections);

public interface IFirstSetupService
{
    Task<string> EnsureDatabaseAsync(
        CancellationToken cancellationToken = default);

    Task<ManagedLayoutMigrationResult> EnsureManagedLayoutAsync(
        CancellationToken cancellationToken = default);

    Task<bool> ShouldRunWizardAsync(
        CancellationToken cancellationToken = default);

    Task CompleteWizardAsync(
        CancellationToken cancellationToken = default);

    Task<SetupScanResult> ScanIwadsAsync(
        CancellationToken cancellationToken = default);

    Task<SetupScanResult> ScanIwadsAsync(
        CancellationToken cancellationToken,
        IProgress<double>? progress);

    Task<SetupScanResult> ScanSourcePortsAsync(
        CancellationToken cancellationToken = default);

    Task<SetupScanResult> ScanSourcePortsAsync(
        CancellationToken cancellationToken,
        IProgress<double>? progress);

    Task<SetupScanResult> ScanModsAsync(
        CancellationToken cancellationToken = default);

    Task<SetupScanResult> ScanModsAsync(
        CancellationToken cancellationToken,
        IProgress<double>? progress);

    Task<IReadOnlyList<IwadInModsPrompt>> FindIwadsInModsAsync(
        CancellationToken cancellationToken,
        IProgress<double>? progress = null);

    Task<SetupScanResult> ScanModsAsync(
        CancellationToken cancellationToken,
        IProgress<double>? progress,
        IReadOnlyDictionary<string, IwadInModsAction>? iwadDecisions);
}

public enum IwadInModsAction
{
    KeepAsMod,
    MoveAndRegister,
}

public sealed record IwadInModsPrompt(
    string FilePath,
    string FileName,
    IReadOnlyList<string> DetectedIwads);

public sealed record ManagedLayoutMigrationResult(
    int UpdatedReferences,
    int MovedFiles);

public sealed record SetupScanResult(
    int Discovered,
    int Imported,
    int Updated,
    int Removed,
    int Skipped,
    IReadOnlyList<string> RemovedItems,
    IReadOnlyList<string> Warnings);

internal sealed record IwadArchiveCandidate(
    string InternalFileName,
    string SuggestedName,
    string Version,
    string Md5,
    long FileSize,
    string CatalogLabel);
