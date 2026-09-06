using Microsoft.Data.Sqlite;

namespace DoomLauncher.WinUI.Services;

internal static class WinUiDatabaseSchema
{
    public static async Task EnsureAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken,
        bool force = false)
    {
        // A persisted revision avoids repeating all compatibility DDL and data
        // repairs on every library read. A savepoint also works inside callers'
        // transactions and rolls back DDL together with the migrated data.
        await using var revision = connection.CreateCommand();
        revision.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='WinUI_SchemaRevision';";
        if (Convert.ToInt32(await revision.ExecuteScalarAsync(cancellationToken)) != 0)
        {
            revision.CommandText = "SELECT COALESCE(MAX(Revision), 0) FROM WinUI_SchemaRevision;";
            if (Convert.ToInt32(await revision.ExecuteScalarAsync(cancellationToken)) >= 1 && !force)
                return;
        }

        if (File.Exists(connection.DataSource))
        {
            var backupDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(connection.DataSource))!, "Backups");
            Directory.CreateDirectory(backupDirectory);
            var backupPath = Path.Combine(backupDirectory, $"schema-before-0.9.3-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.sqlite");
            await using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = backupPath, Pooling = false,
            }.ToString());
            await backup.OpenAsync(cancellationToken);
            connection.BackupDatabase(backup);
        }

        revision.CommandText = "SAVEPOINT winui_schema;";
        await revision.ExecuteNonQueryAsync(cancellationToken);
        try
        {
            await EnsureCoreAsync(connection, cancellationToken);
            foreach (var (table, columns, name) in new[]
            {
                ("Files", new[] { "GameFileID", "FileTypeID" }, "IX_WinUI_Files_GameType"),
                ("TagMapping", new[] { "FileID", "TagID" }, "IX_WinUI_TagMapping_File"),
                ("IWads", new[] { "GameFileID" }, "IX_WinUI_IWads_Game"),
            })
            {
                var existing = await ReadColumnsAsync(connection, table, cancellationToken);
                if (columns.All(existing.Contains))
                {
                    revision.CommandText = $"CREATE INDEX IF NOT EXISTS {name} ON {table}({string.Join(',', columns)});";
                    await revision.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            revision.CommandText = "CREATE TABLE IF NOT EXISTS WinUI_SchemaRevision (Revision INTEGER NOT NULL PRIMARY KEY); INSERT OR IGNORE INTO WinUI_SchemaRevision VALUES (1); RELEASE winui_schema;";
            await revision.ExecuteNonQueryAsync(cancellationToken);
        }
        catch
        {
            revision.CommandText = "ROLLBACK TO winui_schema; RELEASE winui_schema;";
            await revision.ExecuteNonQueryAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task EnsureCoreAsync(
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
