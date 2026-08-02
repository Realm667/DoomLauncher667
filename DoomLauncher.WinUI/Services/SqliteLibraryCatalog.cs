using System.Globalization;
using System.Drawing;
using Microsoft.Data.Sqlite;

namespace DoomLauncher.WinUI.Services;

public sealed class SqliteLibraryCatalog(
    IDoomLauncherDatabaseLocator databaseLocator,
    UiLocalization localization) : ILibraryCatalog
{
    private const int ThumbnailFileType = 4;
    private const int ScreenshotFileType = 1;

    public async Task<LibraryCatalogResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        var databasePath = databaseLocator.FindDatabase();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = true,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await WinUiDatabaseSchema.EnsureAsync(connection, cancellationToken);

        var gameFileDirectory = await GetGameFileDirectoryAsync(
            connection,
            Path.GetDirectoryName(databasePath)!,
            cancellationToken);
        var pageSize = await GetPageSizeAsync(connection, cancellationToken);
        var homeItemsPerGroup = await GetHomeItemsPerGroupAsync(
            connection,
            cancellationToken);
        var placeholderArtworkStyle =
            await GetPlaceholderArtworkStyleAsync(
                connection,
                cancellationToken);
        var configuredIwads = await GetConfiguredIwadsAsync(
            connection,
            cancellationToken);
        var collectionNames = await GetCollectionNamesAsync(
            connection,
            cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                game.GameFileID,
                game.FileName,
                NULLIF(TRIM(game.Title), '') AS Title,
                NULLIF(TRIM(game.Author), '') AS Author,
                game.ReleaseDate,
                NULLIF(TRIM(game.Map), '') AS Maps,
                COALESCE(game.MapCount, 0) AS MapCount,
                COALESCE(game.Rating, 0) AS Rating,
                NULLIF(TRIM(game.Description), '') AS Description,
                COALESCE(game.MinutesPlayed, 0) AS MinutesPlayed,
                game.LastPlayed,
                game.Downloaded,
                COALESCE(winState.Finished, 0) AS IsFinished,
                CASE WHEN idgames.GameFileID IS NULL
                          AND idgamesMetadata.GameFileID IS NULL
                     THEN 0 ELSE 1 END AS IsIdGamesDownload,
                COALESCE(idgames.IdGamesID, idgamesMetadata.IdGamesID) AS IdGamesID,
                (
                    SELECT GROUP_CONCAT(tag.Name, '|')
                    FROM TagMapping mapping
                    JOIN Tags tag ON tag.TagID = mapping.TagID
                    WHERE mapping.FileID = game.GameFileID
                ) AS TagNames,
                NULLIF(TRIM(sourcePort.Name), '') AS SourcePort,
                NULLIF(TRIM(baseIwad.FileName), '') AS BaseIwadFileName,
                NULLIF(TRIM(ownIwad.FileName), '') AS OwnIwadFileName,
                COALESCE((
                    SELECT titlePic.FileName
                    FROM Files titlePic
                    WHERE titlePic.GameFileID = game.GameFileID
                      AND titlePic.FileTypeID = 6
                    ORDER BY COALESCE(titlePic.FileOrder, 0), titlePic.FileID
                    LIMIT 1
                ), (
                    SELECT thumbnail.FileName
                    FROM Files thumbnail
                    WHERE thumbnail.GameFileID = game.GameFileID
                      AND thumbnail.FileTypeID = $thumbnailFileType
                    ORDER BY COALESCE(thumbnail.FileOrder, 0), thumbnail.FileID
                    LIMIT 1
                )) AS ThumbnailFileName,
                COALESCE((
                    SELECT titlePic.FileName
                    FROM Files titlePic
                    WHERE titlePic.GameFileID = game.GameFileID
                      AND titlePic.FileTypeID = 6
                    ORDER BY COALESCE(titlePic.FileOrder, 0), titlePic.FileID
                    LIMIT 1
                ), (
                    SELECT original.FileName
                    FROM Files thumbnail
                    JOIN Files original
                      ON original.FileID = thumbnail.DerivedFromFileID
                    WHERE thumbnail.GameFileID = game.GameFileID
                      AND thumbnail.FileTypeID = $thumbnailFileType
                    ORDER BY COALESCE(thumbnail.FileOrder, 0), thumbnail.FileID
                    LIMIT 1
                )) AS DetailArtworkFileName,
                COALESCE((
                    SELECT titlePic.FileTypeID
                    FROM Files titlePic
                    WHERE titlePic.GameFileID = game.GameFileID
                      AND titlePic.FileTypeID = 6
                    ORDER BY COALESCE(titlePic.FileOrder, 0), titlePic.FileID
                    LIMIT 1
                ), (
                    SELECT original.FileTypeID
                    FROM Files thumbnail
                    JOIN Files original
                      ON original.FileID = thumbnail.DerivedFromFileID
                    WHERE thumbnail.GameFileID = game.GameFileID
                      AND thumbnail.FileTypeID = $thumbnailFileType
                    ORDER BY COALESCE(thumbnail.FileOrder, 0), thumbnail.FileID
                    LIMIT 1
                )) AS DetailArtworkFileType,
                (
                    SELECT GROUP_CONCAT(screenshot.FileName, '|')
                    FROM Files screenshot
                    WHERE screenshot.GameFileID = game.GameFileID
                      AND screenshot.FileTypeID = $screenshotFileType
                      AND NOT EXISTS (
                          SELECT 1
                          FROM Files thumbnail
                          WHERE thumbnail.GameFileID = game.GameFileID
                            AND thumbnail.FileTypeID = $thumbnailFileType
                            AND thumbnail.DerivedFromFileID = screenshot.FileID
                      )
                    ORDER BY COALESCE(screenshot.FileOrder, 0), screenshot.FileID
                ) AS ScreenshotFileNames
            FROM GameFiles game
            LEFT JOIN SourcePorts sourcePort
                ON sourcePort.SourcePortID = game.SourcePortID
            LEFT JOIN IWads baseIwad
                ON baseIwad.IWadID = game.IWadID
            LEFT JOIN IWads ownIwad
                ON ownIwad.GameFileID = game.GameFileID
            LEFT JOIN WinUI_GameState winState
                ON winState.GameFileID = game.GameFileID
            LEFT JOIN WinUI_IdGamesDownloads idgames
                ON idgames.GameFileID = game.GameFileID
            LEFT JOIN WinUI_IdGamesMetadata idgamesMetadata
                ON idgamesMetadata.GameFileID = game.GameFileID
            ORDER BY COALESCE(NULLIF(TRIM(game.Title), ''), game.FileName) COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$thumbnailFileType", ThumbnailFileType);
        command.Parameters.AddWithValue("$screenshotFileType", ScreenshotFileType);

        var entries = new List<LibraryCatalogEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var fileName = GetString(reader, "FileName");
            string? ownIwad = DatabaseTextSanitizer.SingleLine(
                GetNullableString(reader, "OwnIwadFileName"));
            string? baseIwad = DatabaseTextSanitizer.SingleLine(
                GetNullableString(reader, "BaseIwadFileName"));
            ownIwad = ownIwad.Length == 0 ? null : ownIwad;
            baseIwad = baseIwad.Length == 0 ? null : baseIwad;
            var iwad = ownIwad ?? baseIwad ?? localization.Get("NotSet");
            var title = DatabaseTextSanitizer.SingleLine(
                GetNullableString(reader, "Title"));
            if (title.Length == 0)
                title = Path.GetFileNameWithoutExtension(fileName);
            var thumbnail = GetNullableString(reader, "ThumbnailFileName");
            var artwork = ResolveArtwork(
                gameFileDirectory,
                Path.GetDirectoryName(databasePath)!,
                thumbnail,
                iwad,
                title,
                placeholderArtworkStyle);
            var detailArtworkFileName =
                GetNullableString(reader, "DetailArtworkFileName");
            var detailArtworkFileType =
                GetNullableInt32(reader, "DetailArtworkFileType");
            var detailArtwork = ResolveDetailArtwork(
                gameFileDirectory,
                detailArtworkFileName,
                detailArtworkFileType,
                artwork);
            var usesDoomPixelAspect = UsesDoomPixelAspect(
                gameFileDirectory,
                detailArtworkFileName,
                detailArtworkFileType);
            var releaseDateAt = ParseNullableDate(reader, "ReleaseDate");
            var downloadedAt = ParseNullableDate(reader, "Downloaded");
            var mapCount = GetInt32(reader, "MapCount");
            var ratingValue = GetDouble(reader, "Rating");

            var minutesPlayed = GetInt32(reader, "MinutesPlayed");
            var lastPlayedAt = ParseNullableDate(reader, "LastPlayed");
            entries.Add(new LibraryCatalogEntry(
                reader.GetInt32(reader.GetOrdinal("GameFileID")),
                fileName,
                title,
                DatabaseTextSanitizer.SingleLine(GetNullableString(reader, "Author"))
                    is { Length: > 0 } author
                    ? author
                    : Path.GetFileName(fileName),
                ownIwad is null ? "Mod" : "IWAD",
                FormatYear(reader, "ReleaseDate"),
                FormatReleaseDate(reader, "ReleaseDate"),
                FormatMaps(reader),
                FormatRating(reader),
                !string.IsNullOrWhiteSpace(GetNullableString(reader, "Downloaded"))
                    ? localization.Get("Yes")
                    : localization.Get("No"),
                artwork,
                detailArtwork,
                usesDoomPixelAspect,
                ResolveScreenshots(
                    gameFileDirectory,
                    GetNullableString(reader, "ScreenshotFileNames")),
                DatabaseTextSanitizer.Multiline(GetNullableString(reader, "Description"))
                    is { Length: > 0 } description
                    ? description
                    : localization.Get("NoDescription"),
                DatabaseTextSanitizer.SingleLine(GetNullableString(reader, "SourcePort"))
                    is { Length: > 0 } sourcePort
                    ? sourcePort
                    : localization.Get("NotSet"),
                iwad,
                FormatPlaytime(minutesPlayed),
                FormatLastPlayed(lastPlayedAt),
                minutesPlayed,
                lastPlayedAt,
                releaseDateAt,
                downloadedAt,
                mapCount,
                ratingValue,
                GetNullableInt32(reader, "IdGamesID"),
                GetInt32(reader, "IsFinished") != 0,
                GetInt32(reader, "IsIdGamesDownload") != 0,
                !string.IsNullOrWhiteSpace(GetNullableString(reader, "Downloaded")),
                SplitTags(GetNullableString(reader, "TagNames"))));
        }

        return new LibraryCatalogResult(
            entries,
            databasePath,
            pageSize,
            homeItemsPerGroup,
            configuredIwads,
            collectionNames);
    }

    private static async Task<int> GetHomeItemsPerGroupAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Value
            FROM Configuration
            WHERE Name = 'HomeItemsPerGroup'
            LIMIT 1;
            """;
        var rawValue = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        return int.TryParse(
            rawValue,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? Math.Clamp(value, 1, 20)
            : 10;
    }

    private static async Task<string> GetPlaceholderArtworkStyleAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Value
            FROM Configuration
            WHERE Name = 'PlaceholderArtworkStyle'
            LIMIT 1;
            """;
        var value = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        return string.Equals(
            value,
            "Colored",
            StringComparison.OrdinalIgnoreCase)
            ? "colored"
            : "grayscale";
    }

    private static async Task<IReadOnlySet<string>> GetConfiguredIwadsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT FileName
            FROM IWads
            WHERE NULLIF(TRIM(FileName), '') IS NOT NULL;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(DatabaseTextSanitizer.SingleLine(reader.GetString(0)));
        return result;
    }

    private static async Task<IReadOnlySet<string>> GetCollectionNamesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Name
            FROM Tags
            WHERE NULLIF(TRIM(Name), '') IS NOT NULL
            ORDER BY Name COLLATE NOCASE;
            """;
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = DatabaseTextSanitizer.SingleLine(reader.GetString(0));
            if (name.Length > 0)
                result.Add(name);
        }
        return result;
    }

    private static async Task<int> GetPageSizeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Value
            FROM Configuration
            WHERE Name = 'ItemsPerPage'
            LIMIT 1;
            """;
        var rawValue = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        return int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 20, 250)
            : 60;
    }

    private static async Task<string> GetGameFileDirectoryAsync(
        SqliteConnection connection,
        string dataDirectory,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Value
            FROM Configuration
            WHERE Name = 'GameFileDirectory'
            LIMIT 1;
            """;

        var configuredValue = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(configuredValue))
            configuredValue = "Data";

        configuredValue = Environment.ExpandEnvironmentVariables(configuredValue);
        return Path.GetFullPath(
            Path.IsPathFullyQualified(configuredValue)
                ? configuredValue
                : Path.Combine(dataDirectory, configuredValue));
    }

    private static string ResolveArtwork(
        string gameFileDirectory,
        string portableRoot,
        string? thumbnailFileName,
        string iwad,
        string title,
        string placeholderArtworkStyle)
    {
        if (!string.IsNullOrWhiteSpace(thumbnailFileName))
        {
            if (Uri.TryCreate(thumbnailFileName, UriKind.Absolute, out var remoteUri)
                && remoteUri.Scheme is "http" or "https")
            {
                return remoteUri.AbsoluteUri;
            }

            foreach (var directory in new[] { "TitlePics", "Thumbnails" })
            {
                var artworkPath = Path.Combine(
                    gameFileDirectory,
                    directory,
                    thumbnailFileName);
                if (File.Exists(artworkPath))
                    return new Uri(artworkPath).AbsoluteUri;
            }
        }

        var lookupValue = $"{iwad} {title}".ToUpperInvariant();
        var fallbackFile = lookupValue switch
        {
            var value when value.Contains("PLUTONIA") => "plutonia.png",
            var value when value.Contains("TNT") => "tnt.png",
            var value when value.Contains("HERETIC") => "heretic.png",
            var value when value.Contains("HEXEN") => "hexen.png",
            var value when value.Contains("STRIFE") => "strife.png",
            var value when value.Contains("DOOM64") || value.Contains("DOOM 64") => "doom64.png",
            var value when value.Contains("CHEX") => "chexquest.png",
            var value when value.Contains("ULTIMATE DOOM")
                || value.Contains("DOOM.WAD")
                || value.Contains("DOOM1.WAD")
                || value.Contains("DOOM 1")
                || value.Contains("DOOM SHAREWARE")
                || value.Contains("DOOM (SHAREWARE)") => "doom.png",
            _ => "doom2.png",
        };

        var portablePlaceholder = Path.Combine(
            portableRoot,
            "Data",
            "TileImages",
            placeholderArtworkStyle,
            fallbackFile);
        if (File.Exists(portablePlaceholder))
            return new Uri(portablePlaceholder).AbsoluteUri;

        return $"ms-appx:///Assets/Library/{placeholderArtworkStyle}/{fallbackFile}";
    }

    private static string ResolveDetailArtwork(
        string gameFileDirectory,
        string? fileName,
        int? fileType,
        string fallbackArtwork)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return fallbackArtwork;

        var directory = fileType switch
        {
            1 => "Screenshots",
            6 => "TitlePics",
            _ => null,
        };
        if (directory is null)
            return fallbackArtwork;

        var path = Path.Combine(gameFileDirectory, directory, fileName);
        return File.Exists(path)
            ? new Uri(path).AbsoluteUri
            : fallbackArtwork;
    }

    private static bool UsesDoomPixelAspect(
        string gameFileDirectory,
        string? fileName,
        int? fileType)
    {
        if (fileType != 6 || string.IsNullOrWhiteSpace(fileName))
            return false;
        var path = Path.Combine(gameFileDirectory, "TitlePics", fileName);
        if (!File.Exists(path))
            return false;
        try
        {
            using var image = Image.FromFile(path);
            return image.Width == 320 && image.Height == 200;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or OutOfMemoryException
            or IOException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> ResolveScreenshots(
        string gameFileDirectory,
        string? fileNames)
    {
        if (string.IsNullOrWhiteSpace(fileNames))
            return [];

        return fileNames
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(fileName => Path.Combine(gameFileDirectory, "Screenshots", fileName))
            .Where(File.Exists)
            .Select(path => new Uri(path).AbsoluteUri)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string FormatYear(SqliteDataReader reader, string column)
    {
        var rawValue = GetNullableString(reader, column);
        return TryParseDate(rawValue, out var date) ? date.Year.ToString(CultureInfo.InvariantCulture) : "—";
    }

    private static string FormatReleaseDate(SqliteDataReader reader, string column)
    {
        var rawValue = GetNullableString(reader, column);
        return TryParseDate(rawValue, out var date)
            ? date.ToString("d", CultureInfo.CurrentCulture)
            : "—";
    }

    private static string FormatMaps(SqliteDataReader reader)
    {
        var mapCount = GetInt32(reader, "MapCount");
        if (mapCount > 0)
            return mapCount.ToString(CultureInfo.CurrentCulture);
        return GetNullableString(reader, "Maps") ?? "—";
    }

    private static string FormatRating(SqliteDataReader reader)
    {
        var rating = GetInt32(reader, "Rating");
        return rating > 0 ? $"{rating}/5" : "—";
    }

    private string FormatPlaytime(int minutes)
    {
        if (minutes <= 0)
            return localization.Get("UnplayedValue");
        if (minutes < 60)
            return $"{minutes} min";

        var hours = minutes / 60d;
        return $"{hours:0.#} h";
    }

    private string FormatLastPlayed(DateTime? lastPlayedAt)
    {
        if (!lastPlayedAt.HasValue)
            return localization.Get("Never");

        var date = lastPlayedAt.Value;
        var elapsed = DateTime.Now - date.ToLocalTime();
        if (elapsed.TotalDays < 1 && elapsed.TotalDays >= 0)
            return localization.Get("Today");
        if (elapsed.TotalDays < 2 && elapsed.TotalDays >= 0)
            return localization.Get("Yesterday");
        if (elapsed.TotalDays < 14 && elapsed.TotalDays >= 0)
            return localization.Format("DaysAgo", (int)elapsed.TotalDays);

        return date.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
    }

    private static bool TryParseDate(string? value, out DateTime date)
    {
        return DateTime.TryParse(
                   value,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeLocal,
                   out date)
               || DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out date);
    }

    private static DateTime? ParseNullableDate(SqliteDataReader reader, string column)
    {
        var rawValue = GetNullableString(reader, column);
        return TryParseDate(rawValue, out var date) ? date : null;
    }

    private static int GetInt32(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal)
            ? 0
            : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static int? GetNullableInt32(
        SqliteDataReader reader,
        string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal)
            ? null
            : Convert.ToInt32(
                reader.GetValue(ordinal),
                CultureInfo.InvariantCulture);
    }

    private static double GetDouble(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal)
            ? 0
            : Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<string> SplitTags(string? tags)
    {
        return string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split(
                    '|',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(DatabaseTextSanitizer.SingleLine)
                .Where(tag => tag.Length > 0)
                .ToArray();
    }

    private static string GetString(SqliteDataReader reader, string column)
    {
        return reader.GetString(reader.GetOrdinal(column));
    }

    private static string? GetNullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.CurrentCulture);
    }
}
