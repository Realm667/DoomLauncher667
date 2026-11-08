using System.Globalization;
using System.Drawing;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DoomLauncher.WinUI.Models;
using Microsoft.Data.Sqlite;

namespace DoomLauncher.WinUI.Services;

public sealed partial class SqliteNativeLibraryService
{
    public async Task ExportPortableBundleAsync(
        IReadOnlyCollection<int> gameFileIds,
        string destinationPath,
        PortableBundleExportOptions options,
        CancellationToken cancellationToken = default)
    {
        if (gameFileIds.Count == 0)
            throw new ArgumentException("Select at least one library entry.", nameof(gameFileIds));
        destinationPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (File.Exists(destinationPath))
            File.Delete(destinationPath);

        var databasePath = databaseLocator.FindDatabase();
        var entries = new List<PortableEntry>();
        var exportedCollectionNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var archive = ZipFile.Open(destinationPath, ZipArchiveMode.Create);
        var ordinal = 0;
        foreach (var gameFileId in gameFileIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var game = await LoadGameAsync(gameFileId, cancellationToken);
            var media = await LoadGameMediaAsync(gameFileId, cancellationToken);
            var collections = await LoadGameCollectionsAsync(gameFileId, cancellationToken);
            var gamePath = await ResolveManagedGameFileAsync(gameFileId, cancellationToken);
            if (string.IsNullOrWhiteSpace(gamePath) || !File.Exists(gamePath))
                continue;

            var key = $"entries/{++ordinal:D4}";
            var gameEntry = $"{key}/game/{Path.GetFileName(gamePath)}";
            await AddFileToArchiveAsync(
                archive,
                gamePath,
                gameEntry,
                cancellationToken);
            string? artworkEntry = null;
            if (options.IncludeTitleArtwork && media.TitleArtwork is not null)
            {
                artworkEntry = $"{key}/artwork/{media.TitleArtwork.FileName}";
                await AddFileToArchiveAsync(
                    archive,
                    media.TitleArtwork.FullPath,
                    artworkEntry,
                    cancellationToken);
            }
            var screenshotEntries = new List<string>();
            foreach (var screenshot in options.IncludeScreenshots
                         ? media.Screenshots
                         : [])
            {
                var path = $"{key}/screenshots/{screenshot.FileName}";
                await AddFileToArchiveAsync(
                    archive,
                    screenshot.FullPath,
                    path,
                    cancellationToken);
                screenshotEntries.Add(path);
            }
            var selectedCollections = options.IncludeCollections
                ? collections.Collections
                    .Where(item => collections.SelectedTagIds.Contains(item.TagId))
                    .Select(item => item.Name)
                    .ToArray()
                : [];
            exportedCollectionNames.UnionWith(selectedCollections);
            var metadata = await LoadPortableMetadataAsync(
                gameFileId,
                cancellationToken);
            entries.Add(new PortableEntry(
                gameEntry,
                artworkEntry,
                screenshotEntries,
                options.IncludeGeneralMetadata
                    ? game.Title
                    : Path.GetFileNameWithoutExtension(gamePath),
                options.IncludeGeneralMetadata ? game.Author : string.Empty,
                options.IncludeGeneralMetadata ? game.Description : string.Empty,
                options.IncludeGeneralMetadata
                    ? game.SourcePorts.FirstOrDefault(item => item.Id == game.SourcePortId)?.Name
                    : null,
                options.IncludeGeneralMetadata
                    ? game.Iwads.FirstOrDefault(item => item.Id == game.IwadId)?.Name
                    : null,
                options.IncludeGeneralMetadata ? metadata.ReleaseDate : null,
                options.IncludeGeneralMetadata ? metadata.Map : null,
                options.IncludeGeneralMetadata ? metadata.MapCount : 0,
                options.IncludeGeneralMetadata ? metadata.Rating : 0,
                options.IncludePersonalMetadata ? metadata.MinutesPlayed : 0,
                options.IncludePersonalMetadata ? metadata.LastPlayed : null,
                options.IncludePersonalMetadata && metadata.IsFinished,
                options.IncludeGeneralMetadata ? metadata.IdGamesId : null,
                options.IncludeGeneralMetadata ? metadata.IdGamesDirectory : null,
                selectedCollections,
                options.IncludePersonalMetadata
                && options.FavoriteGameFileIds.Contains(gameFileId)));
        }
        if (entries.Count == 0)
            throw new InvalidOperationException("None of the selected entries has a managed mod file.");

        var portableCollections = new List<PortableCollection>();
        var collectionOrdinal = 0;
        foreach (var collectionName in exportedCollectionNames
                     .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            string? artworkEntry = null;
            if (options.CollectionArtworkPaths.TryGetValue(
                    collectionName,
                    out var artworkReference)
                && !string.IsNullOrWhiteSpace(artworkReference))
            {
                var artworkPath = ResolveStoredPath(
                    databasePath,
                    artworkReference);
                if (File.Exists(artworkPath))
                {
                    artworkEntry =
                        $"collections/{++collectionOrdinal:D4}/artwork/" +
                        Path.GetFileName(artworkPath);
                    await AddFileToArchiveAsync(
                        archive,
                        artworkPath,
                        artworkEntry,
                        cancellationToken);
                }
            }
            portableCollections.Add(new PortableCollection(
                collectionName,
                artworkEntry,
                options.LibraryFilterCollections.Contains(collectionName)));
        }

        var manifest = new PortableManifest(
            3,
            "Doom Launcher 667",
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            options.IncludePersonalMetadata,
            options.IncludeGeneralMetadata,
            options.IncludeScreenshots,
            options.IncludeTitleArtwork,
            options.IncludeCollections,
            entries,
            portableCollections);
        var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        await using var manifestStream = manifestEntry.Open();
        await JsonSerializer.SerializeAsync(
            manifestStream,
            manifest,
            new JsonSerializerOptions { WriteIndented = true },
            cancellationToken);
    }

    public async Task<PortableBundleInspection> InspectPortableBundleAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The portable Doom Launcher bundle was not found.", sourcePath);
        using var archive = ZipFile.OpenRead(sourcePath);
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("The bundle does not contain manifest.json.");
        PortableManifest manifest;
        await using (var stream = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<PortableManifest>(
                stream,
                cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("The bundle manifest is invalid.");
        }
        if (manifest.FormatVersion is not 1 and not 2 and not 3)
            throw new InvalidDataException(
                $"Bundle format {manifest.FormatVersion} is not supported.");
        var entries = new List<PortableBundleEntryInspection>();
        foreach (var item in manifest.Entries ?? [])
        {
            var fileName = Path.GetFileName(item.GameArchive);
            entries.Add(new PortableBundleEntryInspection(
                fileName,
                item.Title,
                await FindImportConflictAsync(fileName, cancellationToken)));
        }
        return new PortableBundleInspection(
            manifest.FormatVersion,
            entries.Count,
            manifest.FormatVersion < 3 || manifest.ContainsGeneralMetadata,
            manifest.ContainsPersonalMetadata,
            (manifest.FormatVersion < 3 || manifest.ContainsScreenshots)
                && (manifest.Entries ?? []).Any(item => (item.Screenshots?.Count ?? 0) > 0),
            (manifest.FormatVersion < 3 || manifest.ContainsTitleArtwork)
                && (manifest.Entries ?? []).Any(item => !string.IsNullOrWhiteSpace(item.TitleArtwork)),
            (manifest.FormatVersion < 3 || manifest.ContainsCollections)
                && ((manifest.Collections?.Count ?? 0) > 0
                    || (manifest.Entries ?? []).Any(item => (item.Collections?.Count ?? 0) > 0)),
            entries);
    }

    public async Task<PortableBundleImportResult> ImportPortableBundleAsync(
        string sourcePath,
        PortableBundleImportOptions options,
        CancellationToken cancellationToken = default)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The portable Doom Launcher bundle was not found.", sourcePath);
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"DoomLauncher667-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var importedEntries = 0;
        var importedMedia = 0;
        var importedCollections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var importedFavorites = new HashSet<int>();
        var importedCollectionArtwork =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var importedLibraryFilters =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var archive = ZipFile.OpenRead(sourcePath);
            var manifestArchiveEntry = archive.GetEntry("manifest.json")
                ?? throw new InvalidDataException("The bundle does not contain manifest.json.");
            PortableManifest manifest;
            await using (var stream = manifestArchiveEntry.Open())
            {
                manifest = await JsonSerializer.DeserializeAsync<PortableManifest>(
                    stream,
                    cancellationToken: cancellationToken)
                    ?? throw new InvalidDataException("The bundle manifest is invalid.");
            }
            if (manifest.FormatVersion is not 1 and not 2 and not 3)
                throw new InvalidDataException(
                    $"Bundle format {manifest.FormatVersion} is not supported.");
            var includeGeneralMetadata = options.IncludeGeneralMetadata
                && (manifest.FormatVersion < 3 || manifest.ContainsGeneralMetadata);
            var includePersonalMetadata = options.IncludePersonalMetadata
                && (manifest.FormatVersion == 1 || manifest.ContainsPersonalMetadata);
            var includeScreenshots = options.IncludeScreenshots
                && (manifest.FormatVersion < 3 || manifest.ContainsScreenshots);
            var includeTitleArtwork = options.IncludeTitleArtwork
                && (manifest.FormatVersion < 3 || manifest.ContainsTitleArtwork);
            var includeCollections = options.IncludeCollections
                && (manifest.FormatVersion < 3 || manifest.ContainsCollections);

            foreach (var entry in manifest.Entries ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                var gamePath = await ExtractBundleEntryAsync(
                    archive,
                    entry.GameArchive,
                    temporaryDirectory,
                    cancellationToken);
                var originalFileName = Path.GetFileName(entry.GameArchive);
                var resolution = options.ConflictResolutions is not null
                    && options.ConflictResolutions.TryGetValue(
                        originalFileName,
                        out var selectedResolution)
                        ? selectedResolution
                        : ImportFileConflictResolution.Fail;
                var imported = await ImportAsync(
                    gamePath,
                    resolution,
                    cancellationToken);
                if (imported.WasSkipped)
                    continue;
                var importedGame = await LoadGameAsync(
                    imported.GameFileId,
                    cancellationToken);
                var sourcePortId = importedGame.SourcePorts.FirstOrDefault(item =>
                    string.Equals(item.Name, entry.SourcePort, StringComparison.OrdinalIgnoreCase))?.Id;
                var iwadId = importedGame.Iwads.FirstOrDefault(item =>
                    string.Equals(item.Name, entry.Iwad, StringComparison.OrdinalIgnoreCase))?.Id;
                if (includeGeneralMetadata)
                {
                    await UpdateGameAsync(
                        importedGame with
                        {
                            Title = entry.Title,
                            Author = entry.Author,
                            Description = entry.Description,
                            SourcePortId = sourcePortId,
                            IwadId = iwadId,
                        },
                        cancellationToken);
                }
                await ApplyPortableMetadataAsync(
                    imported.GameFileId,
                    entry,
                    includeGeneralMetadata,
                    includePersonalMetadata,
                    cancellationToken);
                if (includePersonalMetadata)
                {
                    await SetGameFinishedAsync(
                        imported.GameFileId,
                        entry.IsFinished,
                        cancellationToken);
                    if (entry.IsFavorite)
                        importedFavorites.Add(imported.GameFileId);
                }

                if (includeCollections)
                {
                    var collectionData = await LoadGameCollectionsAsync(
                        imported.GameFileId,
                        cancellationToken);
                    var selectedIds = new HashSet<int>(
                        collectionData.SelectedTagIds);
                    foreach (var collectionName in (entry.Collections ?? [])
                                 .Where(name => !string.IsNullOrWhiteSpace(name))
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        var existing = collectionData.Collections.FirstOrDefault(item =>
                            string.Equals(
                                item.Name,
                                collectionName,
                                StringComparison.OrdinalIgnoreCase));
                        if (existing is null)
                        {
                            await SaveGameCollectionsAsync(
                                imported.GameFileId,
                                selectedIds,
                                collectionName,
                                cancellationToken);
                            collectionData = await LoadGameCollectionsAsync(
                                imported.GameFileId,
                                cancellationToken);
                            existing = collectionData.Collections.First(item =>
                                string.Equals(
                                    item.Name,
                                    collectionName,
                                    StringComparison.OrdinalIgnoreCase));
                        }
                        selectedIds.Add(existing.TagId);
                        importedCollections.Add(existing.Name);
                    }
                    await SaveGameCollectionsAsync(
                        imported.GameFileId,
                        selectedIds,
                        null,
                        cancellationToken);
                }

                if (includeTitleArtwork
                    && !string.IsNullOrWhiteSpace(entry.TitleArtwork))
                {
                    var artworkPath = await ExtractBundleEntryAsync(
                        archive,
                        entry.TitleArtwork,
                        temporaryDirectory,
                        cancellationToken);
                    await SetTitleArtworkAsync(
                        imported.GameFileId,
                        artworkPath,
                        cancellationToken);
                    importedMedia++;
                }
                var screenshotPaths = new List<string>();
                if (includeScreenshots)
                {
                    foreach (var screenshot in entry.Screenshots ?? [])
                    {
                        screenshotPaths.Add(await ExtractBundleEntryAsync(
                            archive,
                            screenshot,
                            temporaryDirectory,
                            cancellationToken));
                    }
                }
                if (screenshotPaths.Count > 0)
                {
                    await AddScreenshotsAsync(
                        imported.GameFileId,
                        screenshotPaths,
                        cancellationToken);
                    importedMedia += screenshotPaths.Count;
                }
                importedEntries++;
            }

            if (includeCollections)
            {
                var databasePath = databaseLocator.FindDatabase();
                foreach (var collection in manifest.Collections ?? [])
                {
                    if (string.IsNullOrWhiteSpace(collection.Name))
                        continue;
                    if (collection.ShowAsLibraryFilter)
                        importedLibraryFilters.Add(collection.Name);
                    if (string.IsNullOrWhiteSpace(collection.Artwork))
                        continue;
                    var artworkPath = await ExtractBundleEntryAsync(
                        archive,
                        collection.Artwork,
                        temporaryDirectory,
                        cancellationToken);
                    importedCollectionArtwork[collection.Name] =
                        StoreImportedCollectionArtwork(
                            databasePath,
                            collection.Name,
                            artworkPath);
                    importedMedia++;
                }
            }
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, recursive: true);
        }
        return new PortableBundleImportResult(
            importedEntries,
            importedMedia,
            importedCollections
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            importedFavorites,
            importedCollectionArtwork,
            importedLibraryFilters);
    }

    private async Task<PortableMetadata> LoadPortableMetadataAsync(
        int gameFileId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT g.ReleaseDate, g.Map, COALESCE(g.MapCount, 0),
                   COALESCE(g.Rating, 0), COALESCE(g.MinutesPlayed, 0),
                   g.LastPlayed, COALESCE(s.Finished, 0),
                   COALESCE(m.IdGamesID, d.IdGamesID),
                   COALESCE(m.ArchiveDirectory, d.ArchiveDirectory)
            FROM GameFiles g
            LEFT JOIN WinUI_GameState s ON s.GameFileID=g.GameFileID
            LEFT JOIN WinUI_IdGamesMetadata m ON m.GameFileID=g.GameFileID
            LEFT JOIN WinUI_IdGamesDownloads d ON d.GameFileID=g.GameFileID
            WHERE g.GameFileID=$id;
            """;
        command.Parameters.AddWithValue("$id", gameFileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException($"GameFileID {gameFileId} was not found.");
        return new PortableMetadata(
            GetNullableString(reader, 0),
            GetNullableString(reader, 1),
            reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            reader.IsDBNull(3) ? 0 : reader.GetDouble(3),
            reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            GetNullableString(reader, 5),
            !reader.IsDBNull(6) && reader.GetInt32(6) != 0,
            GetNullableInt32(reader, 7),
            GetNullableString(reader, 8));
    }

    private async Task ApplyPortableMetadataAsync(
        int gameFileId,
        PortableEntry entry,
        bool includeGeneralMetadata,
        bool includePersonalMetadata,
        CancellationToken cancellationToken)
    {
        if (!includeGeneralMetadata && !includePersonalMetadata)
            return;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = (includeGeneralMetadata, includePersonalMetadata) switch
            {
                (true, true) => """
                  UPDATE GameFiles
                  SET ReleaseDate=$releaseDate, Map=$map, MapCount=$mapCount,
                      Rating=$rating, MinutesPlayed=$minutesPlayed,
                      LastPlayed=$lastPlayed, IsSyncNeeded=1
                  WHERE GameFileID=$id;
                  """,
                (true, false) => """
                  UPDATE GameFiles
                  SET ReleaseDate=$releaseDate, Map=$map, MapCount=$mapCount,
                      Rating=$rating, IsSyncNeeded=1
                  WHERE GameFileID=$id;
                  """,
                (false, true) => """
                  UPDATE GameFiles
                  SET MinutesPlayed=$minutesPlayed, LastPlayed=$lastPlayed,
                      IsSyncNeeded=1
                  WHERE GameFileID=$id;
                  """,
                _ => throw new InvalidOperationException(),
            };
            if (includeGeneralMetadata)
            {
                command.Parameters.AddWithValue("$releaseDate", DbValue(entry.ReleaseDate));
                command.Parameters.AddWithValue("$map", DbValue(entry.Map));
                command.Parameters.AddWithValue("$mapCount", entry.MapCount);
                command.Parameters.AddWithValue("$rating", entry.Rating);
            }
            if (includePersonalMetadata)
            {
                command.Parameters.AddWithValue(
                    "$minutesPlayed",
                    entry.MinutesPlayed);
                command.Parameters.AddWithValue(
                    "$lastPlayed",
                    DbValue(entry.LastPlayed));
            }
            command.Parameters.AddWithValue("$id", gameFileId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (includeGeneralMetadata && entry.IdGamesId.HasValue)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO WinUI_IdGamesMetadata
                    (GameFileID, IdGamesID, ArchiveDirectory, ScrapedAt)
                VALUES ($id, $idGamesId, $directory, $scrapedAt)
                ON CONFLICT(GameFileID) DO UPDATE SET
                    IdGamesID=excluded.IdGamesID,
                    ArchiveDirectory=excluded.ArchiveDirectory,
                    ScrapedAt=excluded.ScrapedAt;
                """;
            command.Parameters.AddWithValue("$id", gameFileId);
            command.Parameters.AddWithValue("$idGamesId", entry.IdGamesId.Value);
            command.Parameters.AddWithValue("$directory", DbValue(entry.IdGamesDirectory));
            command.Parameters.AddWithValue(
                "$scrapedAt",
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task AddFileToArchiveAsync(
        ZipArchive archive,
        string sourcePath,
        string entryName,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(
            entryName.Replace('\\', '/'),
            CompressionLevel.Optimal);
        await using var source = File.OpenRead(sourcePath);
        await using var destination = entry.Open();
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static string StoreImportedCollectionArtwork(
        string databasePath,
        string collectionName,
        string sourcePath)
    {
        var portableRoot = Path.GetDirectoryName(databasePath)!;
        var artworkDirectory = Path.Combine(
            portableRoot,
            "Data",
            "CollectionArtworks");
        Directory.CreateDirectory(artworkDirectory);
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not ".png"
            and not ".jpg"
            and not ".jpeg"
            and not ".bmp")
        {
            throw new InvalidDataException(
                $"Unsupported collection artwork format '{extension}'.");
        }
        var collectionHash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(collectionName.ToUpperInvariant())))
            .ToLowerInvariant()[..20];
        var contentHash = Convert.ToHexString(SHA256.HashData(
                File.ReadAllBytes(sourcePath)))
            .ToLowerInvariant()[..12];
        var destination = Path.Combine(
            artworkDirectory,
            $"{collectionHash}-{contentHash}{extension}");
        File.Copy(sourcePath, destination, overwrite: true);
        return Path.GetRelativePath(portableRoot, destination);
    }

    private static async Task<string> ExtractBundleEntryAsync(
        ZipArchive archive,
        string entryName,
        string temporaryDirectory,
        CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(entryName.Replace('\\', '/'))
            ?? throw new InvalidDataException($"Bundle entry '{entryName}' is missing.");
        var entryDirectoryName = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(entry.FullName)))[..16];
        var entryDirectory = Path.Combine(temporaryDirectory, entryDirectoryName);
        Directory.CreateDirectory(entryDirectory);
        var destination = Path.GetFullPath(
            Path.Combine(entryDirectory, Path.GetFileName(entry.Name)));
        var root = Path.GetFullPath(temporaryDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The bundle contains an unsafe file path.");
        await using var source = entry.Open();
        await using var output = File.Create(destination);
        await source.CopyToAsync(output, cancellationToken);
        return destination;
    }

    private sealed record PortableMetadata(
        string? ReleaseDate,
        string? Map,
        int MapCount,
        double Rating,
        int MinutesPlayed,
        string? LastPlayed,
        bool IsFinished,
        int? IdGamesId,
        string? IdGamesDirectory);

    private sealed record PortableManifest(
        int FormatVersion,
        string Application,
        string CreatedAt,
        bool ContainsPersonalMetadata = true,
        bool ContainsGeneralMetadata = true,
        bool ContainsScreenshots = true,
        bool ContainsTitleArtwork = true,
        bool ContainsCollections = true,
        IReadOnlyList<PortableEntry>? Entries = null,
        IReadOnlyList<PortableCollection>? Collections = null);

    private sealed record PortableEntry(
        string GameArchive,
        string? TitleArtwork,
        IReadOnlyList<string>? Screenshots,
        string Title,
        string Author,
        string Description,
        string? SourcePort,
        string? Iwad,
        string? ReleaseDate,
        string? Map,
        int MapCount,
        double Rating,
        int MinutesPlayed,
        string? LastPlayed,
        bool IsFinished,
        int? IdGamesId,
        string? IdGamesDirectory,
        IReadOnlyList<string>? Collections,
        bool IsFavorite = false);

    private sealed record PortableCollection(
        string Name,
        string? Artwork,
        bool ShowAsLibraryFilter);

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<int> ScalarIntAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static async Task DeleteGameDatabaseRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int gameFileId,
        CancellationToken cancellationToken)
    {
        foreach (var sql in new[]
                 {
                     "DELETE FROM WinUI_GameState WHERE GameFileID=$gameFileId;",
                     "DELETE FROM WinUI_IdGamesDownloads WHERE GameFileID=$gameFileId;",
                     "DELETE FROM WinUI_IdGamesMetadata WHERE GameFileID=$gameFileId;",
                     "DELETE FROM Files WHERE GameFileID=$gameFileId;",
                     "DELETE FROM Stats WHERE GameFileID=$gameFileId;",
                     "DELETE FROM GameProfiles WHERE GameFileID=$gameFileId;",
                     "DELETE FROM TagMapping WHERE FileID=$gameFileId;",
                     "DELETE FROM GameFiles WHERE GameFileID=$gameFileId;",
                 })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$gameFileId", gameFileId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<string>> CollectGamePhysicalFilesAsync(
        SqliteConnection connection,
        string databasePath,
        int gameFileId,
        CancellationToken cancellationToken)
    {
        var configuration = await LoadConfigurationAsync(connection, cancellationToken);
        var root = ResolveGameFileDirectory(databasePath, configuration);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var game = connection.CreateCommand())
        {
            game.CommandText =
                "SELECT FileName FROM GameFiles WHERE GameFileID=$gameFileId;";
            game.Parameters.AddWithValue("$gameFileId", gameFileId);
            var reference = Convert.ToString(
                await game.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(reference))
                throw new InvalidOperationException(
                    "Der Bibliothekseintrag existiert nicht mehr.");
            var path = Path.GetFullPath(
                Path.IsPathFullyQualified(reference)
                    ? reference
                    : Path.Combine(root, reference));
            if (!IsPathWithin(path, root))
            {
                throw new InvalidOperationException(
                    "Die physische Datei liegt außerhalb des portablen " +
                    "Data-Verzeichnisses und wird aus Sicherheitsgründen " +
                    "nicht automatisch gelöscht.");
            }
            files.Add(path);
        }

        await using var media = connection.CreateCommand();
        media.CommandText =
            """
            SELECT FileName, FileTypeID
            FROM Files
            WHERE GameFileID=$gameFileId;
            """;
        media.Parameters.AddWithValue("$gameFileId", gameFileId);
        await using var reader = await media.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var directory = reader.GetInt32(1) switch
            {
                1 => "Screenshots",
                2 => "Demos",
                4 => "Thumbnails",
                6 => "TitlePics",
                _ => null,
            };
            if (directory is null)
                continue;
            var path = Path.GetFullPath(Path.Combine(
                root,
                directory,
                reader.GetString(0)));
            if (IsPathWithin(path, Path.Combine(root, directory)))
                files.Add(path);
        }
        return files.ToArray();
    }

    private static void DeletePhysicalFilesAfterDatabase(
        IReadOnlyList<string> paths,
        string itemType)
    {
        var failures = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception exception)
                when (exception is IOException
                    or UnauthorizedAccessException
                    or NotSupportedException)
            {
                failures.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }
        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"Der {itemType}-Eintrag wurde gelöscht, aber nicht alle " +
                "physischen Dateien konnten entfernt werden: " +
                string.Join(" | ", failures));
        }
    }

    private static async Task EnsureSourcePortDirectoryIsNotSharedAsync(
        SqliteConnection connection,
        int sourcePortId,
        string physicalDirectory,
        CancellationToken cancellationToken)
    {
        var databasePath = connection.DataSource;
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Directory FROM SourcePorts WHERE SourcePortID<>$id;";
        command.Parameters.AddWithValue("$id", sourcePortId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var other = ResolveStoredPath(databasePath, reader.GetString(0));
            if (other.Equals(physicalDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Das Source-Port-Verzeichnis wird von einer weiteren " +
                    "Definition verwendet und kann deshalb nicht gelöscht werden.");
            }
        }
    }

    private static string ResolveStoredPath(
        string databasePath,
        string reference)
    {
        var expanded = Environment.ExpandEnvironmentVariables(reference.Trim());
        return Path.GetFullPath(
            Path.IsPathFullyQualified(expanded)
                ? expanded
                : Path.Combine(Path.GetDirectoryName(databasePath)!, expanded));
    }

    private static void ValidateDirectoryDeletionTarget(
        string directory,
        params string[] protectedDirectories)
    {
        var full = Path.GetFullPath(directory).TrimEnd('\\', '/');
        var root = Path.GetPathRoot(full)?.TrimEnd('\\', '/');
        if (string.IsNullOrWhiteSpace(full)
            || string.IsNullOrWhiteSpace(root)
            || full.Equals(root, StringComparison.OrdinalIgnoreCase)
            || Directory.GetParent(full) is null
            || protectedDirectories.Any(path =>
                full.Equals(
                    Path.GetFullPath(path).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Dieses Verzeichnis ist als physisches Löschziel nicht zulässig.");
        }
    }

    private static bool IsPathWithin(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd('\\', '/');
        var fullDirectory = Path.GetFullPath(directory).TrimEnd('\\', '/');
        return fullPath.Equals(fullDirectory, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(
                fullDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

}
