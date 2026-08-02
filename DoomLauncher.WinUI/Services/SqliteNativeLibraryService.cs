using System.Globalization;
using System.Drawing;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DoomLauncher.WinUI.Models;
using Microsoft.Data.Sqlite;

namespace DoomLauncher.WinUI.Services;

public sealed class SqliteNativeLibraryService(
    IDoomLauncherDatabaseLocator databaseLocator) : INativeLibraryService
{
    private static readonly HashSet<string> SupportedExtensions = new(
        [".zip", ".wad", ".pk3", ".ipk3", ".pk7", ".pke", ".7z", ".rar"],
        StringComparer.OrdinalIgnoreCase);

    public async Task<GameEditData> LoadGameAsync(
        int gameFileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var sourcePorts = await LoadChoicesAsync(
            connection,
            """
            SELECT source.SourcePortID,
                   source.Name || CASE
                       WHEN NULLIF(TRIM(capability.Version), '') IS NULL THEN ''
                       ELSE ' · ' || TRIM(capability.Version)
                   END
            FROM SourcePorts source
            LEFT JOIN WinUI_SourcePortCapabilities capability
                ON capability.SourcePortID = source.SourcePortID
            ORDER BY source.Name COLLATE NOCASE;
            """,
            cancellationToken);
        var iwads = await LoadChoicesAsync(
            connection,
            """
            SELECT iwad.IWadID,
                   COALESCE(NULLIF(iwad.Name, ''), iwad.FileName) || CASE
                       WHEN NULLIF(TRIM(metadata.Version), '') IS NULL THEN ''
                       ELSE ' · ' || TRIM(metadata.Version)
                   END
            FROM IWads iwad
            LEFT JOIN WinUI_IwadMetadata metadata
                ON metadata.IWadID = iwad.IWadID
            ORDER BY 2 COLLATE NOCASE;
            """,
            cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT FileName, Title, Author, Description, SourcePortID, IWadID
            FROM GameFiles
            WHERE GameFileID = $gameFileId;
            """;
        command.Parameters.AddWithValue("$gameFileId", gameFileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException($"GameFileID {gameFileId} wurde nicht gefunden.");

        return new GameEditData(
            gameFileId,
            reader.GetString(0),
            DatabaseTextSanitizer.SingleLine(GetNullableString(reader, 1))
                is { Length: > 0 } title
                ? title
                : Path.GetFileNameWithoutExtension(reader.GetString(0)),
            DatabaseTextSanitizer.SingleLine(GetNullableString(reader, 2)),
            DatabaseTextSanitizer.Multiline(GetNullableString(reader, 3)),
            GetNullableInt32(reader, 4),
            GetNullableInt32(reader, 5),
            sourcePorts,
            iwads);
    }

    public async Task UpdateGameAsync(
        GameEditData game,
        CancellationToken cancellationToken = default)
    {
        var title = DatabaseTextSanitizer.SingleLine(game.Title);
        var author = DatabaseTextSanitizer.SingleLine(game.Author);
        var description = DatabaseTextSanitizer.Multiline(game.Description);
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Der Titel darf nicht leer sein.", nameof(game));

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE GameFiles
            SET Title = $title,
                Author = $author,
                Description = $description,
                SourcePortID = $sourcePortId,
                IWadID = $iwadId,
                IsSyncNeeded = 1
            WHERE GameFileID = $gameFileId;
            """;
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$author", DbValue(author));
        command.Parameters.AddWithValue("$description", DbValue(description));
        command.Parameters.AddWithValue("$sourcePortId", DbValue(game.SourcePortId));
        command.Parameters.AddWithValue("$iwadId", DbValue(game.IwadId));
        command.Parameters.AddWithValue("$gameFileId", game.GameFileId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Der Bibliothekseintrag konnte nicht aktualisiert werden.");
    }

    public async Task<LauncherSettingsData> LoadSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var values = await LoadConfigurationAsync(connection, cancellationToken);
        var sourcePorts = await LoadChoicesAsync(
            connection,
            """
            SELECT source.SourcePortID,
                   source.Name || CASE
                       WHEN NULLIF(TRIM(capability.Version), '') IS NULL THEN ''
                       ELSE ' · ' || TRIM(capability.Version)
                   END
            FROM SourcePorts source
            LEFT JOIN WinUI_SourcePortCapabilities capability
                ON capability.SourcePortID = source.SourcePortID
            ORDER BY source.Name COLLATE NOCASE;
            """,
            cancellationToken);
        var iwads = await LoadChoicesAsync(
            connection,
            """
            SELECT iwad.IWadID,
                   COALESCE(NULLIF(iwad.Name, ''), iwad.FileName) || CASE
                       WHEN NULLIF(TRIM(metadata.Version), '') IS NULL THEN ''
                       ELSE ' · ' || TRIM(metadata.Version)
                   END
            FROM IWads iwad
            LEFT JOIN WinUI_IwadMetadata metadata
                ON metadata.IWadID = iwad.IWadID
            ORDER BY 2 COLLATE NOCASE;
            """,
            cancellationToken);

        return new LauncherSettingsData(
            values.GetValueOrDefault("GameFileDirectory", "Data\\"),
            ParseNullableInt(values.GetValueOrDefault("DefaultSourcePort")),
            ParseNullableInt(values.GetValueOrDefault("DefaultIWad")),
            ParseBool(values.GetValueOrDefault("ShowPlayDialog"), true),
            ParseBool(values.GetValueOrDefault("ImportScreenshots"), true),
            Math.Clamp(ParseNullableInt(values.GetValueOrDefault("ItemsPerPage")) ?? 60, 20, 250),
            Math.Clamp(ParseNullableInt(values.GetValueOrDefault("HomeItemsPerGroup")) ?? 10, 1, 20),
            values.GetValueOrDefault("ColorThemeType", "Dark"),
            NormalizePlaceholderArtworkStyle(
                values.GetValueOrDefault(
                    "PlaceholderArtworkStyle",
                    "Grayscale")),
            sourcePorts,
            iwads);
    }

    public async Task UpdateSettingsAsync(
        LauncherSettingsData settings,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.GameFileDirectory))
            throw new ArgumentException("Das Spieleverzeichnis darf nicht leer sein.", nameof(settings));

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await UpsertConfigurationAsync(
            connection,
            transaction,
            "GameFileDirectory",
            settings.GameFileDirectory.Trim(),
            cancellationToken);
        await UpsertConfigurationAsync(
            connection,
            transaction,
            "DefaultSourcePort",
            settings.DefaultSourcePortId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            cancellationToken);
        await UpsertConfigurationAsync(
            connection,
            transaction,
            "DefaultIWad",
            settings.DefaultIwadId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            cancellationToken);
        await UpsertConfigurationAsync(
            connection,
            transaction,
            "ShowPlayDialog",
            settings.ShowPlayDialog.ToString().ToLowerInvariant(),
            cancellationToken);
        await UpsertConfigurationAsync(
            connection,
            transaction,
            "ImportScreenshots",
            settings.ImportScreenshots.ToString().ToLowerInvariant(),
            cancellationToken);
        await UpsertConfigurationAsync(
            connection,
            transaction,
            "ItemsPerPage",
            Math.Clamp(settings.ItemsPerPage, 20, 250).ToString(CultureInfo.InvariantCulture),
            cancellationToken);
        await UpsertConfigurationAsync(
            connection,
            transaction,
            "HomeItemsPerGroup",
            Math.Clamp(settings.HomeItemsPerGroup, 1, 20).ToString(CultureInfo.InvariantCulture),
            cancellationToken);
        await UpsertConfigurationAsync(
            connection,
            transaction,
            "ColorThemeType",
            settings.ColorTheme,
            cancellationToken);
        await UpsertConfigurationAsync(
            connection,
            transaction,
            "PlaceholderArtworkStyle",
            NormalizePlaceholderArtworkStyle(settings.PlaceholderArtworkStyle),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static string NormalizePlaceholderArtworkStyle(string? value) =>
        string.Equals(value, "Colored", StringComparison.OrdinalIgnoreCase)
            ? "Colored"
            : "Grayscale";

    public Task<NativeImportResult> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default) =>
        ImportAsync(
            sourcePath,
            ImportFileConflictResolution.Fail,
            cancellationToken);

    public async Task<NativeImportResult> ImportAsync(
        string sourcePath,
        ImportFileConflictResolution conflictResolution,
        CancellationToken cancellationToken = default)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Die Importdatei wurde nicht gefunden.", sourcePath);
        if (!SupportedExtensions.Contains(Path.GetExtension(sourcePath)))
            throw new NotSupportedException("Unterstützt werden WAD, PK3, PK7, ZIP, 7Z und RAR.");

        var databasePath = databaseLocator.FindDatabase();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var values = await LoadConfigurationAsync(connection, cancellationToken);
        var gameFileDirectory = ResolveGameFileDirectory(databasePath, values);
        var modsDirectory = Path.Combine(gameFileDirectory, "Mods");
        Directory.CreateDirectory(modsDirectory);

        var relativeSource = Path.GetRelativePath(modsDirectory, sourcePath);
        var sourceIsManaged = !Path.IsPathFullyQualified(relativeSource)
            && !relativeSource.Equals("..", StringComparison.Ordinal)
            && !relativeSource.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal);
        var relativeModFile = sourceIsManaged
            ? relativeSource
            : Path.GetFileName(sourcePath);
        var destinationFileName = Path.Combine("Mods", relativeModFile);
        var destinationPath = sourceIsManaged
            ? sourcePath
            : Path.Combine(modsDirectory, relativeModFile);
        int? existingGameFileId = null;
        await using (var duplicate = connection.CreateCommand())
        {
            duplicate.CommandText =
                """
                SELECT GameFileID
                FROM GameFiles
                WHERE FileName=$file COLLATE NOCASE
                   OR FileName=$bare COLLATE NOCASE
                ORDER BY GameFileID
                LIMIT 1;
                """;
            duplicate.Parameters.AddWithValue("$file", destinationFileName);
            duplicate.Parameters.AddWithValue("$bare", relativeModFile);
            var value = await duplicate.ExecuteScalarAsync(cancellationToken);
            if (value is not null && value != DBNull.Value)
                existingGameFileId = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        var hasConflict = existingGameFileId.HasValue
            || (!sourceIsManaged && File.Exists(destinationPath));
        if (hasConflict)
        {
            if (conflictResolution == ImportFileConflictResolution.Fail)
            {
                throw new InvalidOperationException(
                    $"Der Mod {relativeModFile} ist bereits in der Bibliothek vorhanden.");
            }
            if (conflictResolution == ImportFileConflictResolution.Skip)
            {
                return new NativeImportResult(
                    existingGameFileId ?? 0,
                    destinationFileName,
                    destinationPath,
                    WasSkipped: true,
                    ReusedExisting: existingGameFileId.HasValue);
            }
        }
        var createdFile = false;

        try
        {
            if (!sourceIsManaged)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                var temporaryPath = destinationPath + $".importing-{Guid.NewGuid():N}";
                try
                {
                    await using var source = new FileStream(
                        sourcePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        1024 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using (var destination = new FileStream(
                                     temporaryPath,
                                     FileMode.CreateNew,
                                     FileAccess.Write,
                                     FileShare.None,
                                     1024 * 1024,
                                     FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        await source.CopyToAsync(destination, cancellationToken);
                    }

                    File.Move(
                        temporaryPath,
                        destinationPath,
                        overwrite: conflictResolution == ImportFileConflictResolution.Overwrite);
                    createdFile = !hasConflict;
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
            }

            var detectedMaps = await MapNameExtractor.ExtractAsync(
                destinationPath,
                cancellationToken);
            var textMetadata = await ArchiveTextMetadataReader.ReadAsync(
                destinationPath,
                cancellationToken);
            if (existingGameFileId.HasValue)
            {
                await using var refresh = connection.CreateCommand();
                refresh.CommandText =
                    """
                    UPDATE GameFiles
                    SET Map=$maps, MapCount=$mapCount, IsSyncNeeded=1
                    WHERE GameFileID=$id;
                    """;
                refresh.Parameters.AddWithValue(
                    "$maps",
                    detectedMaps.Count == 0
                        ? DBNull.Value
                        : string.Join(", ", detectedMaps));
                refresh.Parameters.AddWithValue("$mapCount", detectedMaps.Count);
                refresh.Parameters.AddWithValue("$id", existingGameFileId.Value);
                await refresh.ExecuteNonQueryAsync(cancellationToken);
                return new NativeImportResult(
                    existingGameFileId.Value,
                    destinationFileName,
                    destinationPath,
                    ReusedExisting: true);
            }
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO GameFiles
                    (FileName, Title, Author, Description, ReleaseDate,
                     Downloaded, MinutesPlayed, Map, MapCount, IsSyncNeeded)
                VALUES
                    ($fileName, $title, $author, $description, $releaseDate,
                     $downloaded, 0, $maps, $mapCount, 1);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$fileName", destinationFileName);
            command.Parameters.AddWithValue(
                "$title",
                string.IsNullOrWhiteSpace(textMetadata.Title)
                    ? DatabaseTextSanitizer.SingleLine(
                        Path.GetFileNameWithoutExtension(destinationFileName))
                    : textMetadata.Title);
            command.Parameters.AddWithValue(
                "$author",
                string.IsNullOrWhiteSpace(textMetadata.Author)
                    ? DBNull.Value
                    : textMetadata.Author);
            command.Parameters.AddWithValue(
                "$description",
                string.IsNullOrWhiteSpace(textMetadata.Description)
                    ? DBNull.Value
                    : textMetadata.Description);
            command.Parameters.AddWithValue(
                "$releaseDate",
                textMetadata.ReleaseDate.HasValue
                    ? textMetadata.ReleaseDate.Value.ToString(
                        "yyyy-MM-dd HH:mm:ss",
                        CultureInfo.InvariantCulture)
                    : DBNull.Value);
            command.Parameters.AddWithValue(
                "$downloaded",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue(
                "$maps",
                detectedMaps.Count == 0
                    ? DBNull.Value
                    : string.Join(", ", detectedMaps));
            command.Parameters.AddWithValue("$mapCount", detectedMaps.Count);
            var gameFileId = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
            return new NativeImportResult(gameFileId, destinationFileName, destinationPath);
        }
        catch
        {
            if (createdFile && File.Exists(destinationPath))
                File.Delete(destinationPath);
            throw;
        }
    }

    public async Task<NativeImportConflict?> FindImportConflictAsync(
        string originalFileName,
        CancellationToken cancellationToken = default)
    {
        var safeName = Path.GetFileName(originalFileName);
        if (string.IsNullOrWhiteSpace(safeName))
            throw new ArgumentException("A file name is required.", nameof(originalFileName));
        var databasePath = databaseLocator.FindDatabase();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var configuration = await LoadConfigurationAsync(connection, cancellationToken);
        var destinationReference = Path.Combine("Mods", safeName);
        int? existingGameFileId = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT GameFileID
                FROM GameFiles
                WHERE FileName=$file COLLATE NOCASE
                   OR FileName=$bare COLLATE NOCASE
                ORDER BY GameFileID
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$file", destinationReference);
            command.Parameters.AddWithValue("$bare", safeName);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            if (value is not null && value != DBNull.Value)
                existingGameFileId = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        var destination = Path.Combine(
            ResolveGameFileDirectory(databasePath, configuration),
            destinationReference);
        var physicalExists = File.Exists(destination);
        return existingGameFileId.HasValue || physicalExists
            ? new NativeImportConflict(safeName, existingGameFileId, physicalExists)
            : null;
    }

    public async Task<GameCollectionsData> LoadGameCollectionsAsync(
        int gameFileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var tags = new List<NativeTag>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT TagID, Name FROM Tags ORDER BY Name COLLATE NOCASE;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                tags.Add(new NativeTag(reader.GetInt32(0), reader.GetString(1)));
        }

        var selected = new HashSet<int>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT TagID FROM TagMapping WHERE FileID = $gameFileId;";
            command.Parameters.AddWithValue("$gameFileId", gameFileId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                selected.Add(reader.GetInt32(0));
        }

        return new GameCollectionsData(tags, selected);
    }

    public async Task SaveGameCollectionsAsync(
        int gameFileId,
        IReadOnlySet<int> tagIds,
        string? newCollectionName,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var selected = new HashSet<int>(tagIds);

        if (!string.IsNullOrWhiteSpace(newCollectionName))
        {
            await using var create = connection.CreateCommand();
            create.Transaction = transaction;
            create.CommandText =
                """
                INSERT INTO Tags (Name, HasTab)
                SELECT $name, 1
                WHERE NOT EXISTS (
                    SELECT 1 FROM Tags WHERE Name = $name COLLATE NOCASE
                );
                SELECT TagID FROM Tags WHERE Name = $name COLLATE NOCASE LIMIT 1;
                """;
            create.Parameters.AddWithValue("$name", newCollectionName.Trim());
            selected.Add(Convert.ToInt32(
                await create.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture));
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM TagMapping WHERE FileID = $gameFileId;";
            delete.Parameters.AddWithValue("$gameFileId", gameFileId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var tagId in selected)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO TagMapping (FileID, TagID) VALUES ($gameFileId, $tagId);";
            insert.Parameters.AddWithValue("$gameFileId", gameFileId);
            insert.Parameters.AddWithValue("$tagId", tagId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteCollectionAsync(
        int tagId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var mappings = connection.CreateCommand())
        {
            mappings.Transaction = transaction;
            mappings.CommandText = "DELETE FROM TagMapping WHERE TagID = $tagId;";
            mappings.Parameters.AddWithValue("$tagId", tagId);
            await mappings.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var collection = connection.CreateCommand())
        {
            collection.Transaction = transaction;
            collection.CommandText = "DELETE FROM Tags WHERE TagID = $tagId;";
            collection.Parameters.AddWithValue("$tagId", tagId);
            if (await collection.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Die Sammlung wurde nicht gefunden.");
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CreateCollectionAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = DatabaseTextSanitizer.SingleLine(name);
        if (normalizedName.Length == 0)
            throw new ArgumentException("A collection name is required.", nameof(name));

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Tags (Name, HasTab)
            SELECT $name, 1
            WHERE NOT EXISTS (
                SELECT 1
                FROM Tags
                WHERE Name = $name COLLATE NOCASE
            );
            """;
        command.Parameters.AddWithValue("$name", normalizedName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetGameFinishedAsync(
        int gameFileId,
        bool isFinished,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO WinUI_GameState (GameFileID, Finished)
            VALUES ($gameFileId, $finished)
            ON CONFLICT(GameFileID) DO UPDATE SET Finished = excluded.Finished;
            """;
        command.Parameters.AddWithValue("$gameFileId", gameFileId);
        command.Parameters.AddWithValue("$finished", isFinished ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MigrateFinishedStateAsync(
        IReadOnlySet<int> gameFileIds,
        CancellationToken cancellationToken = default)
    {
        if (gameFileIds.Count == 0)
            return;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var gameFileId in gameFileIds)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO WinUI_GameState (GameFileID, Finished)
                SELECT $gameFileId, 1
                WHERE EXISTS (
                    SELECT 1 FROM GameFiles WHERE GameFileID = $gameFileId
                )
                ON CONFLICT(GameFileID) DO UPDATE SET Finished = 1;
                """;
            command.Parameters.AddWithValue("$gameFileId", gameFileId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public Task<NativeImportResult> ImportIdGamesAsync(
        IdGamesItem item,
        string downloadedPath,
        CancellationToken cancellationToken = default) =>
        ImportIdGamesAsync(
            item,
            downloadedPath,
            ImportFileConflictResolution.Fail,
            cancellationToken);

    public async Task<NativeImportResult> ImportIdGamesAsync(
        IdGamesItem item,
        string downloadedPath,
        ImportFileConflictResolution conflictResolution,
        CancellationToken cancellationToken = default)
    {
        var result = await ImportAsync(
            downloadedPath,
            conflictResolution,
            cancellationToken);
        if (result.WasSkipped)
            return result;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE GameFiles
                SET Title = $title,
                    Author = $author,
                    Description = $description,
                    ReleaseDate = $releaseDate,
                    Rating = $rating,
                    Downloaded = $downloaded,
                    IsSyncNeeded = 1
                WHERE GameFileID = $gameFileId;
                """;
            update.Parameters.AddWithValue(
                "$title",
                DatabaseTextSanitizer.SingleLine(item.Title));
            update.Parameters.AddWithValue(
                "$author",
                DbValue(DatabaseTextSanitizer.SingleLine(item.Author)));
            update.Parameters.AddWithValue(
                "$description",
                DbValue(DatabaseTextSanitizer.Multiline(item.Description)));
            update.Parameters.AddWithValue(
                "$releaseDate",
                item.ReleaseDate?.ToString(
                    "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
            update.Parameters.AddWithValue("$rating", item.Rating);
            update.Parameters.AddWithValue(
                "$downloaded",
                DateTime.Now.ToString(
                    "yyyy-MM-dd HH:mm:ss.fffffff",
                    CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$gameFileId", result.GameFileId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var mapping = connection.CreateCommand())
        {
            mapping.Transaction = transaction;
            mapping.CommandText =
                """
                INSERT INTO WinUI_IdGamesDownloads
                    (GameFileID, IdGamesID, ArchiveDirectory, DownloadedAt)
                VALUES
                    ($gameFileId, $idGamesId, $directory, $downloadedAt)
                ON CONFLICT(GameFileID) DO UPDATE SET
                    IdGamesID = excluded.IdGamesID,
                    ArchiveDirectory = excluded.ArchiveDirectory,
                    DownloadedAt = excluded.DownloadedAt;
                """;
            mapping.Parameters.AddWithValue("$gameFileId", result.GameFileId);
            mapping.Parameters.AddWithValue("$idGamesId", item.Id);
            mapping.Parameters.AddWithValue("$directory", item.Directory);
            mapping.Parameters.AddWithValue(
                "$downloadedAt",
                DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
            await mapping.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task UpdateGameFromIdGamesAsync(
        int gameFileId,
        IdGamesItem item,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE GameFiles
                SET Title = $title,
                    Author = $author,
                    Description = $description,
                    ReleaseDate = $releaseDate,
                    Rating = $rating,
                    IsSyncNeeded = 1
                WHERE GameFileID = $gameFileId;
                """;
            update.Parameters.AddWithValue(
                "$title",
                DatabaseTextSanitizer.SingleLine(item.Title));
            update.Parameters.AddWithValue(
                "$author",
                DbValue(DatabaseTextSanitizer.SingleLine(item.Author)));
            update.Parameters.AddWithValue(
                "$description",
                DbValue(DatabaseTextSanitizer.Multiline(item.Description)));
            update.Parameters.AddWithValue(
                "$releaseDate",
                item.ReleaseDate?.ToString(
                    "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
            update.Parameters.AddWithValue("$rating", item.Rating);
            update.Parameters.AddWithValue("$gameFileId", gameFileId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException(
                    "Der Bibliothekseintrag wurde nicht gefunden.");
            }
        }

        await using (var mapping = connection.CreateCommand())
        {
            mapping.Transaction = transaction;
            mapping.CommandText =
                """
                INSERT INTO WinUI_IdGamesMetadata
                    (GameFileID, IdGamesID, ArchiveDirectory, ScrapedAt)
                VALUES
                    ($gameFileId, $idGamesId, $directory, $scrapedAt)
                ON CONFLICT(GameFileID) DO UPDATE SET
                    IdGamesID = excluded.IdGamesID,
                    ArchiveDirectory = excluded.ArchiveDirectory,
                    ScrapedAt = excluded.ScrapedAt;
                """;
            mapping.Parameters.AddWithValue("$gameFileId", gameFileId);
            mapping.Parameters.AddWithValue("$idGamesId", item.Id);
            mapping.Parameters.AddWithValue("$directory", item.Directory);
            mapping.Parameters.AddWithValue(
                "$scrapedAt",
                DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
            await mapping.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        await RefreshMapMetadataAsync(gameFileId, cancellationToken);
    }

    public async Task<string?> ResolveManagedGameFileAsync(
        int gameFileId,
        CancellationToken cancellationToken = default)
    {
        var databasePath = databaseLocator.FindDatabase();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var values = await LoadConfigurationAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT FileName FROM GameFiles WHERE GameFileID = $gameFileId;";
        command.Parameters.AddWithValue("$gameFileId", gameFileId);
        var fileName = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(fileName))
            return null;
        return Path.Combine(
            ResolveGameFileDirectory(databasePath, values),
            fileName);
    }

    public async Task<bool> TryImportTitlePicAsync(
        int gameFileId,
        string archivePath,
        CancellationToken cancellationToken = default)
        => await TryImportTitlePicCoreAsync(
            gameFileId,
            archivePath,
            internalWadFileName: null,
            cancellationToken);

    public async Task<bool> TryImportTitlePicAsync(
        int gameFileId,
        string archivePath,
        string internalWadFileName,
        CancellationToken cancellationToken = default)
        => await TryImportTitlePicCoreAsync(
            gameFileId,
            archivePath,
            internalWadFileName,
            cancellationToken);

    private async Task<bool> TryImportTitlePicCoreAsync(
        int gameFileId,
        string archivePath,
        string? internalWadFileName,
        CancellationToken cancellationToken)
    {
        var titlePic = await TitlePicExtractor.TryExtractPngAsync(
            archivePath,
            internalWadFileName,
            cancellationToken);
        if (titlePic is null)
            return false;
        var databasePath = databaseLocator.FindDatabase();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var values = await LoadConfigurationAsync(connection, cancellationToken);
        var gameFileDirectory = ResolveGameFileDirectory(databasePath, values);
        var titlePicDirectory = Path.Combine(gameFileDirectory, "TitlePics");
        Directory.CreateDirectory(titlePicDirectory);

        var titlePicName = $"{Guid.NewGuid():N}.png";
        var titlePicPath = Path.Combine(titlePicDirectory, titlePicName);
        var titlePicTemporary = titlePicPath + ".importing";
        var oldFiles = new List<(int Type, string Name)>();
        try
        {
            await File.WriteAllBytesAsync(
                titlePicTemporary,
                titlePic,
                cancellationToken);
            File.Move(titlePicTemporary, titlePicPath);

            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(
                    cancellationToken);
            await using (var existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText =
                    """
                    SELECT FileTypeID, FileName
                    FROM Files
                    WHERE GameFileID = $gameFileId
                      AND FileTypeID IN (4, 6);
                    """;
                existing.Parameters.AddWithValue("$gameFileId", gameFileId);
                await using var reader =
                    await existing.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    oldFiles.Add((reader.GetInt32(0), reader.GetString(1)));
            }
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText =
                    """
                    DELETE FROM Files
                    WHERE GameFileID = $gameFileId
                      AND FileTypeID IN (4, 6);
                    """;
                delete.Parameters.AddWithValue("$gameFileId", gameFileId);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var insertTitlePic = connection.CreateCommand())
            {
                insertTitlePic.Transaction = transaction;
                insertTitlePic.CommandText =
                    """
                    INSERT INTO Files
                        (GameFileID, FileName, DateCreated, FileTypeID, FileOrder)
                    VALUES
                        ($gameFileId, $fileName, $created, 6, 0);
                    SELECT last_insert_rowid();
                    """;
                insertTitlePic.Parameters.AddWithValue("$gameFileId", gameFileId);
                insertTitlePic.Parameters.AddWithValue("$fileName", titlePicName);
                insertTitlePic.Parameters.AddWithValue(
                    "$created",
                    DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
                await insertTitlePic.ExecuteScalarAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            DeleteIfExists(titlePicPath);
            throw;
        }
        finally
        {
            DeleteIfExists(titlePicTemporary);
        }

        foreach (var oldFile in oldFiles)
        {
            var directory = oldFile.Type == 6
                ? titlePicDirectory
                : Path.Combine(gameFileDirectory, "Thumbnails");
            DeleteIfExists(Path.Combine(directory, oldFile.Name));
        }
        return true;
    }

    public async Task<int> CleanupDerivedThumbnailsAsync(
        CancellationToken cancellationToken = default)
    {
        var databasePath = databaseLocator.FindDatabase();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var values = await LoadConfigurationAsync(connection, cancellationToken);
        var gameFileDirectory = ResolveGameFileDirectory(databasePath, values);
        var candidates = new List<(int ThumbnailId, string ThumbnailName,
            int OriginalId, string OriginalName, int OriginalType)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT thumbnail.FileID, thumbnail.FileName,
                       original.FileID, original.FileName, original.FileTypeID
                FROM Files thumbnail
                JOIN Files original ON original.FileID = thumbnail.DerivedFromFileID
                WHERE thumbnail.FileTypeID = 4
                  AND original.FileTypeID IN (1, 6);
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add((
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetInt32(4)));
            }
        }

        var removed = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var thumbnailPath = Path.Combine(
                gameFileDirectory,
                "Thumbnails",
                candidate.ThumbnailName);
            var originalDirectory = candidate.OriginalType == 6
                ? "TitlePics"
                : "Screenshots";
            var originalPath = Path.Combine(
                gameFileDirectory,
                originalDirectory,
                candidate.OriginalName);
            if (!File.Exists(thumbnailPath)
                || !File.Exists(originalPath)
                || !IsHigherResolution(originalPath, thumbnailPath))
            {
                continue;
            }

            string? movedPath = null;
            if (candidate.OriginalType == 1)
            {
                var titlePicDirectory = Path.Combine(gameFileDirectory, "TitlePics");
                Directory.CreateDirectory(titlePicDirectory);
                movedPath = Path.Combine(titlePicDirectory, candidate.OriginalName);
                if (File.Exists(movedPath))
                    continue;
                File.Move(originalPath, movedPath);
            }

            try
            {
                await using var transaction =
                    (SqliteTransaction)await connection.BeginTransactionAsync(
                        cancellationToken);
                if (candidate.OriginalType == 1)
                {
                    await using var promote = connection.CreateCommand();
                    promote.Transaction = transaction;
                    promote.CommandText =
                        "UPDATE Files SET FileTypeID = 6 WHERE FileID = $id;";
                    promote.Parameters.AddWithValue("$id", candidate.OriginalId);
                    await promote.ExecuteNonQueryAsync(cancellationToken);
                }
                await using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM Files WHERE FileID = $id;";
                    delete.Parameters.AddWithValue("$id", candidate.ThumbnailId);
                    await delete.ExecuteNonQueryAsync(cancellationToken);
                }
                await transaction.CommitAsync(cancellationToken);
                DeleteIfExists(thumbnailPath);
                removed++;
            }
            catch
            {
                if (movedPath is not null
                    && File.Exists(movedPath)
                    && !File.Exists(originalPath))
                {
                    File.Move(movedPath, originalPath);
                }
                throw;
            }
        }
        return removed;
    }

    public async Task<GameMediaData> LoadGameMediaAsync(
        int gameFileId,
        CancellationToken cancellationToken = default)
    {
        var databasePath = databaseLocator.FindDatabase();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var values = await LoadConfigurationAsync(connection, cancellationToken);
        var gameFileDirectory = ResolveGameFileDirectory(databasePath, values);
        NativeMediaFile? titleArtwork = null;
        var screenshots = new List<NativeMediaFile>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT FileID, FileName, FileTypeID, COALESCE(FileOrder, 0)
            FROM Files
            WHERE GameFileID = $gameFileId
              AND FileTypeID IN (1, 6)
            ORDER BY FileTypeID DESC, COALESCE(FileOrder, 0), FileID;
            """;
        command.Parameters.AddWithValue("$gameFileId", gameFileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var type = reader.GetInt32(2);
            var fileName = reader.GetString(1);
            var fullPath = Path.Combine(
                gameFileDirectory,
                type == 6 ? "TitlePics" : "Screenshots",
                fileName);
            if (!File.Exists(fullPath))
                continue;
            var media = new NativeMediaFile(
                reader.GetInt32(0),
                fileName,
                fullPath,
                reader.GetInt32(3));
            if (type == 6 && titleArtwork is null)
                titleArtwork = media;
            else if (type == 1)
                screenshots.Add(media);
        }
        return new GameMediaData(titleArtwork, screenshots);
    }

    public async Task SetTitleArtworkAsync(
        int gameFileId,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ValidateImageFile(sourcePath);
        var databasePath = databaseLocator.FindDatabase();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var values = await LoadConfigurationAsync(connection, cancellationToken);
        var gameFileDirectory = ResolveGameFileDirectory(databasePath, values);
        var titlePicDirectory = Path.Combine(gameFileDirectory, "TitlePics");
        Directory.CreateDirectory(titlePicDirectory);
        var fileName = $"{Guid.NewGuid():N}{Path.GetExtension(sourcePath).ToLowerInvariant()}";
        var destinationPath = Path.Combine(titlePicDirectory, fileName);
        File.Copy(sourcePath, destinationPath, overwrite: false);
        var oldFiles = new List<(int Type, string Name)>();
        try
        {
            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using (var existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText =
                    """
                    SELECT FileTypeID, FileName
                    FROM Files
                    WHERE GameFileID=$gameFileId AND FileTypeID IN (4, 6);
                    """;
                existing.Parameters.AddWithValue("$gameFileId", gameFileId);
                await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    oldFiles.Add((reader.GetInt32(0), reader.GetString(1)));
            }
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText =
                    """
                    DELETE FROM Files
                    WHERE GameFileID=$gameFileId AND FileTypeID IN (4, 6);
                    """;
                delete.Parameters.AddWithValue("$gameFileId", gameFileId);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO Files
                        (GameFileID, FileName, DateCreated, FileTypeID, FileOrder, IsMain)
                    VALUES
                        ($gameFileId, $fileName, $created, 6, 0, 1);
                    """;
                insert.Parameters.AddWithValue("$gameFileId", gameFileId);
                insert.Parameters.AddWithValue("$fileName", fileName);
                insert.Parameters.AddWithValue(
                    "$created",
                    DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            DeleteIfExists(destinationPath);
            throw;
        }
        foreach (var oldFile in oldFiles)
        {
            if (!oldFile.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                DeleteIfExists(Path.Combine(
                    gameFileDirectory,
                    oldFile.Type == 4 ? "Thumbnails" : "TitlePics",
                    oldFile.Name));
            }
        }
    }

    public async Task RemoveTitleArtworkAsync(
        int gameFileId,
        CancellationToken cancellationToken = default)
    {
        var databasePath = databaseLocator.FindDatabase();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var values = await LoadConfigurationAsync(connection, cancellationToken);
        var gameFileDirectory = ResolveGameFileDirectory(databasePath, values);
        var oldFiles = new List<(int Type, string Name)>();
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText =
                """
                SELECT FileTypeID, FileName
                FROM Files
                WHERE GameFileID=$gameFileId AND FileTypeID IN (4, 6);
                """;
            existing.Parameters.AddWithValue("$gameFileId", gameFileId);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                oldFiles.Add((reader.GetInt32(0), reader.GetString(1)));
        }
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText =
                """
                DELETE FROM Files
                WHERE GameFileID=$gameFileId AND FileTypeID IN (4, 6);
                """;
            delete.Parameters.AddWithValue("$gameFileId", gameFileId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        foreach (var oldFile in oldFiles)
        {
            DeleteIfExists(Path.Combine(
                gameFileDirectory,
                oldFile.Type == 4 ? "Thumbnails" : "TitlePics",
                oldFile.Name));
        }
    }

    public async Task AddScreenshotsAsync(
        int gameFileId,
        IReadOnlyList<string> sourcePaths,
        CancellationToken cancellationToken = default)
    {
        var uniqueSources = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var sourcePath in uniqueSources)
            ValidateImageFile(sourcePath);
        if (uniqueSources.Length == 0)
            return;

        var databasePath = databaseLocator.FindDatabase();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var existingOriginalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var existing = connection.CreateCommand())
        {
            existing.CommandText =
                """
                SELECT OriginalFilePath
                FROM Files
                WHERE GameFileID=$gameFileId
                  AND FileTypeID=1
                  AND COALESCE(OriginalFilePath, '') <> '';
                """;
            existing.Parameters.AddWithValue("$gameFileId", gameFileId);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var originalPath = reader.GetString(0);
                try
                {
                    existingOriginalPaths.Add(Path.GetFullPath(originalPath));
                }
                catch (Exception exception)
                    when (exception is ArgumentException or NotSupportedException
                          or PathTooLongException)
                {
                    existingOriginalPaths.Add(originalPath);
                }
            }
        }
        uniqueSources = uniqueSources
            .Where(path => !existingOriginalPaths.Contains(path))
            .ToArray();
        if (uniqueSources.Length == 0)
            return;
        var values = await LoadConfigurationAsync(connection, cancellationToken);
        var gameFileDirectory = ResolveGameFileDirectory(databasePath, values);
        var screenshotDirectory = Path.Combine(gameFileDirectory, "Screenshots");
        Directory.CreateDirectory(screenshotDirectory);
        var copied = new List<(string Source, string Name, string Destination)>();
        try
        {
            foreach (var sourcePath in uniqueSources)
            {
                var name =
                    $"{Guid.NewGuid():N}{Path.GetExtension(sourcePath).ToLowerInvariant()}";
                var destination = Path.Combine(screenshotDirectory, name);
                File.Copy(sourcePath, destination, overwrite: false);
                copied.Add((sourcePath, name, destination));
            }
            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var nextOrder = 0;
            await using (var order = connection.CreateCommand())
            {
                order.Transaction = transaction;
                order.CommandText =
                    """
                    SELECT COALESCE(MAX(FileOrder), -1) + 1
                    FROM Files
                    WHERE GameFileID=$gameFileId AND FileTypeID=1;
                    """;
                order.Parameters.AddWithValue("$gameFileId", gameFileId);
                nextOrder = Convert.ToInt32(
                    await order.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture);
            }
            foreach (var item in copied)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO Files
                        (GameFileID, FileName, DateCreated, FileTypeID,
                         OriginalFileName, OriginalFilePath, FileOrder, IsMain)
                    VALUES
                        ($gameFileId, $fileName, $created, 1,
                         $originalName, $originalPath, $order, 0);
                    """;
                insert.Parameters.AddWithValue("$gameFileId", gameFileId);
                insert.Parameters.AddWithValue("$fileName", item.Name);
                insert.Parameters.AddWithValue(
                    "$created",
                    File.GetCreationTime(item.Source).ToString(
                        "O",
                        CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue(
                    "$originalName",
                    Path.GetFileName(item.Source));
                insert.Parameters.AddWithValue("$originalPath", item.Source);
                insert.Parameters.AddWithValue("$order", nextOrder++);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            foreach (var item in copied)
                DeleteIfExists(item.Destination);
            throw;
        }
    }

    public async Task RemoveScreenshotAsync(
        int gameFileId,
        int screenshotFileId,
        CancellationToken cancellationToken = default)
    {
        var media = await LoadGameMediaAsync(gameFileId, cancellationToken);
        var screenshot = media.Screenshots.FirstOrDefault(
            item => item.FileId == screenshotFileId)
            ?? throw new InvalidOperationException("Der Screenshot wurde nicht gefunden.");
        await using (var connection = await OpenConnectionAsync(cancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM Files
                WHERE FileID=$fileId AND GameFileID=$gameFileId AND FileTypeID=1;
                """;
            command.Parameters.AddWithValue("$fileId", screenshotFileId);
            command.Parameters.AddWithValue("$gameFileId", gameFileId);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Der Screenshot wurde nicht gefunden.");
        }
        DeleteIfExists(screenshot.FullPath);
        var remaining = (await LoadGameMediaAsync(gameFileId, cancellationToken))
            .Screenshots
            .Select(item => item.FileId)
            .ToArray();
        if (remaining.Length > 0)
        {
            await SetScreenshotOrderAsync(
                gameFileId,
                remaining,
                cancellationToken);
        }
    }

    public async Task SetScreenshotOrderAsync(
        int gameFileId,
        IReadOnlyList<int> screenshotFileIds,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        for (var index = 0; index < screenshotFileIds.Count; index++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE Files
                SET FileOrder=$order
                WHERE FileID=$fileId AND GameFileID=$gameFileId AND FileTypeID=1;
                """;
            command.Parameters.AddWithValue("$order", index);
            command.Parameters.AddWithValue("$fileId", screenshotFileIds[index]);
            command.Parameters.AddWithValue("$gameFileId", gameFileId);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Die Screenshot-Reihenfolge ist ungültig.");
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetScreenshotAsTitleArtworkAsync(
        int gameFileId,
        int screenshotFileId,
        CancellationToken cancellationToken = default)
    {
        var media = await LoadGameMediaAsync(gameFileId, cancellationToken);
        var screenshot = media.Screenshots.FirstOrDefault(
            item => item.FileId == screenshotFileId)
            ?? throw new InvalidOperationException("Der Screenshot wurde nicht gefunden.");
        await SetTitleArtworkAsync(gameFileId, screenshot.FullPath, cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM Files
            WHERE FileID=$fileId AND GameFileID=$gameFileId AND FileTypeID=1;
            """;
        command.Parameters.AddWithValue("$fileId", screenshotFileId);
        command.Parameters.AddWithValue("$gameFileId", gameFileId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
            DeleteIfExists(screenshot.FullPath);
    }

    private static bool IsHigherResolution(string originalPath, string thumbnailPath)
    {
        try
        {
            using var original = Image.FromFile(originalPath);
            using var thumbnail = Image.FromFile(thumbnailPath);
            return original.Width >= thumbnail.Width
                && original.Height >= thumbnail.Height
                && (long)original.Width * original.Height
                    > (long)thumbnail.Width * thumbnail.Height;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void ValidateImageFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Die Bilddatei wurde nicht gefunden.", path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp"))
        {
            throw new NotSupportedException(
                "Unterstützt werden PNG-, JPG-, JPEG- und BMP-Dateien.");
        }
        try
        {
            using var image = Image.FromFile(path);
            if (image.Width < 1 || image.Height < 1)
                throw new InvalidDataException("Die Bilddatei hat keine gültige Größe.");
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Die Datei ist kein gültiges Bild.", exception);
        }
    }

    public async Task<LauncherDefinitionsData> LoadLauncherDefinitionsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var sourcePorts = new List<NativeSourcePortDefinition>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT source.SourcePortID, source.Name, source.Directory,
                       source.Executable, source.SupportedExtensions,
                       source.FileOption, source.ExtraParameters,
                       COALESCE(capability.Version, ''),
                       COALESCE(capability.ScreenshotSupport, 'Auto'),
                       COALESCE(capability.ScreenshotDirectories, ''),
                       COALESCE(capability.ScreenshotExtensions, '.png,.jpg,.jpeg,.bmp'),
                       COALESCE(capability.ScreenshotArgument, ''),
                       COALESCE(capability.StatisticsAdapter, 'None'),
                       COALESCE(capability.StatisticsDirectories, ''),
                       COALESCE(capability.SaveGameExtensions, '.zds')
                FROM SourcePorts source
                LEFT JOIN WinUI_SourcePortCapabilities capability
                    ON capability.SourcePortID = source.SourcePortID
                WHERE COALESCE(source.Archived, 0) = 0
                ORDER BY source.Name COLLATE NOCASE;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                sourcePorts.Add(new NativeSourcePortDefinition(
                    reader.GetInt32(0),
                    DatabaseTextSanitizer.SingleLine(GetNullableString(reader, 1)),
                    GetNullableString(reader, 2) ?? string.Empty,
                    GetNullableString(reader, 3) ?? string.Empty,
                    GetNullableString(reader, 4) ?? string.Empty,
                    GetNullableString(reader, 5) ?? "-file",
                    GetNullableString(reader, 6) ?? string.Empty,
                    GetNullableString(reader, 7) ?? string.Empty,
                    GetNullableString(reader, 8) ?? "Auto",
                    GetNullableString(reader, 9) ?? string.Empty,
                    GetNullableString(reader, 10) ?? ".png,.jpg,.jpeg,.bmp",
                    GetNullableString(reader, 11) ?? string.Empty,
                    GetNullableString(reader, 12) ?? "None",
                    GetNullableString(reader, 13) ?? string.Empty,
                    GetNullableString(reader, 14) ?? ".zds"));
            }
        }

        var iwads = new List<NativeIwadDefinition>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT i.IWadID, i.Name, gf.FileName, i.FileName,
                       COALESCE(metadata.Version, ''),
                       COALESCE(metadata.Md5, ''),
                       COALESCE(metadata.FileSize, 0),
                       COALESCE(metadata.CatalogLabel, '')
                FROM IWads i
                JOIN GameFiles gf ON gf.GameFileID = i.GameFileID
                LEFT JOIN WinUI_IwadMetadata metadata
                    ON metadata.IWadID = i.IWadID
                ORDER BY i.Name COLLATE NOCASE;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                iwads.Add(new NativeIwadDefinition(
                    reader.GetInt32(0),
                    DatabaseTextSanitizer.SingleLine(GetNullableString(reader, 1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt64(6),
                    reader.GetString(7)));
            }
        }

        return new LauncherDefinitionsData(sourcePorts, iwads);
    }

    public async Task SaveSourcePortAsync(
        NativeSourcePortDefinition definition,
        CancellationToken cancellationToken = default)
    {
        var name = DatabaseTextSanitizer.SingleLine(definition.Name);
        if (name.Length == 0
            || string.IsNullOrWhiteSpace(definition.Directory)
            || string.IsNullOrWhiteSpace(definition.Executable))
        {
            throw new ArgumentException(
                "Name, Verzeichnis und Executable des Sourceports sind erforderlich.");
        }
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = definition.SourcePortId.HasValue
            ? """
              UPDATE SourcePorts
              SET Name=$name, Directory=$directory, Executable=$executable,
                  SupportedExtensions=$extensions, FileOption=$fileOption,
                  ExtraParameters=$extra, LaunchType=0, Archived=0
              WHERE SourcePortID=$id;
              """
            : """
              INSERT INTO SourcePorts
                  (Name, Directory, Executable, SupportedExtensions, FileOption,
                   ExtraParameters, SettingsFiles, LaunchType, Archived)
              VALUES
                  ($name, $directory, $executable, $extensions, $fileOption,
                   $extra, '', 0, 0);
              """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$directory", definition.Directory.Trim());
        command.Parameters.AddWithValue("$executable", Path.GetFileName(definition.Executable.Trim()));
        command.Parameters.AddWithValue(
            "$extensions",
            DatabaseTextSanitizer.SingleLine(definition.SupportedExtensions));
        command.Parameters.AddWithValue(
            "$fileOption",
            "-file");
        command.Parameters.AddWithValue(
            "$extra",
            DatabaseTextSanitizer.SingleLine(definition.ExtraParameters));
        command.Parameters.AddWithValue("$id", definition.SourcePortId ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);

        var sourcePortId = definition.SourcePortId;
        if (!sourcePortId.HasValue)
        {
            await using var identity = connection.CreateCommand();
            identity.Transaction = transaction;
            identity.CommandText = "SELECT last_insert_rowid();";
            sourcePortId = Convert.ToInt32(
                await identity.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
        }

        await using var capability = connection.CreateCommand();
        capability.Transaction = transaction;
        capability.CommandText =
            """
            INSERT INTO WinUI_SourcePortCapabilities
                (SourcePortID, Version, ScreenshotSupport, ScreenshotDirectories,
                 ScreenshotExtensions, ScreenshotArgument, StatisticsAdapter,
                 StatisticsDirectories, SaveGameExtensions)
            VALUES
                ($id, $version, $screenshotSupport, $screenshotDirectories,
                 $screenshotExtensions, $screenshotArgument, $statisticsAdapter,
                 $statisticsDirectories, $saveGameExtensions)
            ON CONFLICT(SourcePortID) DO UPDATE SET
                Version=excluded.Version,
                ScreenshotSupport=excluded.ScreenshotSupport,
                ScreenshotDirectories=excluded.ScreenshotDirectories,
                ScreenshotExtensions=excluded.ScreenshotExtensions,
                ScreenshotArgument=excluded.ScreenshotArgument,
                StatisticsAdapter=excluded.StatisticsAdapter,
                StatisticsDirectories=excluded.StatisticsDirectories,
                SaveGameExtensions=excluded.SaveGameExtensions;
            """;
        capability.Parameters.AddWithValue("$id", sourcePortId.Value);
        capability.Parameters.AddWithValue(
            "$version",
            DatabaseTextSanitizer.SingleLine(definition.Version));
        capability.Parameters.AddWithValue(
            "$screenshotSupport",
            definition.ScreenshotSupport is "Auto" or "Configured" or "None"
                ? definition.ScreenshotSupport
                : "Auto");
        capability.Parameters.AddWithValue(
            "$screenshotDirectories",
            DatabaseTextSanitizer.SingleLine(definition.ScreenshotDirectories));
        capability.Parameters.AddWithValue(
            "$screenshotExtensions",
            DatabaseTextSanitizer.SingleLine(definition.ScreenshotExtensions));
        capability.Parameters.AddWithValue(
            "$screenshotArgument",
            string.Empty);
        capability.Parameters.AddWithValue(
            "$statisticsAdapter",
            definition.StatisticsAdapter is "ZDoomSave"
                ? "ZDoomSave"
                : "None");
        capability.Parameters.AddWithValue(
            "$statisticsDirectories",
            DatabaseTextSanitizer.SingleLine(definition.StatisticsDirectories));
        capability.Parameters.AddWithValue(
            "$saveGameExtensions",
            DatabaseTextSanitizer.SingleLine(definition.SaveGameExtensions));
        await capability.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteSourcePortAsync(
        int sourcePortId,
        bool deletePhysicalFiles = false,
        CancellationToken cancellationToken = default)
    {
        if (sourcePortId <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourcePortId));

        var databasePath = databaseLocator.FindDatabase();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        string? physicalDirectory = null;
        if (deletePhysicalFiles)
        {
            await using var source = connection.CreateCommand();
            source.CommandText =
                "SELECT Directory FROM SourcePorts WHERE SourcePortID=$id;";
            source.Parameters.AddWithValue("$id", sourcePortId);
            var storedDirectory = Convert.ToString(
                await source.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(storedDirectory))
            {
                throw new InvalidOperationException(
                    "Die Source-Port-Definition existiert nicht mehr.");
            }

            physicalDirectory = ResolveStoredPath(databasePath, storedDirectory);
            await EnsureSourcePortDirectoryIsNotSharedAsync(
                connection,
                sourcePortId,
                physicalDirectory,
                cancellationToken);
            var configuration = await LoadConfigurationAsync(
                connection,
                cancellationToken);
            ValidateDirectoryDeletionTarget(
                physicalDirectory,
                Path.GetDirectoryName(databasePath)!,
                ResolveGameFileDirectory(databasePath, configuration),
                AppContext.BaseDirectory);
        }

        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var sql in new[]
                 {
                     "UPDATE GameFiles SET SourcePortID=NULL WHERE SourcePortID=$id;",
                     "UPDATE Files SET SourcePortID=NULL WHERE SourcePortID=$id;",
                     """
                     UPDATE Configuration
                     SET Value=''
                     WHERE Name='DefaultSourcePort'
                       AND CAST(COALESCE(Value, '0') AS INTEGER)=$id;
                     """,
                     """
                     DELETE FROM WinUI_SourcePortCapabilities
                     WHERE SourcePortID=$id;
                     """,
                 })
        {
            await using var cleanup = connection.CreateCommand();
            cleanup.Transaction = transaction;
            cleanup.CommandText = sql;
            cleanup.Parameters.AddWithValue("$id", sourcePortId);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM SourcePorts WHERE SourcePortID=$id;";
        delete.Parameters.AddWithValue("$id", sourcePortId);
        if (await delete.ExecuteNonQueryAsync(cancellationToken) == 0)
            throw new InvalidOperationException("Die Source-Port-Definition existiert nicht mehr.");
        await transaction.CommitAsync(cancellationToken);
        if (deletePhysicalFiles
            && physicalDirectory is not null
            && Directory.Exists(physicalDirectory))
        {
            try
            {
                Directory.Delete(physicalDirectory, recursive: true);
            }
            catch (Exception exception)
                when (exception is IOException
                    or UnauthorizedAccessException
                    or NotSupportedException)
            {
                throw new InvalidOperationException(
                    "Die Definition wurde gelöscht, das Source-Port-Verzeichnis " +
                    "konnte jedoch nicht physisch entfernt werden.",
                    exception);
            }
        }
    }

    public async Task SaveIwadAsync(
        NativeIwadDefinition definition,
        CancellationToken cancellationToken = default)
    {
        var name = DatabaseTextSanitizer.SingleLine(definition.Name);
        var archive = DatabaseTextSanitizer.SingleLine(definition.ArchiveFileName);
        var internalName = Path.GetFileName(
            DatabaseTextSanitizer.SingleLine(definition.InternalFileName));
        if (name.Length == 0 || archive.Length == 0 || internalName.Length == 0)
            throw new ArgumentException("Name, Archivdatei und IWAD-Datei sind erforderlich.");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        if (internalName.Equals("HEXDD.WAD", StringComparison.OrdinalIgnoreCase))
        {
            await using var dependency = connection.CreateCommand();
            dependency.Transaction = transaction;
            dependency.CommandText =
                """
                SELECT COUNT(*)
                FROM IWads
                WHERE FileName = 'HEXEN.WAD' COLLATE NOCASE;
                """;
            var count = Convert.ToInt32(
                await dependency.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
            if (count == 0)
            {
                throw new InvalidOperationException(
                    "HEXDD.WAD ist die Erweiterung Deathkings of the Dark Citadel " +
                    "und benötigt HEXEN.WAD. Bitte zuerst HEXEN.WAD als IWAD definieren.");
            }
        }
        int gameFileId;
        var iwadId = definition.IwadId;
        await using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText =
                "SELECT GameFileID FROM GameFiles WHERE FileName=$file COLLATE NOCASE LIMIT 1;";
            find.Parameters.AddWithValue("$file", archive);
            var value = await find.ExecuteScalarAsync(cancellationToken);
            if (value is null)
                throw new InvalidOperationException(
                    $"Das IWAD-Archiv {archive} muss zuerst in die Bibliothek importiert werden.");
            gameFileId = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = definition.IwadId.HasValue
                ? """
                  UPDATE IWads
                  SET Name=$name, FileName=$internal, GameFileID=$gameFileId
                  WHERE IWadID=$id;
                  """
                : """
                  INSERT INTO IWads (Name, FileName, GameFileID)
                  VALUES ($name, $internal, $gameFileId);
                  """;
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$internal", internalName);
            command.Parameters.AddWithValue("$gameFileId", gameFileId);
            command.Parameters.AddWithValue("$id", definition.IwadId ?? (object)DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (!iwadId.HasValue)
        {
            await using var identity = connection.CreateCommand();
            identity.Transaction = transaction;
            identity.CommandText = "SELECT last_insert_rowid();";
            iwadId = Convert.ToInt32(
                await identity.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
        }
        await using (var metadata = connection.CreateCommand())
        {
            metadata.Transaction = transaction;
            metadata.CommandText =
                """
                INSERT INTO WinUI_IwadMetadata
                    (IWadID, Version, Md5, FileSize, CatalogLabel, DetectedAt)
                VALUES
                    ($id, $version, $md5, $fileSize, $catalogLabel, $detectedAt)
                ON CONFLICT(IWadID) DO UPDATE SET
                    Version=excluded.Version,
                    Md5=excluded.Md5,
                    FileSize=excluded.FileSize,
                    CatalogLabel=excluded.CatalogLabel,
                    DetectedAt=excluded.DetectedAt;
                """;
            metadata.Parameters.AddWithValue("$id", iwadId.Value);
            metadata.Parameters.AddWithValue(
                "$version",
                DatabaseTextSanitizer.SingleLine(definition.Version));
            metadata.Parameters.AddWithValue(
                "$md5",
                DatabaseTextSanitizer.SingleLine(definition.Md5).ToLowerInvariant());
            metadata.Parameters.AddWithValue("$fileSize", Math.Max(0, definition.FileSize));
            metadata.Parameters.AddWithValue(
                "$catalogLabel",
                DatabaseTextSanitizer.SingleLine(definition.CatalogLabel));
            metadata.Parameters.AddWithValue(
                "$detectedAt",
                string.IsNullOrWhiteSpace(definition.Md5)
                    ? DBNull.Value
                    : DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await metadata.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteIwadAsync(
        int iwadId,
        bool deletePhysicalFiles = false,
        CancellationToken cancellationToken = default)
    {
        if (iwadId <= 0)
            throw new ArgumentOutOfRangeException(nameof(iwadId));

        var databasePath = databaseLocator.FindDatabase();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        int? gameFileId = null;
        IReadOnlyList<string> physicalFiles = [];
        if (deletePhysicalFiles)
        {
            await using var source = connection.CreateCommand();
            source.CommandText =
                """
                SELECT i.GameFileID
                FROM IWads i
                WHERE i.IWadID=$id;
                """;
            source.Parameters.AddWithValue("$id", iwadId);
            var value = await source.ExecuteScalarAsync(cancellationToken);
            if (value is null)
                throw new InvalidOperationException("Die IWAD-Definition existiert nicht mehr.");
            gameFileId = Convert.ToInt32(value, CultureInfo.InvariantCulture);

            await using var shared = connection.CreateCommand();
            shared.CommandText =
                """
                SELECT COUNT(*)
                FROM IWads
                WHERE GameFileID=$gameFileId AND IWadID<>$id;
                """;
            shared.Parameters.AddWithValue("$gameFileId", gameFileId.Value);
            shared.Parameters.AddWithValue("$id", iwadId);
            if (Convert.ToInt32(
                    await shared.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture) > 0)
            {
                throw new InvalidOperationException(
                    "Die physische Archivdatei wird von weiteren IWAD-Definitionen " +
                    "verwendet und kann deshalb nicht gelöscht werden.");
            }
            physicalFiles = await CollectGamePhysicalFilesAsync(
                connection,
                databasePath,
                gameFileId.Value,
                cancellationToken);
        }

        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var sql in new[]
                 {
                     "UPDATE GameFiles SET IWadID=NULL WHERE IWadID=$id;",
                     """
                     UPDATE Configuration
                     SET Value=''
                     WHERE Name='DefaultIWad'
                       AND CAST(COALESCE(Value, '0') AS INTEGER)=$id;
                     """,
                     "DELETE FROM WinUI_IwadMetadata WHERE IWadID=$id;",
                 })
        {
            await using var cleanup = connection.CreateCommand();
            cleanup.Transaction = transaction;
            cleanup.CommandText = sql;
            cleanup.Parameters.AddWithValue("$id", iwadId);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM IWads WHERE IWadID=$id;";
        delete.Parameters.AddWithValue("$id", iwadId);
        if (await delete.ExecuteNonQueryAsync(cancellationToken) == 0)
            throw new InvalidOperationException("Die IWAD-Definition existiert nicht mehr.");
        if (deletePhysicalFiles && gameFileId.HasValue)
        {
            await DeleteGameDatabaseRowsAsync(
                connection,
                transaction,
                gameFileId.Value,
                cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        if (deletePhysicalFiles)
            DeletePhysicalFilesAfterDatabase(physicalFiles, "IWAD");
    }

    public async Task DeleteGameAsync(
        int gameFileId,
        bool deletePhysicalFiles = false,
        CancellationToken cancellationToken = default)
    {
        if (gameFileId <= 0)
            throw new ArgumentOutOfRangeException(nameof(gameFileId));

        var databasePath = databaseLocator.FindDatabase();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using (var iwad = connection.CreateCommand())
        {
            iwad.CommandText =
                "SELECT COUNT(*) FROM IWads WHERE GameFileID=$gameFileId;";
            iwad.Parameters.AddWithValue("$gameFileId", gameFileId);
            if (Convert.ToInt32(
                    await iwad.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture) > 0)
            {
                throw new InvalidOperationException(
                    "IWADs werden über die Launcher-Definitionen gelöscht.");
            }
        }

        var physicalFiles = deletePhysicalFiles
            ? await CollectGamePhysicalFilesAsync(
                connection,
                databasePath,
                gameFileId,
                cancellationToken)
            : [];
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText =
                "SELECT COUNT(*) FROM GameFiles WHERE GameFileID=$gameFileId;";
            exists.Parameters.AddWithValue("$gameFileId", gameFileId);
            if (Convert.ToInt32(
                    await exists.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture) == 0)
            {
                throw new InvalidOperationException(
                    "Der Bibliothekseintrag existiert nicht mehr.");
            }
        }
        await DeleteGameDatabaseRowsAsync(
            connection,
            transaction,
            gameFileId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (deletePhysicalFiles)
            DeletePhysicalFilesAfterDatabase(physicalFiles, "Mod");
    }

    public async Task<IwadVersionDetection> DetectIwadVersionAsync(
        string archiveFileName,
        string internalFileName,
        CancellationToken cancellationToken = default)
    {
        var reference = DatabaseTextSanitizer.SingleLine(archiveFileName);
        if (string.IsNullOrWhiteSpace(reference))
            throw new ArgumentException("Bitte zuerst eine IWAD- oder Archivdatei auswählen.");
        var archivePath = await ResolveIwadArchivePathAsync(reference, cancellationToken);
        return await IwadVersionDetector.DetectAsync(
            archivePath,
            internalFileName,
            cancellationToken);
    }

    public async Task<LibraryStatisticsData> LoadStatisticsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var played = 0;
        var unplayed = 0;
        var finished = 0;
        var totalMinutes = 0;
        var idGamesDownloads = 0;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT
                    SUM(CASE WHEN COALESCE(g.MinutesPlayed, 0) > 0 THEN 1 ELSE 0 END),
                    SUM(CASE WHEN COALESCE(g.MinutesPlayed, 0) = 0 THEN 1 ELSE 0 END),
                    SUM(CASE WHEN COALESCE(s.Finished, 0) = 1 THEN 1 ELSE 0 END),
                    SUM(COALESCE(g.MinutesPlayed, 0)),
                    COUNT(DISTINCT downloads.GameFileID)
                FROM GameFiles g
                LEFT JOIN WinUI_GameState s ON s.GameFileID = g.GameFileID
                LEFT JOIN WinUI_IdGamesDownloads downloads
                    ON downloads.GameFileID = g.GameFileID;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                played = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                unplayed = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                finished = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                totalMinutes = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
                idGamesDownloads = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
            }
        }

        var byIwad = new List<IwadLibraryStatistic>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT COALESCE(NULLIF(i.Name, ''), i.FileName, 'Not assigned'),
                       COUNT(*),
                       SUM(COALESCE(g.MapCount, 0))
                FROM GameFiles g
                LEFT JOIN IWads i ON i.IWadID = g.IWadID
                GROUP BY COALESCE(NULLIF(i.Name, ''), i.FileName, 'Not assigned')
                ORDER BY COUNT(*) DESC, 1 COLLATE NOCASE;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                byIwad.Add(new IwadLibraryStatistic(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.IsDBNull(2) ? 0 : reader.GetInt32(2)));
            }
        }

        var session = new SessionStatisticsData(0, 0, 0, 0, 0, 0, 0, 0);
        if (await TableExistsAsync(connection, "Stats", cancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                WITH latest AS (
                    SELECT s.*
                    FROM Stats s
                    JOIN (
                        SELECT GameFileID, MapName, MAX(StatID) AS StatID
                        FROM Stats
                        GROUP BY GameFileID, MapName
                    ) newest ON newest.StatID = s.StatID
                )
                SELECT COUNT(*),
                       SUM(COALESCE(KillCount, 0)), SUM(COALESCE(TotalKills, 0)),
                       SUM(COALESCE(SecretCount, 0)), SUM(COALESCE(TotalSecrets, 0)),
                       SUM(COALESCE(ItemCount, 0)), SUM(COALESCE(TotalItems, 0)),
                       COUNT(DISTINCT Skill)
                FROM latest;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                session = new SessionStatisticsData(
                    reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                    reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                    reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                    reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                    reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                    reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                    reader.IsDBNull(7) ? 0 : reader.GetInt32(7));
            }
        }
        return new LibraryStatisticsData(
            played,
            unplayed,
            finished,
            totalMinutes,
            idGamesDownloads,
            byIwad,
            session);
    }

    public async Task<int> BackfillMapMetadataAsync(
        CancellationToken cancellationToken = default)
    {
        const string migrationKey = "MapMetadataV1";
        var databasePath = databaseLocator.FindDatabase();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using (var completed = connection.CreateCommand())
        {
            completed.CommandText =
                "SELECT COUNT(*) FROM WinUI_Migrations WHERE MigrationKey = $key;";
            completed.Parameters.AddWithValue("$key", migrationKey);
            if (Convert.ToInt32(
                    await completed.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture) > 0)
            {
                return 0;
            }
        }

        var values = await LoadConfigurationAsync(connection, cancellationToken);
        var gameFileDirectory = ResolveGameFileDirectory(databasePath, values);
        var entries = new List<(
            int Id,
            string FileName,
            string? Maps,
            int MapCount)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT GameFileID, FileName, Map, COALESCE(MapCount, 0) FROM GameFiles;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                entries.Add((
                    reader.GetInt32(0),
                    reader.GetString(1),
                    GetNullableString(reader, 2),
                    reader.GetInt32(3)));
            }
        }

        var updated = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(gameFileDirectory, entry.FileName);
            IReadOnlyList<string> maps;
            try
            {
                maps = await MapNameExtractor.ExtractAsync(path, cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException
                or InvalidDataException
                or UnauthorizedAccessException
                or NotSupportedException)
            {
                continue;
            }
            var mergedMaps = MapNameExtractor.ParseStored(entry.Maps)
                .Concat(maps)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(map => map, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (mergedMaps.Length == 0)
                continue;
            await UpdateMapMetadataAsync(
                connection,
                entry.Id,
                mergedMaps,
                Math.Max(entry.MapCount, mergedMaps.Length),
                cancellationToken);
            updated++;
        }

        await using (var marker = connection.CreateCommand())
        {
            marker.CommandText =
                """
                INSERT OR REPLACE INTO WinUI_Migrations (MigrationKey, CompletedAt)
                VALUES ($key, $completedAt);
                """;
            marker.Parameters.AddWithValue("$key", migrationKey);
            marker.Parameters.AddWithValue(
                "$completedAt",
                DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
            await marker.ExecuteNonQueryAsync(cancellationToken);
        }
        return updated;
    }

    public async Task<DatabaseHealthReport> CheckDatabaseHealthAsync(
        bool repair,
        CancellationToken cancellationToken = default)
    {
        var databasePath = databaseLocator.FindDatabase();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var integrity = "unknown";
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA integrity_check;";
            integrity = Convert.ToString(
                await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture) ?? "unknown";
        }

        var orphanedFiles = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM Files f LEFT JOIN GameFiles g ON g.GameFileID=f.GameFileID WHERE g.GameFileID IS NULL;",
            cancellationToken);
        var orphanedMappings = await ScalarIntAsync(
            connection,
            """
            SELECT COUNT(*) FROM TagMapping m
            LEFT JOIN GameFiles g ON g.GameFileID=m.FileID
            LEFT JOIN Tags t ON t.TagID=m.TagID
            WHERE g.GameFileID IS NULL OR t.TagID IS NULL;
            """,
            cancellationToken);
        var values = await LoadConfigurationAsync(connection, cancellationToken);
        var gameDirectory = ResolveGameFileDirectory(databasePath, values);
        var missingManagedFiles = 0;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT FileName FROM GameFiles WHERE COALESCE(FileName, '') <> '';";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!File.Exists(Path.Combine(gameDirectory, reader.GetString(0))))
                    missingManagedFiles++;
            }
        }

        var backupPath = string.Empty;
        var messages = new List<string>();
        if (repair)
        {
            var backupDirectory = Path.Combine(
                Path.GetDirectoryName(databasePath)!,
                "Backups");
            Directory.CreateDirectory(backupDirectory);
            backupPath = Path.Combine(
                backupDirectory,
                $"DoomLauncher-before-health-repair-{DateTime.Now:yyyy-MM-dd-HHmmss}.sqlite");
            await using (var backup = new SqliteConnection(
                             new SqliteConnectionStringBuilder
                             {
                                 DataSource = backupPath,
                                 Mode = SqliteOpenMode.ReadWriteCreate,
                             }.ToString()))
            {
                await backup.OpenAsync(cancellationToken);
                connection.BackupDatabase(backup);
            }
            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "DELETE FROM Files WHERE GameFileID NOT IN (SELECT GameFileID FROM GameFiles);";
                await command.ExecuteNonQueryAsync(cancellationToken);
                command.CommandText =
                    """
                    DELETE FROM TagMapping
                    WHERE FileID NOT IN (SELECT GameFileID FROM GameFiles)
                       OR TagID NOT IN (SELECT TagID FROM Tags);
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            messages.Add("Orphaned database relations were removed.");
            messages.Add("Missing content files were reported but not deleted from the database.");
        }
        if (missingManagedFiles > 0)
            messages.Add($"{missingManagedFiles} managed game files are missing on disk.");
        if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
            messages.Add($"SQLite integrity check: {integrity}");

        return new DatabaseHealthReport(
            string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase)
                && orphanedFiles == 0
                && orphanedMappings == 0
                && missingManagedFiles == 0,
            repair,
            integrity,
            orphanedFiles,
            orphanedMappings,
            missingManagedFiles,
            backupPath,
            messages);
    }

    public async Task<DuplicateConsolidationResult> ConsolidateGeneratedNameDuplicatesAsync(
        CancellationToken cancellationToken = default)
    {
        var databasePath = databaseLocator.FindDatabase();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = new List<(int Id, string Reference)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT GameFileID, FileName
                FROM GameFiles
                WHERE COALESCE(FileName, '') <> ''
                ORDER BY GameFileID;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add((reader.GetInt32(0), reader.GetString(1)));
        }
        var generatedGroups = rows
            .Select(row => (
                Row: row,
                BaseName: Path.GetFileName(row.Reference),
                CanonicalName: StripAllGeneratedImportPrefixes(
                    Path.GetFileName(row.Reference))))
            .Where(item => !item.BaseName.Equals(
                item.CanonicalName,
                StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.CanonicalName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var groups = rows
            .Select(row => (
                Row: row,
                BaseName: Path.GetFileName(row.Reference),
                CanonicalName: StripAllGeneratedImportPrefixes(
                    Path.GetFileName(row.Reference))))
            .GroupBy(item => item.CanonicalName, StringComparer.OrdinalIgnoreCase)
            .Where(group => generatedGroups.Any(generated =>
                generated.Key.Equals(group.Key, StringComparison.OrdinalIgnoreCase)))
            .Select(group =>
            {
                var keeper = group.OrderBy(item => item.Row.Id).First();
                return (
                    CanonicalName: group.Key,
                    Keeper: keeper,
                    Duplicates: group
                        .Where(item => item.Row.Id != keeper.Row.Id)
                        .ToArray());
            })
            .ToArray();
        var duplicates = groups
            .SelectMany(group => group.Duplicates.Select(item => (
                Row: item.Row,
                Keeper: group.Keeper.Row)))
            .ToArray();
        if (duplicates.Length == 0 && groups.All(group =>
                group.Keeper.BaseName.Equals(
                    group.CanonicalName,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return new DuplicateConsolidationResult(
                0,
                0,
                new Dictionary<int, int>(),
                [],
                []);
        }

        var physicalFiles = new List<string>();
        foreach (var duplicate in duplicates)
        {
            physicalFiles.AddRange(await CollectGamePhysicalFilesAsync(
                connection,
                databasePath,
                duplicate.Row.Id,
                cancellationToken));
        }
        var configuration = await LoadConfigurationAsync(connection, cancellationToken);
        var managedRoot = ResolveGameFileDirectory(databasePath, configuration);
        var renamePlans = new List<(
            int Id,
            string Reference,
            string CanonicalReference,
            string Source,
            string Destination)>();
        foreach (var group in groups)
        {
            if (group.Keeper.BaseName.Equals(
                    group.CanonicalName,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            var directory = Path.GetDirectoryName(group.Keeper.Row.Reference)
                ?? "Mods";
            var canonicalReference = Path.Combine(directory, group.CanonicalName);
            var source = Path.GetFullPath(Path.Combine(
                managedRoot,
                group.Keeper.Row.Reference));
            var destination = Path.GetFullPath(Path.Combine(
                managedRoot,
                canonicalReference));
            if (!IsPathWithin(source, managedRoot)
                || !IsPathWithin(destination, managedRoot)
                || (!File.Exists(source) && !File.Exists(destination)))
                continue;
            renamePlans.Add((
                group.Keeper.Row.Id,
                group.Keeper.Row.Reference,
                canonicalReference,
                source,
                destination));
        }
        var copiedTargets = new List<string>();
        try
        {
            foreach (var plan in renamePlans)
            {
                if (File.Exists(plan.Destination))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(plan.Destination)!);
                File.Copy(plan.Source, plan.Destination, overwrite: false);
                copiedTargets.Add(plan.Destination);
            }
        }
        catch
        {
            foreach (var target in copiedTargets)
                DeleteIfExists(target);
            throw;
        }
        var mapping = new Dictionary<int, int>();
        var removedNames = new List<string>();
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var duplicate in duplicates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using (var tags = connection.CreateCommand())
            {
                tags.Transaction = transaction;
                tags.CommandText =
                    """
                    INSERT INTO TagMapping (FileID, TagID)
                    SELECT $keeper, TagID
                    FROM TagMapping duplicate
                    WHERE duplicate.FileID=$duplicate
                      AND NOT EXISTS (
                          SELECT 1 FROM TagMapping existing
                          WHERE existing.FileID=$keeper
                            AND existing.TagID=duplicate.TagID
                      );
                    """;
                tags.Parameters.AddWithValue("$keeper", duplicate.Keeper.Id);
                tags.Parameters.AddWithValue("$duplicate", duplicate.Row.Id);
                await tags.ExecuteNonQueryAsync(cancellationToken);
            }
            await DeleteGameDatabaseRowsAsync(
                connection,
                transaction,
                duplicate.Row.Id,
                cancellationToken);
            mapping[duplicate.Row.Id] = duplicate.Keeper.Id;
            removedNames.Add(Path.GetFileName(duplicate.Row.Reference));
        }
        foreach (var plan in renamePlans)
        {
            await using var rename = connection.CreateCommand();
            rename.Transaction = transaction;
            rename.CommandText =
                """
                UPDATE GameFiles
                SET FileName=$fileName, IsSyncNeeded=1
                WHERE GameFileID=$id;
                """;
            rename.Parameters.AddWithValue("$fileName", plan.CanonicalReference);
            rename.Parameters.AddWithValue("$id", plan.Id);
            await rename.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        DeletePhysicalFilesAfterDatabase(physicalFiles, "Duplikat");
        foreach (var plan in renamePlans)
        {
            if (!plan.Source.Equals(
                    plan.Destination,
                    StringComparison.OrdinalIgnoreCase))
                DeleteIfExists(plan.Source);
        }
        return new DuplicateConsolidationResult(
            mapping.Count,
            renamePlans.Count,
            mapping,
            removedNames,
            renamePlans
                .Select(plan =>
                    $"{Path.GetFileName(plan.Reference)} -> " +
                    Path.GetFileName(plan.CanonicalReference))
                .ToArray());
    }

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

    private async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databaseLocator.FindDatabase(),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = true,
            DefaultTimeout = 5,
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await WinUiDatabaseSchema.EnsureAsync(connection, cancellationToken);
        return connection;
    }

    private async Task RefreshMapMetadataAsync(
        int gameFileId,
        CancellationToken cancellationToken)
    {
        var archivePath = await ResolveManagedGameFileAsync(
            gameFileId,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            return;

        var maps = await MapNameExtractor.ExtractAsync(
            archivePath,
            cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        string? storedMapNames = null;
        var storedMapCount = 0;
        await using (var existing = connection.CreateCommand())
        {
            existing.CommandText =
                "SELECT Map, COALESCE(MapCount, 0) FROM GameFiles WHERE GameFileID=$gameFileId;";
            existing.Parameters.AddWithValue("$gameFileId", gameFileId);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                storedMapNames = GetNullableString(reader, 0);
                storedMapCount = reader.GetInt32(1);
            }
        }
        var mergedMaps = MapNameExtractor.ParseStored(storedMapNames)
            .Concat(maps)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(map => map, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (mergedMaps.Length == 0)
            return;
        await UpdateMapMetadataAsync(
            connection,
            gameFileId,
            mergedMaps,
            Math.Max(storedMapCount, mergedMaps.Length),
            cancellationToken);
    }

    private static async Task UpdateMapMetadataAsync(
        SqliteConnection connection,
        int gameFileId,
        IReadOnlyList<string> maps,
        int mapCount,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE GameFiles
            SET Map = $maps,
                MapCount = $mapCount,
                IsSyncNeeded = 1
            WHERE GameFileID = $gameFileId;
            """;
        command.Parameters.AddWithValue(
            "$maps",
            maps.Count == 0 ? DBNull.Value : string.Join(", ", maps));
        command.Parameters.AddWithValue("$mapCount", mapCount);
        command.Parameters.AddWithValue("$gameFileId", gameFileId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<NativeChoice>> LoadChoicesAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        var result = new List<NativeChoice>
        {
            new(null, "Nicht festgelegt"),
        };
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new NativeChoice(reader.GetInt32(0), reader.GetString(1)));
        return result;
    }

    private static async Task<Dictionary<string, string>> LoadConfigurationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Name, Value FROM Configuration;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result[reader.GetString(0)] = GetNullableString(reader, 1) ?? string.Empty;
        return result;
    }

    private static async Task UpsertConfigurationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string name,
        string value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE Configuration
            SET Value = $value
            WHERE Name = $name;
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$value", value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) > 0)
            return;

        command.CommandText =
            """
            INSERT INTO Configuration
                (Name, Value, AvailableValues, UserCanModify)
            VALUES
                ($name, $value, '', 1);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string FindAvailableFileName(string directory, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidate = fileName;
        for (var suffix = 2; File.Exists(Path.Combine(directory, candidate)); suffix++)
            candidate = $"{stem} ({suffix}){extension}";
        return candidate;
    }

    private static string? StripGeneratedImportPrefix(string fileName)
    {
        var segments = fileName.Split('-', 3);
        if (segments.Length >= 3
            && segments[0].Equals("DoomLauncher", StringComparison.OrdinalIgnoreCase)
            && IsGeneratedHexToken(segments[1]))
        {
            return segments[2];
        }
        var separator = fileName.IndexOf('-');
        if (separator > 0
            && IsGeneratedHexToken(fileName[..separator]))
        {
            return fileName[(separator + 1)..];
        }
        return null;
    }

    private static string StripAllGeneratedImportPrefixes(string fileName)
    {
        var current = fileName;
        while (StripGeneratedImportPrefix(current) is { } stripped
               && !stripped.Equals(current, StringComparison.OrdinalIgnoreCase))
        {
            current = stripped;
        }
        return current;
    }

    private static bool IsGeneratedHexToken(string value) =>
        value.Length is 32 or 64
        && value.All(character =>
            character is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F');

    private async Task<string> ResolveIwadArchivePathAsync(
        string archiveReference,
        CancellationToken cancellationToken)
    {
        var databasePath = databaseLocator.FindDatabase();
        var databaseDirectory = Path.GetDirectoryName(databasePath)!;
        var expanded = Environment.ExpandEnvironmentVariables(archiveReference);
        var direct = Path.IsPathFullyQualified(expanded)
            ? Path.GetFullPath(expanded)
            : Path.GetFullPath(Path.Combine(databaseDirectory, expanded));
        if (File.Exists(direct))
            return direct;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var configuration = await LoadConfigurationAsync(connection, cancellationToken);
        var managed = Path.Combine(
            ResolveGameFileDirectory(databasePath, configuration),
            expanded.TrimStart('\\', '/'));
        if (File.Exists(managed))
            return managed;

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT FileName
            FROM GameFiles
            WHERE FileName=$file COLLATE NOCASE
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$file", Path.GetFileName(expanded));
        var storedName = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(storedName))
        {
            var storedPath = Path.Combine(
                ResolveGameFileDirectory(databasePath, configuration),
                storedName);
            if (File.Exists(storedPath))
                return storedPath;
        }

        throw new FileNotFoundException(
            "Die konfigurierte IWAD- oder Archivdatei wurde weder relativ zum " +
            "Programmverzeichnis noch im verwalteten Spieleverzeichnis gefunden.",
            archiveReference);
    }

    private static string ResolveGameFileDirectory(
        string databasePath,
        IReadOnlyDictionary<string, string> values)
    {
        var configured = Environment.ExpandEnvironmentVariables(
            values.GetValueOrDefault("GameFileDirectory", "Data\\"));
        return Path.GetFullPath(
            Path.IsPathFullyQualified(configured)
                ? configured
                : Path.Combine(Path.GetDirectoryName(databasePath)!, configured));
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static int? ParseNullableInt(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool ParseBool(string? value, bool defaultValue)
    {
        return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private static object DbValue(object? value) => value ?? DBNull.Value;

    private static string? GetNullableString(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static int? GetNullableInt32(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }
}
