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
        progress?.Report(0);
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
        progress?.Report(3);

        var sourceGameDirectory = await ReadConfiguredPathAsync(
            sourceDatabase,
            legacyDirectory,
            "GameFileDirectory",
            "GameFiles",
            cancellationToken);
        var destinationGameDirectory = Path.Combine(destinationRoot, "Data");
        var sources = await BuildMigrationSourcesAsync(
            sourceDatabase,
            legacyDirectory,
            sourceGameDirectory,
            destinationGameDirectory,
            cancellationToken);
        var files = EnumerateMigrationFiles(sources).ToArray();
        var copied = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.GetFullPath(file.Source).Equals(
                    Path.GetFullPath(file.Target),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(file.Target)!);
            File.Copy(file.Source, file.Target, overwrite: true);
            copied++;
            progress?.Report(
                files.Length == 0
                    ? 70
                    : 3 + (copied * 67d / files.Length));
        }
        progress?.Report(70);

        var temporaryDatabase = destinationDatabase + $".migrating-{Guid.NewGuid():N}";
        try
        {
            File.Copy(sourceDatabase, temporaryDatabase, overwrite: false);
            progress?.Report(76);
            await RewritePathsAsync(
                temporaryDatabase,
                legacyDirectory,
                destinationGameDirectory,
                sources,
                cancellationToken);
            progress?.Report(84);
            await UpgradeSchemaAsync(temporaryDatabase, cancellationToken);
            progress?.Report(94);
            await VerifyIntegrityAsync(temporaryDatabase, cancellationToken);
            progress?.Report(98);
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

    private sealed record MigrationSource(
        string SourceDirectory,
        string TargetDirectory,
        bool RouteLooseFilesToMods = false);

    private sealed record MigrationFile(string Source, string Target);

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

    private static async Task<IReadOnlyList<MigrationSource>> BuildMigrationSourcesAsync(
        string database,
        string legacyRoot,
        string gameDirectory,
        string destinationData,
        CancellationToken cancellationToken)
    {
        var sources = new List<MigrationSource>();
        AddMigrationSource(sources, gameDirectory, destinationData, true);

        var configured = new[]
        {
            ("GameWadDirectory", "IWADs", "GameWads"),
            ("ScreenshotDirectory", Path.Combine("GameFiles", "Screenshots"), "Screenshots"),
            ("SaveGameDirectory", Path.Combine("GameFiles", "SaveGames"), "SaveGames"),
            ("DemoDirectory", Path.Combine("GameFiles", "Demos"), "Demos"),
            ("TempDirectory", Path.Combine("GameFiles", "Temp"), "Temp"),
        };
        foreach (var (name, fallback, target) in configured)
        {
            var source = await ReadConfiguredPathAsync(
                database,
                legacyRoot,
                name,
                fallback,
                cancellationToken);
            AddMigrationSource(
                sources,
                source,
                Path.Combine(destinationData, target));
        }

        foreach (var (sourceName, targetName) in new[]
                 {
                     ("IWADs", "GameWads"),
                     ("GameWads", "GameWads"),
                     ("Sourceports", "Sourceports"),
                     ("SourcePorts", "Sourceports"),
                     ("TileImages", "TileImages"),
                     ("TitlePics", "TitlePics"),
                     ("Screenshots", "Screenshots"),
                     ("SaveGames", "SaveGames"),
                     ("Demos", "Demos"),
                 })
        {
            AddMigrationSource(
                sources,
                Path.Combine(legacyRoot, sourceName),
                Path.Combine(destinationData, targetName));
        }

        foreach (var sourcePortDirectory in await ReadSourcePortDirectoriesAsync(
                     database,
                     legacyRoot,
                     cancellationToken))
        {
            AddMigrationSource(
                sources,
                sourcePortDirectory,
                Path.Combine(
                    destinationData,
                    "Sourceports",
                    Path.GetFileName(sourcePortDirectory.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar))));
        }
        return sources;
    }

    private static async Task<IReadOnlyList<string>> ReadSourcePortDirectoriesAsync(
        string database,
        string legacyRoot,
        CancellationToken cancellationToken)
    {
        var directories = new List<string>();
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
            "SELECT DISTINCT Directory FROM SourcePorts " +
            "WHERE Directory IS NOT NULL AND TRIM(Directory) <> '';";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var value = Environment.ExpandEnvironmentVariables(reader.GetString(0));
            var full = Path.GetFullPath(
                Path.IsPathFullyQualified(value)
                    ? value
                    : Path.Combine(legacyRoot, value));
            if (Directory.Exists(full)
                && IsWithinDirectory(full, legacyRoot))
                directories.Add(full);
        }
        return directories;
    }

    private static void AddMigrationSource(
        ICollection<MigrationSource> sources,
        string source,
        string target,
        bool routeLooseFilesToMods = false)
    {
        if (!Directory.Exists(source))
            return;
        source = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
        if (sources.Any(existing =>
                IsWithinDirectory(source, existing.SourceDirectory)))
        {
            return;
        }
        sources.Add(new MigrationSource(
            source,
            Path.GetFullPath(target),
            routeLooseFilesToMods));
    }

    private static IEnumerable<MigrationFile> EnumerateMigrationFiles(
        IReadOnlyList<MigrationSource> sources)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            foreach (var file in Directory.EnumerateFiles(
                         source.SourceDirectory,
                         "*",
                         SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source.SourceDirectory, file);
                if (source.RouteLooseFilesToMods
                    && !IsManagedSubdirectory(relative))
                {
                    relative = Path.Combine("Mods", relative);
                }
                var target = Path.GetFullPath(
                    Path.Combine(source.TargetDirectory, relative));
                if (targets.Add(target))
                    yield return new MigrationFile(file, target);
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
            await command.ExecuteScalarAsync(cancellationToken));
        if (string.IsNullOrWhiteSpace(value))
            value = fallback;
        value = Environment.ExpandEnvironmentVariables(value);
        return Path.GetFullPath(
            Path.IsPathFullyQualified(value) ? value : Path.Combine(root, value));
    }

    private static async Task RewritePathsAsync(
        string database,
        string legacyRoot,
        string destinationData,
        IReadOnlyList<MigrationSource> sources,
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

            UPDATE GameFiles
            SET FileName = substr(FileName, length('GameFiles\') + 1)
            WHERE replace(FileName, '/', '\') LIKE 'GameFiles\%';

            UPDATE GameFiles
            SET FileName = 'GameWads\' || substr(FileName, length('IWADs\') + 1)
            WHERE replace(FileName, '/', '\') LIKE 'IWADs\%';
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await RewriteSourcePortPathsAsync(
            connection,
            transaction,
            legacyRoot,
            destinationData,
            sources,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task RewriteSourcePortPathsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string legacyRoot,
        string destinationData,
        IReadOnlyList<MigrationSource> sources,
        CancellationToken cancellationToken)
    {
        var rows = new List<(long Id, string Directory)>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT SourcePortID, Directory FROM SourcePorts;";
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? string.Empty : reader.GetString(1)));
            }
        }

        var destinationSourcePorts = Path.Combine(
            destinationData,
            "Sourceports");
        var sourcePortSources = sources
            .Where(source => IsWithinDirectory(
                source.TargetDirectory,
                destinationSourcePorts))
            .ToArray();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Directory)
                || row.Directory.Replace('/', '\\').StartsWith(
                    "Data\\Sourceports",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var expanded = Environment.ExpandEnvironmentVariables(row.Directory);
            var full = Path.GetFullPath(
                Path.IsPathFullyQualified(expanded)
                    ? expanded
                    : Path.Combine(legacyRoot, expanded));
            var matchingSource = sourcePortSources.FirstOrDefault(source =>
                IsWithinDirectory(full, source.SourceDirectory));
            if (matchingSource is null)
                continue;
            var relative = Path.GetRelativePath(
                matchingSource.SourceDirectory,
                full);
            var migratedDirectory = relative == "."
                ? matchingSource.TargetDirectory
                : Path.Combine(matchingSource.TargetDirectory, relative);
            var portableRelative = Path.GetRelativePath(
                destinationData,
                migratedDirectory);
            var portable = $"Data\\{portableRelative.TrimEnd('\\', '/')}\\";
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE SourcePorts SET Directory=$directory WHERE SourcePortID=$id;";
            update.Parameters.AddWithValue("$directory", portable);
            update.Parameters.AddWithValue("$id", row.Id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var normalizedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedPath.Equals(
                   normalizedDirectory,
                   StringComparison.OrdinalIgnoreCase)
               || normalizedPath.StartsWith(
                   normalizedDirectory + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static async Task UpgradeSchemaAsync(
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
        await WinUiDatabaseSchema.EnsureAsync(connection, cancellationToken);
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
