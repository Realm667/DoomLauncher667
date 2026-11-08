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

}
