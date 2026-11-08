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

}
