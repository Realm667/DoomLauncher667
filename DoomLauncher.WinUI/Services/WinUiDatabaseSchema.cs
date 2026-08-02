using Microsoft.Data.Sqlite;

namespace DoomLauncher.WinUI.Services;

internal static class WinUiDatabaseSchema
{
    public static async Task EnsureAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await EnsureLegacyCompatibilityAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS WinUI_GameState (
                GameFileID INTEGER NOT NULL PRIMARY KEY,
                Finished INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (GameFileID) REFERENCES GameFiles(GameFileID) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS WinUI_IdGamesDownloads (
                GameFileID INTEGER NOT NULL PRIMARY KEY,
                IdGamesID INTEGER NOT NULL,
                ArchiveDirectory TEXT NULL,
                DownloadedAt TEXT NOT NULL,
                FOREIGN KEY (GameFileID) REFERENCES GameFiles(GameFileID) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS IX_WinUI_IdGamesDownloads_IdGamesID
                ON WinUI_IdGamesDownloads(IdGamesID);

            CREATE TABLE IF NOT EXISTS WinUI_IdGamesMetadata (
                GameFileID INTEGER NOT NULL PRIMARY KEY,
                IdGamesID INTEGER NOT NULL,
                ArchiveDirectory TEXT NULL,
                ScrapedAt TEXT NOT NULL,
                FOREIGN KEY (GameFileID) REFERENCES GameFiles(GameFileID) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS WinUI_Migrations (
                MigrationKey TEXT NOT NULL PRIMARY KEY,
                CompletedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS WinUI_SourcePortCapabilities (
                SourcePortID INTEGER NOT NULL PRIMARY KEY,
                Version TEXT NOT NULL DEFAULT '',
                ScreenshotSupport TEXT NOT NULL DEFAULT 'Auto',
                ScreenshotDirectories TEXT NOT NULL DEFAULT '',
                ScreenshotExtensions TEXT NOT NULL DEFAULT '.png,.jpg,.jpeg,.bmp',
                ScreenshotArgument TEXT NOT NULL DEFAULT '',
                StatisticsAdapter TEXT NOT NULL DEFAULT 'None',
                StatisticsDirectories TEXT NOT NULL DEFAULT '',
                SaveGameExtensions TEXT NOT NULL DEFAULT '.zds',
                FOREIGN KEY (SourcePortID) REFERENCES SourcePorts(SourcePortID)
                    ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS WinUI_IwadMetadata (
                IWadID INTEGER NOT NULL PRIMARY KEY,
                Version TEXT NOT NULL DEFAULT '',
                Md5 TEXT NOT NULL DEFAULT '',
                FileSize INTEGER NOT NULL DEFAULT 0,
                CatalogLabel TEXT NOT NULL DEFAULT '',
                DetectedAt TEXT NULL,
                FOREIGN KEY (IWadID) REFERENCES IWads(IWadID)
                    ON DELETE CASCADE
            );

            INSERT OR IGNORE INTO WinUI_SourcePortCapabilities
                (SourcePortID, StatisticsAdapter)
            SELECT SourcePortID,
                   CASE
                       WHEN Name LIKE '%GZDoom%'
                         OR Name LIKE '%UZDoom%'
                         OR Name LIKE '%ZDoom%'
                         OR Name LIKE '%VKDoom%'
                         OR Name LIKE '%LZDoom%'
                         OR Name LIKE '%QZDoom%'
                         OR Name LIKE '%Zandronum%'
                         OR Name LIKE '%Skulltag%'
                       THEN 'ZDoomSave'
                       ELSE 'None'
                   END
            FROM SourcePorts;

            CREATE INDEX IF NOT EXISTS IX_WinUI_IdGamesMetadata_IdGamesID
                ON WinUI_IdGamesMetadata(IdGamesID);

            CREATE TABLE IF NOT EXISTS Stats (
                StatID INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                GameFileID INTEGER NOT NULL,
                KillCount INTEGER NOT NULL,
                TotalKills INTEGER NOT NULL,
                SecretCount INTEGER NOT NULL,
                TotalSecrets INTEGER NOT NULL,
                LevelTime REAL NOT NULL,
                ItemCount INTEGER NOT NULL,
                TotalItems INTEGER NOT NULL,
                SourcePortID INTEGER NOT NULL,
                MapName TEXT NOT NULL,
                RecordTime TEXT NOT NULL,
                Skill INTEGER NULL
            );

            INSERT INTO WinUI_GameState (GameFileID, Finished)
            SELECT DISTINCT mapping.FileID, 1
            FROM TagMapping mapping
            JOIN Tags tag ON tag.TagID = mapping.TagID
            JOIN GameFiles game ON game.GameFileID = mapping.FileID
            WHERE TRIM(tag.Name) = 'Finished' COLLATE NOCASE
            ON CONFLICT(GameFileID) DO UPDATE SET Finished = 1;

            DELETE FROM TagMapping
            WHERE TagID IN (
                SELECT TagID FROM Tags
                WHERE TRIM(Name) = 'Finished' COLLATE NOCASE
            );

            DELETE FROM Tags
            WHERE TRIM(Name) = 'Finished' COLLATE NOCASE;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureLegacyCompatibilityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var fileColumns = await ReadColumnsAsync(
            connection,
            "Files",
            cancellationToken);
        if (fileColumns.Count > 0)
        {
            await AddColumnAsync(
                connection,
                fileColumns,
                "Files",
                "DerivedFromFileID",
                "INTEGER NULL",
                cancellationToken);
            await AddColumnAsync(
                connection,
                fileColumns,
                "Files",
                "FileOrder",
                "INTEGER NOT NULL DEFAULT 0",
                cancellationToken);
            await AddColumnAsync(
                connection,
                fileColumns,
                "Files",
                "IsMain",
                "INTEGER NOT NULL DEFAULT 0",
                cancellationToken);
            await AddColumnAsync(
                connection,
                fileColumns,
                "Files",
                "OriginalFileName",
                "TEXT NULL",
                cancellationToken);
            await AddColumnAsync(
                connection,
                fileColumns,
                "Files",
                "OriginalFilePath",
                "TEXT NULL",
                cancellationToken);

            // DoomLauncher 3.7.9 rebuilt Files without DerivedFromFileID but left
            // thumbnail relationships temporarily stored in SourcePortID.
            if (fileColumns.Contains("SourcePortID"))
            {
                await using var repair = connection.CreateCommand();
                repair.CommandText =
                    """
                    UPDATE Files
                    SET DerivedFromFileID = SourcePortID
                    WHERE FileTypeID = 4
                      AND DerivedFromFileID IS NULL
                      AND SourcePortID IS NOT NULL;
                    """;
                await repair.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        var gameColumns = await ReadColumnsAsync(
            connection,
            "GameFiles",
            cancellationToken);
        if (gameColumns.Count > 0)
        {
            await AddColumnAsync(
                connection,
                gameColumns,
                "GameFiles",
                "MapCount",
                "INTEGER NOT NULL DEFAULT 0",
                cancellationToken);
            await AddColumnAsync(
                connection,
                gameColumns,
                "GameFiles",
                "IsSyncNeeded",
                "INTEGER NOT NULL DEFAULT 0",
                cancellationToken);
        }
    }

    private static async Task<HashSet<string>> ReadColumnsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info([{table}]);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            columns.Add(reader.GetString(1));
        return columns;
    }

    private static async Task AddColumnAsync(
        SqliteConnection connection,
        ISet<string> columns,
        string table,
        string column,
        string declaration,
        CancellationToken cancellationToken)
    {
        if (columns.Contains(column))
            return;
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"ALTER TABLE [{table}] ADD COLUMN [{column}] {declaration};";
        await command.ExecuteNonQueryAsync(cancellationToken);
        columns.Add(column);
    }
}
