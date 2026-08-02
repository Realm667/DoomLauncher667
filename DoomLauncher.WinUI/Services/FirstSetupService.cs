using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace DoomLauncher.WinUI.Services;

public sealed class FirstSetupService(
    IDoomLauncherDatabaseLocator databaseLocator,
    INativeLibraryService libraryService) : IFirstSetupService
{
    private const string WizardMarker = "FirstSetupWizardV2";
    private static readonly HashSet<string> ArchiveExtensions = new(
        [".zip", ".7z", ".rar", ".wad"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ModExtensions = new(
        [".zip", ".7z", ".rar", ".wad", ".pk3", ".ipk3", ".pk7", ".pke"],
        StringComparer.OrdinalIgnoreCase);

    public async Task<string> EnsureDatabaseAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return databaseLocator.FindDatabase();
        }
        catch (FileNotFoundException)
        {
        }

        var configured = Environment.GetEnvironmentVariable(
            DoomLauncherDatabaseLocator.DatabaseEnvironmentVariable);
        var destination = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DoomLauncher667",
                DoomLauncherDatabaseLocator.DatabaseFileName)
            : Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(
                    configured.Trim().Trim('"')));
        var template = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "DoomLauncher.Empty.sqlite");
        if (!File.Exists(template))
            throw new FileNotFoundException(
                "Die Vorlage für eine leere portable Datenbank fehlt.",
                template);

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(template, destination, overwrite: false);
        Environment.SetEnvironmentVariable(
            DoomLauncherDatabaseLocator.DatabaseEnvironmentVariable,
            destination);
        await Task.CompletedTask;
        return destination;
    }

    public async Task<ManagedLayoutMigrationResult> EnsureManagedLayoutAsync(
        CancellationToken cancellationToken = default)
    {
        var databasePath = databaseLocator.FindDatabase();
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        var configuredRoot = await GetGameFilesRootAsync(
            connection,
            databasePath,
            cancellationToken);
        var databaseDirectory = Path.GetDirectoryName(databasePath)!;
        var root = Path.Combine(databaseDirectory, "Data");
        var legacyPortableRoot = Path.Combine(root, "GameFiles");
        var movedLayoutFiles = 0;
        if (Directory.Exists(legacyPortableRoot))
        {
            movedLayoutFiles = MoveDirectoryContents(
                legacyPortableRoot,
                root,
                cancellationToken);
            if (!Directory.EnumerateFileSystemEntries(legacyPortableRoot).Any())
                Directory.Delete(legacyPortableRoot);
        }
        else if (!IsSamePath(configuredRoot, legacyPortableRoot)
            && !IsSamePath(configuredRoot, root))
        {
            // Preserve an explicitly configured external library. Only the
            // former portable Data\GameFiles layout is flattened automatically.
            root = configuredRoot;
        }

        var portableData = Path.Combine(databaseDirectory, "Data");
        var tileImages = Path.Combine(portableData, "TileImages");
        var collectionArtworks = Path.Combine(
            portableData,
            "CollectionArtworks");
        var legacyTileImages = Path.Combine(databaseDirectory, "TileImages");
        if (Directory.Exists(legacyTileImages))
        {
            movedLayoutFiles += MoveDirectoryContents(
                legacyTileImages,
                tileImages,
                cancellationToken);
            if (!Directory.EnumerateFileSystemEntries(legacyTileImages).Any())
                Directory.Delete(legacyTileImages);
        }
        var legacyUserData = Path.Combine(databaseDirectory, "UserData");
        var legacyCollectionArtworks = Path.Combine(
            legacyUserData,
            "CollectionArtworks");
        if (Directory.Exists(legacyCollectionArtworks))
        {
            movedLayoutFiles += MoveDirectoryContents(
                legacyCollectionArtworks,
                collectionArtworks,
                cancellationToken);
            if (!Directory.EnumerateFileSystemEntries(
                    legacyCollectionArtworks).Any())
            {
                Directory.Delete(legacyCollectionArtworks);
            }
        }
        if (Directory.Exists(legacyUserData)
            && !Directory.EnumerateFileSystemEntries(legacyUserData).Any())
        {
            Directory.Delete(legacyUserData);
        }

        var mods = Path.Combine(root, "Mods");
        var gameWads = Path.Combine(root, "GameWads");
        var sourcePorts = Path.Combine(root, "Sourceports");
        var saveGames = Path.Combine(root, "SaveGames");
        var screenshots = Path.Combine(root, "Screenshots");
        var temp = Path.Combine(root, "Temp");
        var demos = Path.Combine(root, "Demos");
        var titlePics = Path.Combine(root, "TitlePics");
        var themes = Path.Combine(root, "Themes");
        Directory.CreateDirectory(mods);
        Directory.CreateDirectory(gameWads);
        Directory.CreateDirectory(sourcePorts);
        Directory.CreateDirectory(saveGames);
        Directory.CreateDirectory(screenshots);
        Directory.CreateDirectory(temp);
        Directory.CreateDirectory(demos);
        Directory.CreateDirectory(titlePics);
        Directory.CreateDirectory(themes);
        Directory.CreateDirectory(tileImages);
        Directory.CreateDirectory(collectionArtworks);

        var references = new List<(
            int Id,
            string FileName,
            bool IsIwad,
            string? InternalIwad)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT game.GameFileID, game.FileName,
                       CASE WHEN iwad.IWadID IS NULL THEN 0 ELSE 1 END,
                       iwad.FileName
                FROM GameFiles game
                LEFT JOIN IWads iwad ON iwad.GameFileID = game.GameFileID;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                references.Add((
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetInt32(2) != 0,
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        var updates = new List<(int Id, string FileName)>();
        var movedFiles = new List<(string Source, string Target)>();
        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.IsPathFullyQualified(reference.FileName)
                || reference.FileName.Contains('\\')
                || reference.FileName.Contains('/'))
            {
                continue;
            }

            var directoryName = reference.IsIwad ? "GameWads" : "Mods";
            var managedName = reference.FileName;
            if (reference.IsIwad
                && !string.IsNullOrWhiteSpace(reference.InternalIwad)
                && !File.Exists(Path.Combine(root, directoryName, managedName))
                && File.Exists(Path.Combine(
                    root,
                    directoryName,
                    reference.InternalIwad)))
            {
                managedName = reference.InternalIwad;
            }
            var relative = Path.Combine(directoryName, managedName);
            var source = Path.Combine(root, reference.FileName);
            var target = Path.Combine(root, relative);
            if (!File.Exists(source) && !File.Exists(target))
                continue;
            if (File.Exists(source) && !File.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(source, target);
                movedFiles.Add((source, target));
            }
            updates.Add((reference.Id, relative));
        }

        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var update in updates)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "UPDATE GameFiles SET FileName=$fileName WHERE GameFileID=$id;";
                command.Parameters.AddWithValue("$fileName", update.FileName);
                command.Parameters.AddWithValue("$id", update.Id);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await UpsertConfigurationAsync(
                connection,
                transaction,
                "GameFileDirectory",
                ToPortableDirectory(databasePath, root),
                cancellationToken);
            await UpsertConfigurationAsync(
                connection,
                transaction,
                "GameWadDirectory",
                ToPortableDirectory(databasePath, gameWads),
                cancellationToken);
            await UpsertConfigurationAsync(
                connection,
                transaction,
                "SaveGameDirectory",
                ToPortableDirectory(databasePath, saveGames),
                cancellationToken);
            await UpsertConfigurationAsync(
                connection,
                transaction,
                "ScreenshotDirectory",
                ToPortableDirectory(databasePath, screenshots),
                cancellationToken);
            await UpsertConfigurationAsync(
                connection,
                transaction,
                "TempDirectory",
                ToPortableDirectory(databasePath, temp),
                cancellationToken);
            await UpsertConfigurationAsync(
                connection,
                transaction,
                "DemoDirectory",
                ToPortableDirectory(databasePath, demos),
                cancellationToken);
            await using (var sourcePortPaths = connection.CreateCommand())
            {
                sourcePortPaths.Transaction = transaction;
                sourcePortPaths.CommandText =
                    """
                    UPDATE SourcePorts
                    SET Directory = 'Data\' || substr(
                        replace(Directory, '/', '\'),
                        length('Data\GameFiles\') + 1)
                    WHERE replace(Directory, '/', '\') LIKE 'Data\GameFiles\%';
                    """;
                await sourcePortPaths.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            foreach (var moved in movedFiles.AsEnumerable().Reverse())
            {
                if (File.Exists(moved.Target) && !File.Exists(moved.Source))
                    File.Move(moved.Target, moved.Source);
            }
            throw;
        }
        return new ManagedLayoutMigrationResult(
            updates.Count,
            movedLayoutFiles + movedFiles.Count);
    }

    private static int MoveDirectoryContents(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        var movedFiles = 0;
        foreach (var file in Directory.EnumerateFiles(
                     source,
                     "*",
                     SearchOption.TopDirectoryOnly).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetFileName(file));
            if (File.Exists(target))
            {
                if (!FilesAreIdentical(file, target))
                {
                    throw new IOException(
                        $"Die Datenmigration würde eine abweichende Datei überschreiben: {target}");
                }
                File.Delete(file);
            }
            else
            {
                File.Move(file, target);
            }
            movedFiles++;
        }
        foreach (var directory in Directory.EnumerateDirectories(
                     source,
                     "*",
                     SearchOption.TopDirectoryOnly).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetFileName(directory));
            movedFiles += MoveDirectoryContents(
                directory,
                target,
                cancellationToken);
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        return movedFiles;
    }

    private static bool FilesAreIdentical(string first, string second)
    {
        var firstInfo = new FileInfo(first);
        var secondInfo = new FileInfo(second);
        if (firstInfo.Length != secondInfo.Length)
            return false;
        using var firstStream = File.OpenRead(first);
        using var secondStream = File.OpenRead(second);
        return SHA256.HashData(firstStream)
            .SequenceEqual(SHA256.HashData(secondStream));
    }

    private static bool IsSamePath(string first, string second) =>
        Path.GetFullPath(first)
            .TrimEnd('\\', '/')
            .Equals(
                Path.GetFullPath(second).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);

    public async Task<bool> ShouldRunWizardAsync(
        CancellationToken cancellationToken = default)
    {
        var databasePath = databaseLocator.FindDatabase();
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var marker = connection.CreateCommand();
        marker.CommandText =
            "SELECT COUNT(*) FROM WinUI_Migrations WHERE MigrationKey=$key;";
        marker.Parameters.AddWithValue("$key", WizardMarker);
        if (Convert.ToInt32(
                await marker.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture) > 0)
        {
            return false;
        }

        await using var entries = connection.CreateCommand();
        entries.CommandText = "SELECT COUNT(*) FROM GameFiles;";
        return Convert.ToInt32(
            await entries.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture) == 0;
    }

    public async Task CompleteWizardAsync(
        CancellationToken cancellationToken = default)
    {
        var databasePath = databaseLocator.FindDatabase();
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR REPLACE INTO WinUI_Migrations (MigrationKey, CompletedAt)
            VALUES ($key, $completedAt);
            """;
        command.Parameters.AddWithValue("$key", WizardMarker);
        command.Parameters.AddWithValue(
            "$completedAt",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SetupScanResult> ScanIwadsAsync(
        CancellationToken cancellationToken = default)
        => await ScanIwadsAsync(cancellationToken, null);

    public async Task<SetupScanResult> ScanIwadsAsync(
        CancellationToken cancellationToken,
        IProgress<double>? progress)
    {
        progress?.Report(0);
        await EnsureManagedLayoutAsync(cancellationToken);
        var (databasePath, root) = await GetLayoutAsync(cancellationToken);
        var directory = Path.Combine(root, "GameWads");
        var warnings = new List<string>();
        var removedItems = new List<string>();
        var definitions = await libraryService.LoadLauncherDefinitionsAsync(cancellationToken);
        var pending = new List<(string Archive, string Reference, IwadArchiveCandidate Candidate)>();
        var successfullyReadArchives = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var archives = EnumerateFiles(directory, ArchiveExtensions).ToArray();
        for (var archiveIndex = 0; archiveIndex < archives.Length; archiveIndex++)
        {
            var archive = archives[archiveIndex];
            try
            {
                var candidates = await IwadVersionDetector.ScanArchiveAsync(
                    archive,
                    cancellationToken);
                successfullyReadArchives.Add(Path.GetFullPath(archive));
                foreach (var candidate in candidates)
                {
                    pending.Add((
                        archive,
                        Path.GetRelativePath(root, archive),
                        candidate));
                }
            }
            catch (Exception exception) when (
                exception is IOException
                or InvalidDataException
                or UnauthorizedAccessException
                or NotSupportedException)
            {
                warnings.Add($"{Path.GetFileName(archive)}: {exception.Message}");
            }
            progress?.Report(
                archives.Length == 0
                    ? 35
                    : 5 + ((archiveIndex + 1) * 30d / archives.Length));
        }

        var imported = 0;
        var updated = 0;
        var skipped = 0;
        var orderedPending = pending
            .OrderBy(item => item.Candidate.InternalFileName.Equals(
                "HEXDD.WAD",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        for (var pendingIndex = 0; pendingIndex < orderedPending.Length; pendingIndex++)
        {
            var item = orderedPending[pendingIndex];
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var existing = definitions.Iwads.FirstOrDefault(definition =>
                    (!string.IsNullOrWhiteSpace(item.Candidate.Md5)
                     && definition.Md5.Equals(
                         item.Candidate.Md5,
                         StringComparison.OrdinalIgnoreCase))
                    || (definition.ArchiveFileName.Equals(
                            item.Reference,
                            StringComparison.OrdinalIgnoreCase)
                        && definition.InternalFileName.Equals(
                            item.Candidate.InternalFileName,
                            StringComparison.OrdinalIgnoreCase)));
                var sameLocation = existing is not null
                    && existing.ArchiveFileName.Equals(
                        item.Reference,
                        StringComparison.OrdinalIgnoreCase)
                    && existing.InternalFileName.Equals(
                        item.Candidate.InternalFileName,
                        StringComparison.OrdinalIgnoreCase);
                var existingLocationStillPresent = existing is not null
                    && pending.Any(candidate =>
                        candidate.Reference.Equals(
                            existing.ArchiveFileName,
                            StringComparison.OrdinalIgnoreCase)
                        && candidate.Candidate.InternalFileName.Equals(
                            existing.InternalFileName,
                            StringComparison.OrdinalIgnoreCase)
                        && candidate.Candidate.Md5.Equals(
                            existing.Md5,
                            StringComparison.OrdinalIgnoreCase));
                await EnsureGameFileAsync(
                    databasePath,
                    root,
                    item.Archive,
                    item.Reference,
                    item.Candidate.SuggestedName,
                    existing?.IwadId,
                    cancellationToken);
                if (existing is not null
                    && existing.Md5.Equals(
                        item.Candidate.Md5,
                        StringComparison.OrdinalIgnoreCase)
                    && (sameLocation || existingLocationStillPresent))
                {
                    skipped++;
                    progress?.Report(
                        35 + ((pendingIndex + 1) * 45d /
                            Math.Max(1, orderedPending.Length)));
                    continue;
                }

                await libraryService.SaveIwadAsync(
                    new NativeIwadDefinition(
                        existing?.IwadId,
                        item.Candidate.SuggestedName,
                        item.Reference,
                        item.Candidate.InternalFileName,
                        item.Candidate.Version,
                        item.Candidate.Md5,
                        item.Candidate.FileSize,
                        item.Candidate.CatalogLabel),
                    cancellationToken);
                if (existing is null)
                    imported++;
                else
                    updated++;
                definitions = await libraryService.LoadLauncherDefinitionsAsync(
                    cancellationToken);
            }
            catch (Exception exception)
            {
                warnings.Add(
                    $"{Path.GetFileName(item.Archive)} / " +
                    $"{item.Candidate.InternalFileName}: {exception.Message}");
            }
            progress?.Report(
                orderedPending.Length == 0
                    ? 80
                    : 35 + ((pendingIndex + 1) * 45d / orderedPending.Length));
        }

        var reconciliation = await ReconcileLegacyIwadGameFilesAsync(
            databasePath,
            root,
            pending,
            cancellationToken);
        updated += reconciliation.Reconciled;
        removedItems.AddRange(reconciliation.RemovedItems);
        progress?.Report(86);

        definitions = await libraryService.LoadLauncherDefinitionsAsync(cancellationToken);
        for (var definitionIndex = 0;
             definitionIndex < definitions.Iwads.Count;
             definitionIndex++)
        {
            var definition = definitions.Iwads[definitionIndex];
            cancellationToken.ThrowIfCancellationRequested();
            var archivePath = ResolveManagedReference(
                databasePath,
                root,
                directory,
                definition.ArchiveFileName);
            if (archivePath is null)
                continue;

            var missingArchive = !File.Exists(archivePath);
            var missingInternalIwad = !missingArchive
                && successfullyReadArchives.Contains(archivePath)
                && !pending.Any(item =>
                    Path.GetFullPath(item.Archive).Equals(
                        archivePath,
                        StringComparison.OrdinalIgnoreCase)
                    && item.Candidate.InternalFileName.Equals(
                        definition.InternalFileName,
                        StringComparison.OrdinalIgnoreCase));
            if (!missingArchive && !missingInternalIwad)
                continue;

            try
            {
                await libraryService.DeleteIwadAsync(
                    definition.IwadId!.Value,
                    cancellationToken: cancellationToken);
                removedItems.Add(
                    $"{definition.Name} ({definition.InternalFileName})");
            }
            catch (Exception exception)
            {
                warnings.Add($"{definition.Name}: {exception.Message}");
            }
            progress?.Report(
                86 + ((definitionIndex + 1) * 13d /
                    Math.Max(1, definitions.Iwads.Count)));
        }
        progress?.Report(100);
        return new SetupScanResult(
            pending.Count,
            imported,
            updated,
            removedItems.Count,
            skipped,
            removedItems,
            warnings);
    }

    public async Task<SetupScanResult> ScanSourcePortsAsync(
        CancellationToken cancellationToken = default)
        => await ScanSourcePortsAsync(cancellationToken, null);

    public async Task<SetupScanResult> ScanSourcePortsAsync(
        CancellationToken cancellationToken,
        IProgress<double>? progress)
    {
        progress?.Report(0);
        await EnsureManagedLayoutAsync(cancellationToken);
        var (databasePath, root) = await GetLayoutAsync(cancellationToken);
        var directory = Path.Combine(root, "Sourceports");
        var warnings = new List<string>();
        var removedItems = new List<string>();
        var definitions = await libraryService.LoadLauncherDefinitionsAsync(cancellationToken);
        var imported = 0;
        var updated = 0;
        var skipped = 0;
        var discovered = 0;
        var portDirectories = Directory.EnumerateDirectories(
                directory,
                "*",
                SearchOption.TopDirectoryOnly)
            .ToArray();
        for (var portIndex = 0; portIndex < portDirectories.Length; portIndex++)
        {
            var portDirectory = portDirectories[portIndex];
            cancellationToken.ThrowIfCancellationRequested();
            var executable = FindSourcePortExecutable(portDirectory);
            if (executable is null)
            {
                skipped++;
                warnings.Add(
                    $"{Path.GetFileName(portDirectory)}: keine passende EXE gefunden.");
                progress?.Report(
                    5 + ((portIndex + 1) * 77d /
                        Math.Max(1, portDirectories.Length)));
                continue;
            }
            discovered++;
            try
            {
                var portableDirectory = ToPortableDirectory(databasePath, portDirectory);
                var existing = definitions.SourcePorts.FirstOrDefault(port =>
                    port.Directory.TrimEnd('\\', '/').Equals(
                        portableDirectory.TrimEnd('\\', '/'),
                        StringComparison.OrdinalIgnoreCase)
                    || (port.Executable.Equals(
                            Path.GetFileName(executable),
                            StringComparison.OrdinalIgnoreCase)
                        && port.Name.Equals(
                            Path.GetFileName(portDirectory),
                            StringComparison.OrdinalIgnoreCase)));
                var identity = Path.GetFileNameWithoutExtension(executable);
                var zdoomFamily = IsZDoomFamily(identity)
                    || IsZDoomFamily(Path.GetFileName(portDirectory));
                var version = ReadExecutableVersion(executable);
                await libraryService.SaveSourcePortAsync(
                    new NativeSourcePortDefinition(
                        existing?.SourcePortId,
                        Path.GetFileName(portDirectory),
                        portableDirectory,
                        Path.GetFileName(executable),
                        ".wad,.pk3,.ipk3,.pk7,.deh,.bex,.pke",
                        "-file",
                        string.Empty,
                        version,
                        "Auto",
                        string.Empty,
                        ".png,.jpg,.jpeg,.bmp",
                        string.Empty,
                        zdoomFamily ? "ZDoomSave" : "None",
                        string.Empty,
                        ".zds"),
                    cancellationToken);
                if (existing is null)
                    imported++;
                else
                    updated++;
                definitions = await libraryService.LoadLauncherDefinitionsAsync(
                    cancellationToken);
            }
            catch (Exception exception)
            {
                warnings.Add($"{Path.GetFileName(portDirectory)}: {exception.Message}");
            }
            progress?.Report(
                portDirectories.Length == 0
                    ? 82
                    : 5 + ((portIndex + 1) * 77d / portDirectories.Length));
        }

        definitions = await libraryService.LoadLauncherDefinitionsAsync(cancellationToken);
        for (var definitionIndex = 0;
             definitionIndex < definitions.SourcePorts.Count;
             definitionIndex++)
        {
            var definition = definitions.SourcePorts[definitionIndex];
            cancellationToken.ThrowIfCancellationRequested();
            var portDirectory = ResolveManagedReference(
                databasePath,
                root,
                directory,
                definition.Directory);
            if (portDirectory is null)
                continue;
            var executable = Path.Combine(
                portDirectory,
                Path.GetFileName(definition.Executable));
            if (Directory.Exists(portDirectory) && File.Exists(executable))
                continue;

            try
            {
                await libraryService.DeleteSourcePortAsync(
                    definition.SourcePortId!.Value,
                    cancellationToken: cancellationToken);
                removedItems.Add(definition.DisplayLabel);
            }
            catch (Exception exception)
            {
                warnings.Add($"{definition.Name}: {exception.Message}");
            }
            progress?.Report(
                82 + ((definitionIndex + 1) * 17d /
                    Math.Max(1, definitions.SourcePorts.Count)));
        }
        progress?.Report(100);
        return new SetupScanResult(
            discovered,
            imported,
            updated,
            removedItems.Count,
            skipped,
            removedItems,
            warnings);
    }

    public async Task<SetupScanResult> ScanModsAsync(
        CancellationToken cancellationToken = default)
        => await ScanModsAsync(cancellationToken, null, null);

    public async Task<SetupScanResult> ScanModsAsync(
        CancellationToken cancellationToken,
        IProgress<double>? progress)
        => await ScanModsAsync(cancellationToken, progress, null);

    public async Task<IReadOnlyList<IwadInModsPrompt>> FindIwadsInModsAsync(
        CancellationToken cancellationToken,
        IProgress<double>? progress = null)
    {
        progress?.Report(0);
        await EnsureManagedLayoutAsync(cancellationToken);
        var (_, root) = await GetLayoutAsync(cancellationToken);
        var directory = Path.Combine(root, "Mods");
        var files = EnumerateFiles(directory, ModExtensions).ToArray();
        var prompts = new List<IwadInModsPrompt>();
        for (var index = 0; index < files.Length; index++)
        {
            var file = files[index];
            cancellationToken.ThrowIfCancellationRequested();
            if (ArchiveExtensions.Contains(Path.GetExtension(file)))
            {
                try
                {
                    var candidates = await IwadVersionDetector.ScanArchiveAsync(
                        file,
                        cancellationToken);
                    if (candidates.Count > 0)
                    {
                        prompts.Add(new IwadInModsPrompt(
                            Path.GetFullPath(file),
                            Path.GetFileName(file),
                            candidates
                                .Select(candidate => candidate.SuggestedName)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToArray()));
                    }
                }
                catch (Exception exception) when (
                    exception is IOException
                    or InvalidDataException
                    or UnauthorizedAccessException
                    or NotSupportedException)
                {
                    // The actual mod scan reports unreadable archives as warnings.
                }
            }
            progress?.Report(
                files.Length == 0
                    ? 100
                    : ((index + 1) * 100d / files.Length));
        }
        progress?.Report(100);
        return prompts;
    }

    public async Task<SetupScanResult> ScanModsAsync(
        CancellationToken cancellationToken,
        IProgress<double>? progress,
        IReadOnlyDictionary<string, IwadInModsAction>? iwadDecisions)
    {
        progress?.Report(0);
        await EnsureManagedLayoutAsync(cancellationToken);
        var (_, root) = await GetLayoutAsync(cancellationToken);
        var directory = Path.Combine(root, "Mods");
        var files = EnumerateFiles(directory, ModExtensions).ToArray();
        var imported = 0;
        var updated = 0;
        var skipped = 0;
        var movedIwads = 0;
        var warnings = new List<string>();
        for (var fileIndex = 0; fileIndex < files.Length; fileIndex++)
        {
            var file = files[fileIndex];
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fullPath = Path.GetFullPath(file);
                var iwadAction = IwadInModsAction.KeepAsMod;
                var hasDecision = iwadDecisions?.TryGetValue(
                    fullPath,
                    out iwadAction) == true;
                var isIwad = hasDecision;
                if (iwadDecisions is null
                    && ArchiveExtensions.Contains(Path.GetExtension(file)))
                {
                    isIwad = (await IwadVersionDetector.ScanArchiveAsync(
                        file,
                        cancellationToken)).Count > 0;
                }
                if (isIwad && hasDecision
                    && iwadAction == IwadInModsAction.MoveAndRegister)
                {
                    var target = Path.Combine(
                        root,
                        "GameWads",
                        Path.GetFileName(file));
                    if (File.Exists(target))
                    {
                        throw new IOException(
                            $"Im IWAD-Verzeichnis existiert bereits " +
                            $"{Path.GetFileName(file)}.");
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(file, target);
                    movedIwads++;
                    progress?.Report(
                        (fileIndex + 1) * 90d / Math.Max(1, files.Length));
                    continue;
                }
                if (isIwad && !hasDecision)
                {
                    skipped++;
                    warnings.Add(
                        $"{Path.GetFileName(file)}: IWAD erkannt; bitte in das " +
                        "IWAD-Verzeichnis verschieben.");
                    progress?.Report(
                        (fileIndex + 1) * 90d / Math.Max(1, files.Length));
                    continue;
                }
                var result = await libraryService.ImportAsync(file, cancellationToken);
                imported++;
                try
                {
                    await libraryService.TryImportTitlePicAsync(
                        result.GameFileId,
                        result.DestinationPath,
                        cancellationToken);
                }
                catch (Exception exception) when (
                    exception is IOException
                    or InvalidDataException
                    or NotSupportedException)
                {
                    warnings.Add($"{Path.GetFileName(file)}: {exception.Message}");
                }
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("bereits", StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
            }
            catch (Exception exception)
            {
                warnings.Add($"{Path.GetFileName(file)}: {exception.Message}");
            }
            progress?.Report(
                files.Length == 0
                    ? 90
                    : ((fileIndex + 1) * 90d / files.Length));
        }
        if (movedIwads > 0)
        {
            progress?.Report(92);
            var iwadResult = await ScanIwadsAsync(cancellationToken);
            updated += iwadResult.Imported + iwadResult.Updated;
            warnings.AddRange(iwadResult.Warnings);
        }
        progress?.Report(100);
        return new SetupScanResult(
            files.Length,
            imported,
            updated,
            0,
            skipped,
            [],
            warnings);
    }

    private async Task<(string DatabasePath, string Root)> GetLayoutAsync(
        CancellationToken cancellationToken)
    {
        var databasePath = databaseLocator.FindDatabase();
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        var root = await GetGameFilesRootAsync(
            connection,
            databasePath,
            cancellationToken);
        return (databasePath, root);
    }

    private static async Task EnsureGameFileAsync(
        string databasePath,
        string gameFilesRoot,
        string fullPath,
        string reference,
        string title,
        int? existingIwadId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var existing = connection.CreateCommand();
        existing.CommandText =
            "SELECT GameFileID FROM GameFiles WHERE FileName=$file COLLATE NOCASE LIMIT 1;";
        existing.Parameters.AddWithValue("$file", reference);
        if (await existing.ExecuteScalarAsync(cancellationToken) is not null)
            return;

        if (existingIwadId.HasValue)
        {
            await using var current = connection.CreateCommand();
            current.CommandText =
                """
                SELECT game.GameFileID, game.FileName
                FROM IWads iwad
                JOIN GameFiles game ON game.GameFileID=iwad.GameFileID
                WHERE iwad.IWadID=$iwadId;
                """;
            current.Parameters.AddWithValue("$iwadId", existingIwadId.Value);
            await using var reader = await current.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var gameFileId = reader.GetInt32(0);
                var currentReference = reader.GetString(1);
                var currentPath = Path.GetFullPath(
                    Path.IsPathFullyQualified(currentReference)
                        ? currentReference
                        : Path.Combine(gameFilesRoot, currentReference));
                await reader.DisposeAsync();
                if (!File.Exists(currentPath))
                {
                    await using var relocate = connection.CreateCommand();
                    relocate.CommandText =
                        """
                        UPDATE GameFiles
                        SET FileName=$file,
                            Title=COALESCE(NULLIF(TRIM(Title), ''), $title)
                        WHERE GameFileID=$gameFileId;
                        """;
                    relocate.Parameters.AddWithValue("$file", reference);
                    relocate.Parameters.AddWithValue(
                        "$title",
                        DatabaseTextSanitizer.SingleLine(title));
                    relocate.Parameters.AddWithValue("$gameFileId", gameFileId);
                    await relocate.ExecuteNonQueryAsync(cancellationToken);
                    return;
                }
            }
        }

        var maps = await MapNameExtractor.ExtractAsync(fullPath, cancellationToken);
        await using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO GameFiles
                (FileName, Title, Author, Downloaded, MinutesPlayed,
                 Map, MapCount, IsSyncNeeded)
            VALUES
                ($file, $title, NULL, $downloaded, 0, $maps, $mapCount, 1);
            """;
        insert.Parameters.AddWithValue("$file", reference);
        insert.Parameters.AddWithValue(
            "$title",
            DatabaseTextSanitizer.SingleLine(title));
        insert.Parameters.AddWithValue(
            "$downloaded",
            DateTime.Now.ToString(
                "yyyy-MM-dd HH:mm:ss.fffffff",
                CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue(
            "$maps",
            maps.Count == 0 ? DBNull.Value : string.Join(", ", maps));
        insert.Parameters.AddWithValue("$mapCount", maps.Count);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<(
        int Reconciled,
        IReadOnlyList<string> RemovedItems)> ReconcileLegacyIwadGameFilesAsync(
        string databasePath,
        string gameFilesRoot,
        IReadOnlyList<(string Archive, string Reference, IwadArchiveCandidate Candidate)> pending,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        var active = new List<(int GameFileId, string Reference, string Internal, string Title)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT game.GameFileID, game.FileName, iwad.FileName, iwad.Name
                FROM IWads iwad
                JOIN GameFiles game ON game.GameFileID=iwad.GameFileID;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                active.Add((
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3)));
            }
        }

        var legacy = new List<(int GameFileId, string Reference, string Title)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT game.GameFileID, game.FileName, COALESCE(game.Title, '')
                FROM GameFiles game
                LEFT JOIN IWads iwad ON iwad.GameFileID=game.GameFileID
                WHERE iwad.IWadID IS NULL
                  AND replace(game.FileName, '/', '\') LIKE 'GameWads\%';
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var reference = reader.GetString(1);
                var path = Path.GetFullPath(
                    Path.IsPathFullyQualified(reference)
                        ? reference
                        : Path.Combine(gameFilesRoot, reference));
                if (!File.Exists(path))
                {
                    legacy.Add((
                        reader.GetInt32(0),
                        reference,
                        reader.GetString(2)));
                }
            }
        }

        var matches = (
            from old in legacy
            from current in active
            let legacyInternal = Path.GetFileName(old.Reference)
            where legacyInternal.Equals(
                      current.Internal,
                      StringComparison.OrdinalIgnoreCase)
                  || (legacyInternal.Equals(
                          "HERETICS.WAD",
                          StringComparison.OrdinalIgnoreCase)
                      && current.Internal.Equals(
                          "HERETIC.WAD",
                          StringComparison.OrdinalIgnoreCase))
            let scan = pending.FirstOrDefault(item =>
                item.Reference.Equals(current.Reference, StringComparison.OrdinalIgnoreCase)
                && item.Candidate.InternalFileName.Equals(
                    current.Internal,
                    StringComparison.OrdinalIgnoreCase))
            let score = ScoreIwadTitle(
                old.Title,
                current.Title,
                scan.Candidate?.SuggestedName ?? string.Empty,
                scan.Candidate?.CatalogLabel ?? string.Empty)
            orderby score descending
            select (Legacy: old, Active: current, Score: score)
        ).ToList();

        var usedLegacy = new HashSet<int>();
        var usedActive = new HashSet<int>();
        var reconciled = 0;
        foreach (var match in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (usedLegacy.Contains(match.Legacy.GameFileId)
                || usedActive.Contains(match.Active.GameFileId))
                continue;
            usedLegacy.Add(match.Legacy.GameFileId);
            usedActive.Add(match.Active.GameFileId);

            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                foreach (var table in new[] { "Files", "Stats", "GameProfiles" })
                {
                    await using var move = connection.CreateCommand();
                    move.Transaction = transaction;
                    move.CommandText =
                        $"UPDATE {table} SET GameFileID=$legacy WHERE GameFileID=$active;";
                    move.Parameters.AddWithValue("$legacy", match.Legacy.GameFileId);
                    move.Parameters.AddWithValue("$active", match.Active.GameFileId);
                    await move.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var tags = connection.CreateCommand())
                {
                    tags.Transaction = transaction;
                    tags.CommandText =
                        """
                        INSERT OR IGNORE INTO TagMapping (FileID, TagID)
                        SELECT $legacy, TagID FROM TagMapping WHERE FileID=$active;
                        DELETE FROM TagMapping WHERE FileID=$active;
                        """;
                    tags.Parameters.AddWithValue("$legacy", match.Legacy.GameFileId);
                    tags.Parameters.AddWithValue("$active", match.Active.GameFileId);
                    await tags.ExecuteNonQueryAsync(cancellationToken);
                }

                await MergeSingleGameFileRowAsync(
                    connection,
                    transaction,
                    "WinUI_GameState",
                    "Finished",
                    match.Legacy.GameFileId,
                    match.Active.GameFileId,
                    cancellationToken);
                await MoveOptionalGameFileRowAsync(
                    connection,
                    transaction,
                    "WinUI_IdGamesDownloads",
                    match.Legacy.GameFileId,
                    match.Active.GameFileId,
                    cancellationToken);
                await MoveOptionalGameFileRowAsync(
                    connection,
                    transaction,
                    "WinUI_IdGamesMetadata",
                    match.Legacy.GameFileId,
                    match.Active.GameFileId,
                    cancellationToken);

                await using (var game = connection.CreateCommand())
                {
                    game.Transaction = transaction;
                    game.CommandText =
                        """
                        UPDATE GameFiles
                        SET FileName=$reference,
                            Map=COALESCE(NULLIF(TRIM(Map), ''),
                                (SELECT Map FROM GameFiles WHERE GameFileID=$active)),
                            MapCount=MAX(
                                COALESCE(MapCount, 0),
                                COALESCE((SELECT MapCount FROM GameFiles
                                          WHERE GameFileID=$active), 0))
                        WHERE GameFileID=$legacy;
                        UPDATE IWads SET GameFileID=$legacy WHERE GameFileID=$active;
                        DELETE FROM GameFiles WHERE GameFileID=$active;
                        """;
                    game.Parameters.AddWithValue("$reference", match.Active.Reference);
                    game.Parameters.AddWithValue("$legacy", match.Legacy.GameFileId);
                    game.Parameters.AddWithValue("$active", match.Active.GameFileId);
                    await game.ExecuteNonQueryAsync(cancellationToken);
                }
                await transaction.CommitAsync(cancellationToken);
                reconciled++;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        var removed = new List<string>();
        foreach (var old in legacy.Where(item => !usedLegacy.Contains(item.GameFileId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var empty = connection.CreateCommand();
            empty.CommandText =
                """
                SELECT
                    COALESCE(game.MinutesPlayed, 0)
                    + (SELECT COUNT(*) FROM Files WHERE GameFileID=game.GameFileID)
                    + (SELECT COUNT(*) FROM Stats WHERE GameFileID=game.GameFileID)
                    + (SELECT COUNT(*) FROM GameProfiles WHERE GameFileID=game.GameFileID)
                    + (SELECT COUNT(*) FROM TagMapping WHERE FileID=game.GameFileID)
                FROM GameFiles game
                WHERE game.GameFileID=$id;
                """;
            empty.Parameters.AddWithValue("$id", old.GameFileId);
            var usage = Convert.ToInt32(
                await empty.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
            if (usage != 0)
                continue;
            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM GameFiles WHERE GameFileID=$id;";
            delete.Parameters.AddWithValue("$id", old.GameFileId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
            removed.Add(old.Title.Length == 0 ? old.Reference : old.Title);
        }
        return (reconciled, removed);
    }

    private static int ScoreIwadTitle(string legacy, params string[] candidates)
    {
        var left = NormalizeName(legacy);
        if (left.Length == 0)
            return 0;
        var leftWords = left.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return candidates
            .Select(NormalizeName)
            .Where(candidate => candidate.Length > 0)
            .Select(candidate =>
            {
                if (candidate.Equals(left, StringComparison.OrdinalIgnoreCase))
                    return 1000;
                var words = candidate.Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                var overlap = leftWords.Intersect(words).Count();
                var union = leftWords.Union(words).Count();
                return (candidate.Contains(left, StringComparison.OrdinalIgnoreCase)
                        || left.Contains(candidate, StringComparison.OrdinalIgnoreCase)
                            ? 200
                            : 0)
                    + (union == 0 ? 0 : overlap * 100 / union);
            })
            .DefaultIfEmpty(0)
            .Max();
    }

    private static async Task MergeSingleGameFileRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string valueColumn,
        int legacyId,
        int activeId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
             INSERT INTO {table} (GameFileID, {valueColumn})
             SELECT $legacy, MAX({valueColumn})
             FROM {table}
             WHERE GameFileID IN ($legacy, $active)
             HAVING COUNT(*) > 0
             ON CONFLICT(GameFileID) DO UPDATE SET
                 {valueColumn}=MAX({table}.{valueColumn}, excluded.{valueColumn});
             DELETE FROM {table} WHERE GameFileID=$active;
             """;
        command.Parameters.AddWithValue("$legacy", legacyId);
        command.Parameters.AddWithValue("$active", activeId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MoveOptionalGameFileRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        int legacyId,
        int activeId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
             DELETE FROM {table}
             WHERE GameFileID=$active
               AND EXISTS (SELECT 1 FROM {table} WHERE GameFileID=$legacy);
             UPDATE {table} SET GameFileID=$legacy WHERE GameFileID=$active;
             """;
        command.Parameters.AddWithValue("$legacy", legacyId);
        command.Parameters.AddWithValue("$active", activeId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IEnumerable<string> EnumerateFiles(
        string directory,
        IReadOnlySet<string> extensions)
    {
        if (!Directory.Exists(directory))
            return [];
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(file => extensions.Contains(Path.GetExtension(file)))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase);
    }

    private static string? FindSourcePortExecutable(string directory)
    {
        return Directory.EnumerateFiles(directory, "*.exe", SearchOption.AllDirectories)
            .Where(path =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                return !name.Contains("unins", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("setup", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("crash", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("updater", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith("server", StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(path => ScoreExecutable(directory, path))
            .ThenBy(path => path.Length)
            .FirstOrDefault();
    }

    private static int ScoreExecutable(string directory, string path)
    {
        var folder = NormalizeName(Path.GetFileName(directory));
        var executable = NormalizeName(Path.GetFileNameWithoutExtension(path));
        var score = executable.Equals(folder, StringComparison.OrdinalIgnoreCase)
            ? 100
            : 0;
        if (IsZDoomFamily(executable)
            || executable.Contains("doom", StringComparison.OrdinalIgnoreCase)
            || executable.Contains("eternity", StringComparison.OrdinalIgnoreCase)
            || executable.Contains("vavoom", StringComparison.OrdinalIgnoreCase)
            || executable.Contains("3dge", StringComparison.OrdinalIgnoreCase))
        {
            score += 70;
        }
        if (Path.GetDirectoryName(path)!.Equals(
                directory,
                StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }
        return score;
    }

    private static string NormalizeName(string value) =>
        new(value.Where(char.IsLetterOrDigit).ToArray());

    private static bool IsZDoomFamily(string value) =>
        value.Contains("gzdoom", StringComparison.OrdinalIgnoreCase)
        || value.Contains("uzdoom", StringComparison.OrdinalIgnoreCase)
        || value.Contains("vkdoom", StringComparison.OrdinalIgnoreCase)
        || value.Contains("lzdoom", StringComparison.OrdinalIgnoreCase)
        || value.Contains("qzdoom", StringComparison.OrdinalIgnoreCase)
        || value.Equals("zdoom", StringComparison.OrdinalIgnoreCase)
        || value.Contains("zandronum", StringComparison.OrdinalIgnoreCase)
        || value.Contains("skulltag", StringComparison.OrdinalIgnoreCase);

    private static string ReadExecutableVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return DatabaseTextSanitizer.SingleLine(
                string.IsNullOrWhiteSpace(info.ProductVersion)
                    ? info.FileVersion
                    : info.ProductVersion);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<SqliteConnection> OpenAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = true,
            DefaultTimeout = 5,
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await WinUiDatabaseSchema.EnsureAsync(connection, cancellationToken);
        return connection;
    }

    private static async Task<string> GetGameFilesRootAsync(
        SqliteConnection connection,
        string databasePath,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Value FROM Configuration WHERE Name='GameFileDirectory' LIMIT 1;";
        var configured = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture)
            ?? "Data\\";
        configured = Environment.ExpandEnvironmentVariables(configured);
        return Path.GetFullPath(
            Path.IsPathFullyQualified(configured)
                ? configured
                : Path.Combine(Path.GetDirectoryName(databasePath)!, configured));
    }

    private static string ToPortableDirectory(
        string databasePath,
        string directory)
    {
        var databaseDirectory = Path.GetDirectoryName(databasePath)!;
        var relative = Path.GetRelativePath(databaseDirectory, directory);
        return relative.TrimEnd('\\', '/') + "\\";
    }

    private static string? ResolveManagedReference(
        string databasePath,
        string gameFilesRoot,
        string managedDirectory,
        string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;
        var expanded = Environment.ExpandEnvironmentVariables(reference.Trim());
        var candidates = Path.IsPathFullyQualified(expanded)
            ? new[] { Path.GetFullPath(expanded) }
            :
            [
                Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(databasePath)!,
                    expanded)),
                Path.GetFullPath(Path.Combine(gameFilesRoot, expanded)),
            ];
        return candidates.FirstOrDefault(candidate =>
            IsPathWithin(candidate, managedDirectory));
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
            SET Value=$value
            WHERE Name=$name;
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$value", value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) > 0)
            return;
        command.CommandText =
            """
            INSERT INTO Configuration (Name, Value, AvailableValues, UserCanModify)
            VALUES ($name, $value, '', 1);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
