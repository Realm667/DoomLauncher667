using Microsoft.Data.Sqlite;

namespace DoomLauncher.WinUI.Services;

public interface ILegacyInstallationMigrationService
{
    bool DatabaseExists();

    Task<MigrationResult> MigrateAsync(
        string legacyDirectory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record MigrationResult(
    string DatabasePath,
    int CopiedFiles,
    string? BackupPath);

public sealed class LegacyInstallationMigrationService(
    IDoomLauncherDatabaseLocator databaseLocator)
    : ILegacyInstallationMigrationService
{
    public bool DatabaseExists()
    {
        try
        {
            return File.Exists(databaseLocator.FindDatabase());
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    public async Task<MigrationResult> MigrateAsync(
        string legacyDirectory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        legacyDirectory = Path.GetFullPath(legacyDirectory);
        var sourceDatabase = Path.Combine(
            legacyDirectory,
            DoomLauncherDatabaseLocator.DatabaseFileName);
        if (!File.Exists(sourceDatabase))
            throw new FileNotFoundException(
                "Im ausgewählten Ordner wurde keine DoomLauncher.sqlite gefunden.",
                sourceDatabase);

        var destinationDatabase = GetDestinationDatabasePath();
        var destinationRoot = Path.GetDirectoryName(destinationDatabase)!;
        Directory.CreateDirectory(destinationRoot);
        var backupPath = await BackupDestinationAsync(
            destinationDatabase,
            destinationRoot,
            cancellationToken);

        var sourceGameDirectory = await ReadConfiguredPathAsync(
            sourceDatabase,
            legacyDirectory,
            "GameFileDirectory",
            "GameFiles",
            cancellationToken);
        var destinationGameDirectory = Path.Combine(destinationRoot, "Data");
        var sourceTileDirectory = Path.Combine(legacyDirectory, "TileImages");
        var destinationTileDirectory = Path.Combine(
            destinationRoot,
            "Data",
            "TileImages");
        var files = EnumerateMigrationFiles(sourceGameDirectory, sourceTileDirectory).ToArray();
        var copied = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = file.StartsWith(
                sourceGameDirectory,
                StringComparison.OrdinalIgnoreCase)
                ? Path.GetRelativePath(sourceGameDirectory, file)
                : Path.GetRelativePath(sourceTileDirectory, file);
            var targetRoot = file.StartsWith(
                sourceGameDirectory,
                StringComparison.OrdinalIgnoreCase)
                ? destinationGameDirectory
                : destinationTileDirectory;
            if (targetRoot.Equals(
                    destinationGameDirectory,
                    StringComparison.OrdinalIgnoreCase)
                && !IsManagedSubdirectory(relative))
            {
                relative = Path.Combine("Mods", relative);
            }
            var target = Path.Combine(targetRoot, relative);
            if (Path.GetFullPath(file).Equals(
                    Path.GetFullPath(target),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            copied++;
            progress?.Report(files.Length == 0 ? 70 : copied * 70d / files.Length);
        }

        var temporaryDatabase = destinationDatabase + $".migrating-{Guid.NewGuid():N}";
        try
        {
            File.Copy(sourceDatabase, temporaryDatabase, overwrite: false);
            await RewritePathsAsync(temporaryDatabase, cancellationToken);
            await VerifyIntegrityAsync(temporaryDatabase, cancellationToken);
            SqliteConnection.ClearAllPools();
            File.Move(temporaryDatabase, destinationDatabase, overwrite: true);
            Environment.SetEnvironmentVariable(
                DoomLauncherDatabaseLocator.DatabaseEnvironmentVariable,
                destinationDatabase);
            progress?.Report(100);
            return new MigrationResult(destinationDatabase, copied, backupPath);
        }
        finally
        {
            if (File.Exists(temporaryDatabase))
                File.Delete(temporaryDatabase);
        }
    }

    private static bool IsManagedSubdirectory(string relativePath)
    {
        var first = relativePath
            .Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return first is not null
            && first.Equals("GameWads", StringComparison.OrdinalIgnoreCase)
            || first?.Equals("Sourceports", StringComparison.OrdinalIgnoreCase) == true
            || first?.Equals("Mods", StringComparison.OrdinalIgnoreCase) == true
            || first?.Equals("Screenshots", StringComparison.OrdinalIgnoreCase) == true
            || first?.Equals("TitlePics", StringComparison.OrdinalIgnoreCase) == true
            || first?.Equals("Thumbnails", StringComparison.OrdinalIgnoreCase) == true
            || first?.Equals("SaveGames", StringComparison.OrdinalIgnoreCase) == true
            || first?.Equals("Demos", StringComparison.OrdinalIgnoreCase) == true
            || first?.Equals("Temp", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static IEnumerable<string> EnumerateMigrationFiles(
        string gameDirectory,
        string tileDirectory)
    {
        if (Directory.Exists(gameDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(
                         gameDirectory,
                         "*",
                         SearchOption.AllDirectories))
            {
                yield return file;
            }
        }
        if (Directory.Exists(tileDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(
                         tileDirectory,
                         "*",
                         SearchOption.AllDirectories))
            {
                yield return file;
            }
        }
    }

    private static async Task<string?> BackupDestinationAsync(
        string destinationDatabase,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(destinationDatabase))
            return null;
        cancellationToken.ThrowIfCancellationRequested();
        var backupDirectory = Path.Combine(destinationRoot, "Backups");
        Directory.CreateDirectory(backupDirectory);
        var backup = Path.Combine(
            backupDirectory,
            $"DoomLauncher-before-migration-{DateTime.Now:yyyyMMdd-HHmmss}.sqlite");
        SqliteConnection.ClearAllPools();
        File.Copy(destinationDatabase, backup, overwrite: false);
        await Task.CompletedTask;
        return backup;
    }

    private static async Task<string> ReadConfiguredPathAsync(
        string database,
        string root,
        string name,
        string fallback,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Value FROM Configuration WHERE Name=$name LIMIT 1;";
        command.Parameters.AddWithValue("$name", name);
        var value = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken)) ?? fallback;
        value = Environment.ExpandEnvironmentVariables(value);
        return Path.GetFullPath(
            Path.IsPathFullyQualified(value) ? value : Path.Combine(root, value));
    }

    private static async Task RewritePathsAsync(
        string database,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE Configuration
            SET Value = CASE Name
                WHEN 'GameFileDirectory' THEN 'Data\'
                WHEN 'SaveGameDirectory' THEN 'Data\SaveGames\'
                WHEN 'ScreenshotDirectory' THEN 'Data\Screenshots\'
                WHEN 'TempDirectory' THEN 'Data\Temp\'
                WHEN 'DemoDirectory' THEN 'Data\Demos\'
                WHEN 'GameWadDirectory' THEN 'Data\GameWads\'
                ELSE Value
            END
            WHERE Name IN (
                'GameFileDirectory', 'SaveGameDirectory', 'ScreenshotDirectory',
                'TempDirectory', 'DemoDirectory', 'GameWadDirectory'
            );
            UPDATE SourcePorts
            SET Directory = CASE
                WHEN Directory LIKE 'GameFiles\%'
                    THEN 'Data\' || substr(Directory, length('GameFiles\') + 1)
                WHEN Directory LIKE 'Data\GameFiles\%'
                    THEN 'Data\' || substr(Directory, length('Data\GameFiles\') + 1)
                ELSE Directory
            END
            WHERE Directory LIKE 'GameFiles\%'
               OR Directory LIKE 'Data\GameFiles\%';

            UPDATE GameFiles
            SET FileName = 'GameWads\' || FileName
            WHERE instr(replace(FileName, '/', '\'), '\') = 0
              AND GameFileID IN (SELECT GameFileID FROM IWads);

            UPDATE GameFiles
            SET FileName = 'Mods\' || FileName
            WHERE instr(replace(FileName, '/', '\'), '\') = 0
              AND GameFileID NOT IN (SELECT GameFileID FROM IWads);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task VerifyIntegrityAsync(
        string database,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Die importierte Datenbank ist beschädigt: {result}");
    }

    private static string GetDestinationDatabasePath()
    {
        var configured = Environment.GetEnvironmentVariable(
            DoomLauncherDatabaseLocator.DatabaseEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"')));
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DoomLauncher667",
            DoomLauncherDatabaseLocator.DatabaseFileName);
    }
}
