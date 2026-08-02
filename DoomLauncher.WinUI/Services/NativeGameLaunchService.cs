using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DoomLauncher.Modern.Core.Launch;
using Microsoft.Data.Sqlite;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace DoomLauncher.WinUI.Services;

public sealed class NativeGameLaunchService(
    IDoomLauncherDatabaseLocator databaseLocator,
    IProcessStarter processStarter) : ILaunchService
{
    private static readonly HashSet<string> DehackedExtensions =
        new([".deh", ".bex"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ScreenshotExtensions =
        new([".png", ".jpg", ".jpeg", ".bmp"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ZDoomSaveExtensions =
        new([".zds"], StringComparer.OrdinalIgnoreCase);

    public async Task<GameLaunchResult> LaunchAsync(
        GameLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = await BuildPlanAsync(request, cancellationToken);
        var beforeScreenshots = SnapshotFiles(
            plan.ScreenshotCaptureDirectories,
            plan.ScreenshotExtensions);
        var beforeSaveGames = SnapshotFiles(
            plan.StatisticsCaptureDirectories,
            plan.SaveGameExtensions);
        var startedAt = DateTimeOffset.Now;

        var startInfo = new ProcessStartInfo
        {
            FileName = plan.ExecutablePath,
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = false,
        };
        foreach (var argument in plan.Arguments)
            startInfo.ArgumentList.Add(argument);

        var innerSession = processStarter.Start(startInfo);
        await SetLastPlayedAsync(request.GameFileId, startedAt, cancellationToken);
        var session = new NativeGameLaunchSession(
            innerSession,
            async () =>
            {
                try
                {
                    await CompleteSessionAsync(
                        plan,
                        request.GameFileId,
                        startedAt,
                        beforeScreenshots,
                        beforeSaveGames);
                }
                finally
                {
                    TryDeleteDirectory(plan.SessionDirectory);
                }
            });
        return new GameLaunchResult(
            session,
            $"{request.DisplayName} wurde nativ mit {plan.SourcePortName} gestartet.");
    }

    private async Task<NativeLaunchPlan> BuildPlanAsync(
        GameLaunchRequest request,
        CancellationToken cancellationToken)
    {
        var databasePath = databaseLocator.FindDatabase();
        await using var connection = await OpenAsync(databasePath, readOnly: true, cancellationToken);
        var configuration = await LoadConfigurationAsync(connection, cancellationToken);
        var game = await LoadGameAsync(connection, request.GameFileId, cancellationToken);

        var sourcePortId = request.SourcePortId
            ?? game.SourcePortId
            ?? ParseInt(configuration.GetValueOrDefault("DefaultSourcePort"));
        var iwadId = request.IwadId
            ?? game.IwadId
            ?? ParseInt(configuration.GetValueOrDefault("DefaultIWad"));
        if (!sourcePortId.HasValue)
            throw new InvalidOperationException("Kein Sourceport wurde definiert.");
        if (!iwadId.HasValue)
            throw new InvalidOperationException("Kein IWAD wurde definiert.");

        var sourcePort = await LoadSourcePortAsync(
            connection,
            sourcePortId.Value,
            cancellationToken);
        var selectedIwad = await LoadIwadAsync(
            connection,
            iwadId.Value,
            cancellationToken);
        var isDeathkings = selectedIwad.InternalFileName.Equals(
            "HEXDD.WAD",
            StringComparison.OrdinalIgnoreCase);
        var baseIwad = isDeathkings
            ? await LoadIwadByInternalNameAsync(
                connection,
                "HEXEN.WAD",
                cancellationToken)
                ?? throw new InvalidOperationException(
                    "HEXDD.WAD benötigt HEXEN.WAD. Bitte zuerst HEXEN.WAD als IWAD definieren.")
            : selectedIwad;
        var databaseDirectory = Path.GetDirectoryName(databasePath)!;
        var gameDirectory = ResolvePath(
            databaseDirectory,
            configuration.GetValueOrDefault("GameFileDirectory", "Data"));
        var tempDirectory = ResolvePath(
            databaseDirectory,
            configuration.GetValueOrDefault("TempDirectory", "Data\\Temp"));
        var screenshotDirectory = ResolvePath(
            databaseDirectory,
            configuration.GetValueOrDefault(
                "ScreenshotDirectory",
                "Data\\Screenshots"));
        var sourcePortDirectory = ResolvePath(databaseDirectory, sourcePort.Directory);
        var executablePath = Path.Combine(sourcePortDirectory, sourcePort.Executable);
        if (!File.Exists(executablePath))
            throw new FileNotFoundException("Die Sourceport-EXE wurde nicht gefunden.", executablePath);

        var sessionDirectory = Path.Combine(
            tempDirectory,
            "WinUI-Sessions",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionDirectory);

        try
        {
            var supportedExtensions = SplitValues(sourcePort.SupportedExtensions)
                .Select(NormalizeExtension)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var iwadPath = await PrepareIwadAsync(
                databaseDirectory,
                gameDirectory,
                sessionDirectory,
                baseIwad,
                cancellationToken);

            var storedSettings = MergeSettings(game);
            var settings = storedSettings with
            {
                Map = request.Map ?? storedSettings.Map,
                Skill = request.Skill ?? storedSettings.Skill,
            };
            var launchFiles = new List<string>();
            if (isDeathkings)
            {
                launchFiles.Add(await PrepareIwadAsync(
                    databaseDirectory,
                    gameDirectory,
                    sessionDirectory,
                    selectedIwad,
                    cancellationToken));
            }
            if (game.GameFileId != selectedIwad.GameFileId)
            {
                launchFiles.AddRange(await PrepareGameArchiveAsync(
                    Path.Combine(gameDirectory, game.FileName),
                    sessionDirectory,
                    supportedExtensions,
                    SplitValues(settings.SpecificFiles),
                    cancellationToken));
            }

            var additionalNames = SplitValues(settings.Files)
                .Concat(SplitValues(settings.FilesSourcePort))
                .Concat(SplitValues(settings.FilesIwad))
                .Concat(SplitValues(sourcePort.SettingsFiles))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var additionalName in additionalNames)
            {
                var additional = await FindGameFileAsync(
                    connection,
                    additionalName,
                    cancellationToken);
                if (additional is null || additional.GameFileId == game.GameFileId)
                    continue;
                launchFiles.AddRange(await PrepareGameArchiveAsync(
                    Path.Combine(gameDirectory, additional.FileName),
                    sessionDirectory,
                    supportedExtensions,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    cancellationToken));
            }

            var arguments = BuildArguments(
                sourcePort,
                iwadPath,
                launchFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                settings).ToList();
            var screenshotCaptureDirectories =
                sourcePort.ScreenshotSupport.Equals(
                    "None",
                    StringComparison.OrdinalIgnoreCase)
                    ? []
                    : BuildCaptureDirectories(
                        sourcePortDirectory,
                        sourcePort.Name,
                        sourcePort.ScreenshotDirectories,
                        configuration.GetValueOrDefault("ScreenshotCaptureDirectories"),
                        includeKnownDefaults: sourcePort.ScreenshotSupport.Equals(
                            "Auto",
                            StringComparison.OrdinalIgnoreCase));
            var statisticsCaptureDirectories = sourcePort.StatisticsAdapter.Equals(
                    "None",
                    StringComparison.OrdinalIgnoreCase)
                ? []
                : BuildCaptureDirectories(
                    sourcePortDirectory,
                    sourcePort.Name,
                        sourcePort.StatisticsDirectories,
                        configuration.GetValueOrDefault("ScreenshotCaptureDirectories"),
                    includeKnownDefaults: true);
            return new NativeLaunchPlan(
                databasePath,
                executablePath,
                sourcePortDirectory,
                sourcePort.Name,
                sourcePort.SourcePortId,
                arguments,
                sessionDirectory,
                screenshotDirectory,
                screenshotCaptureDirectories,
                statisticsCaptureDirectories,
                SplitValues(sourcePort.ScreenshotExtensions)
                    .Select(NormalizeExtension)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    is { Count: > 0 } screenshotExtensions
                        ? screenshotExtensions
                        : ScreenshotExtensions,
                SplitValues(sourcePort.SaveGameExtensions)
                    .Select(NormalizeExtension)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    is { Count: > 0 } saveGameExtensions
                        ? saveGameExtensions
                        : ZDoomSaveExtensions,
                sourcePort.StatisticsAdapter,
                ParseBool(configuration.GetValueOrDefault("ImportScreenshots"), true)
                    && !sourcePort.ScreenshotSupport.Equals(
                        "None",
                        StringComparison.OrdinalIgnoreCase),
                ParseBool(configuration.GetValueOrDefault("DeleteScreenshotsAfterImport"), false));
        }
        catch
        {
            TryDeleteDirectory(sessionDirectory);
            throw;
        }
    }

    private static IReadOnlyList<string> BuildArguments(
        SourcePortRow sourcePort,
        string iwadPath,
        IReadOnlyList<string> launchFiles,
        EffectiveSettings settings)
    {
        if (settings.ExtraParametersOnly)
            return Tokenize(settings.ExtraParameters);

        var arguments = new List<string> { "-iwad", iwadPath };
        var regularFiles = launchFiles
            .Where(path => !DehackedExtensions.Contains(Path.GetExtension(path)))
            .ToArray();
        var dehackedFiles = launchFiles
            .Where(path => DehackedExtensions.Contains(Path.GetExtension(path)))
            .ToArray();
        if (regularFiles.Length > 0)
        {
            arguments.Add("-file");
            arguments.AddRange(regularFiles);
        }
        if (dehackedFiles.Length > 0)
        {
            arguments.Add("-deh");
            arguments.AddRange(dehackedFiles);
        }
        if (!string.IsNullOrWhiteSpace(settings.Map))
            arguments.AddRange(BuildMapArguments(settings.Map));
        if (!string.IsNullOrWhiteSpace(settings.Skill))
        {
            arguments.Add("-skill");
            arguments.Add(settings.Skill);
        }
        arguments.AddRange(Tokenize(settings.ExtraParameters));
        arguments.AddRange(Tokenize(sourcePort.ExtraParameters));
        return arguments;
    }

    private static IReadOnlyList<string> BuildMapArguments(string map)
    {
        map = DatabaseTextSanitizer.SingleLine(map).ToUpperInvariant();
        if (System.Text.RegularExpressions.Regex.IsMatch(map, "^E\\dM\\d$"))
            return ["-warp", map[1].ToString(), map[3].ToString()];
        if (System.Text.RegularExpressions.Regex.IsMatch(map, "^MAP\\d\\d$"))
            return ["-warp", int.Parse(map[3..], CultureInfo.InvariantCulture).ToString()];
        return ["+map", map];
    }

    private async Task CompleteSessionAsync(
        NativeLaunchPlan plan,
        int gameFileId,
        DateTimeOffset startedAt,
        IReadOnlyDictionary<string, FileStamp> beforeScreenshots,
        IReadOnlyDictionary<string, FileStamp> beforeSaveGames)
    {
        var endedAt = DateTimeOffset.Now;
        var elapsedMinutes = Convert.ToInt32((endedAt - startedAt).TotalMinutes);
        await using var connection = await OpenAsync(
            plan.DatabasePath,
            readOnly: false,
            CancellationToken.None);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                UPDATE GameFiles
                SET MinutesPlayed = COALESCE(MinutesPlayed, 0) + $minutes,
                    LastPlayed = $lastPlayed,
                    IsSyncNeeded = 1
                WHERE GameFileID = $gameFileId;
                """;
            command.Parameters.AddWithValue("$minutes", elapsedMinutes);
            command.Parameters.AddWithValue(
                "$lastPlayed",
                endedAt.LocalDateTime.ToString(
                    "yyyy-MM-dd HH:mm:ss.fffffff",
                    CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$gameFileId", gameFileId);
            await command.ExecuteNonQueryAsync();
        }

        var changedSaveGames = FindChangedFiles(
            plan.StatisticsCaptureDirectories,
            plan.SaveGameExtensions,
            beforeSaveGames);
        if (plan.StatisticsAdapter.Equals(
                "ZDoomSave",
                StringComparison.OrdinalIgnoreCase))
        {
            foreach (var saveGame in changedSaveGames)
            {
                await TryImportZDoomStatisticsAsync(
                    connection,
                    saveGame,
                    gameFileId,
                    plan.SourcePortId);
            }
        }

        if (!plan.ImportScreenshots)
            return;
        var newScreenshots = FindNewScreenshots(
            plan.ScreenshotCaptureDirectories,
            plan.ScreenshotExtensions,
            beforeScreenshots);
        if (newScreenshots.Count == 0)
            return;

        Directory.CreateDirectory(plan.ScreenshotDirectory);
        await using var transaction = await connection.BeginTransactionAsync();
        var nextOrder = 0;
        await using (var order = connection.CreateCommand())
        {
            order.Transaction = (SqliteTransaction)transaction;
            order.CommandText =
                """
                SELECT COALESCE(MAX(FileOrder), -1) + 1
                FROM Files
                WHERE GameFileID=$gameFileId AND FileTypeID=1;
                """;
            order.Parameters.AddWithValue("$gameFileId", gameFileId);
            nextOrder = Convert.ToInt32(
                await order.ExecuteScalarAsync(),
                CultureInfo.InvariantCulture);
        }
        foreach (var screenshot in newScreenshots)
        {
            var destinationName =
                $"{Guid.NewGuid():N}{Path.GetExtension(screenshot).ToLowerInvariant()}";
            var destinationPath = Path.Combine(plan.ScreenshotDirectory, destinationName);
            File.Copy(screenshot, destinationPath, overwrite: false);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT INTO Files
                    (GameFileID, FileName, DateCreated, FileTypeID, SourcePortID,
                     OriginalFileName, OriginalFilePath, FileOrder, IsMain)
                VALUES
                    ($gameFileId, $fileName, $created, 1, $sourcePortId,
                     $originalName, $originalPath, $fileOrder, 0);
                """;
            command.Parameters.AddWithValue("$gameFileId", gameFileId);
            command.Parameters.AddWithValue("$fileName", destinationName);
            command.Parameters.AddWithValue(
                "$created",
                File.GetCreationTime(screenshot).ToString(
                    "yyyy-MM-dd HH:mm:ss.fffffff",
                    CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$sourcePortId", plan.SourcePortId);
            command.Parameters.AddWithValue("$originalName", Path.GetFileName(screenshot));
            command.Parameters.AddWithValue("$originalPath", screenshot);
            command.Parameters.AddWithValue("$fileOrder", nextOrder++);
            await command.ExecuteNonQueryAsync();
            if (plan.DeleteImportedScreenshots)
                File.Delete(screenshot);
        }
        await transaction.CommitAsync();
    }

    private async Task SetLastPlayedAsync(
        int gameFileId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(
            databaseLocator.FindDatabase(),
            readOnly: false,
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE GameFiles
            SET LastPlayed = $lastPlayed, IsSyncNeeded = 1
            WHERE GameFileID = $gameFileId;
            """;
        command.Parameters.AddWithValue(
            "$lastPlayed",
            startedAt.LocalDateTime.ToString(
                "yyyy-MM-dd HH:mm:ss.fffffff",
                CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$gameFileId", gameFileId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> PrepareGameArchiveAsync(
        string archivePath,
        string sessionDirectory,
        IReadOnlySet<string> supportedExtensions,
        IReadOnlySet<string> specificFiles,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(archivePath) && !Directory.Exists(archivePath))
            throw new FileNotFoundException("Eine Mod-Datei wurde nicht gefunden.", archivePath);
        if (Directory.Exists(archivePath))
            return [archivePath];
        var extension = Path.GetExtension(archivePath);
        if (supportedExtensions.Contains(extension))
            return [archivePath];

        var output = Path.Combine(
            sessionDirectory,
            Path.GetFileNameWithoutExtension(archivePath)
                + "-"
                + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(output);
        var result = new List<string>();
        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.Name)
                    || !ShouldExtract(entry.FullName, supportedExtensions, specificFiles))
                {
                    continue;
                }
                var destination = UniqueDestination(output, entry.Name);
                entry.ExtractToFile(destination);
                result.Add(destination);
            }
        }
        else if (extension.Equals(".7z", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".rar", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ShouldExtract(entry.Key ?? string.Empty, supportedExtensions, specificFiles))
                    continue;
                var entryName = Path.GetFileName(entry.Key ?? string.Empty);
                if (entryName.Length == 0)
                    continue;
                var destination = UniqueDestination(output, entryName);
                entry.WriteToFile(
                    destination,
                    new ExtractionOptions
                    {
                        ExtractFullPath = false,
                        Overwrite = false,
                    });
                result.Add(destination);
            }
        }
        else
        {
            throw new NotSupportedException(
                $"Das Archivformat {extension} kann nicht nativ gestartet werden.");
        }
        if (result.Count == 0)
            throw new InvalidDataException(
                $"Das Archiv {Path.GetFileName(archivePath)} enthält keine für den Sourceport geeigneten Dateien.");
        return result;
    }

    private static async Task<string> PrepareIwadAsync(
        string databaseDirectory,
        string gameDirectory,
        string sessionDirectory,
        IwadRow iwad,
        CancellationToken cancellationToken)
    {
        var configuredArchive = Environment.ExpandEnvironmentVariables(
            iwad.ArchiveFileName.Trim());
        var archivePath = Path.IsPathFullyQualified(configuredArchive)
            ? Path.GetFullPath(configuredArchive)
            : Path.GetFullPath(Path.Combine(gameDirectory, configuredArchive));
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("Das IWAD-Archiv wurde nicht gefunden.", archivePath);
        if (Path.GetFileName(archivePath).Equals(
                iwad.InternalFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            return archivePath;
        }

        var output = Path.Combine(sessionDirectory, "IWAD");
        Directory.CreateDirectory(output);
        if (Path.GetExtension(archivePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var entry = archive.Entries.FirstOrDefault(item =>
                Path.GetFileName(item.FullName).Equals(
                    iwad.InternalFileName,
                    StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                throw new InvalidDataException(
                    $"IWAD {iwad.InternalFileName} wurde in {iwad.ArchiveFileName} nicht gefunden.");
            var destination = Path.Combine(output, Path.GetFileName(iwad.InternalFileName));
            entry.ExtractToFile(destination);
            return destination;
        }

        using var compressed = ArchiveFactory.OpenArchive(archivePath);
        var compressedEntry = compressed.Entries.FirstOrDefault(item =>
            !item.IsDirectory
            && Path.GetFileName(item.Key ?? string.Empty).Equals(
                iwad.InternalFileName,
                StringComparison.OrdinalIgnoreCase));
        if (compressedEntry is null)
            throw new InvalidDataException(
                $"IWAD {iwad.InternalFileName} wurde in {iwad.ArchiveFileName} nicht gefunden.");
        var extracted = Path.Combine(output, Path.GetFileName(iwad.InternalFileName));
        compressedEntry.WriteToFile(
            extracted,
            new ExtractionOptions { ExtractFullPath = false, Overwrite = false });
        await Task.CompletedTask;
        return extracted;
    }

    private static bool ShouldExtract(
        string entryName,
        IReadOnlySet<string> supportedExtensions,
        IReadOnlySet<string> specificFiles)
    {
        if (specificFiles.Count > 0)
            return specificFiles.Any(file =>
                file.Equals(entryName, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(file).Equals(
                    Path.GetFileName(entryName),
                    StringComparison.OrdinalIgnoreCase));
        return supportedExtensions.Contains(Path.GetExtension(entryName));
    }

    private static string UniqueDestination(string directory, string fileName)
    {
        fileName = Path.GetFileName(fileName);
        var destination = Path.Combine(directory, fileName);
        for (var suffix = 2; File.Exists(destination); suffix++)
        {
            destination = Path.Combine(
                directory,
                $"{Path.GetFileNameWithoutExtension(fileName)}-{suffix}{Path.GetExtension(fileName)}");
        }
        return destination;
    }

    private static IReadOnlyDictionary<string, FileStamp> SnapshotFiles(
        IReadOnlyList<string> directories,
        IReadOnlySet<string> extensions)
    {
        var result = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumerateFiles(directories, extensions))
        {
            var info = new FileInfo(path);
            result[path] = new FileStamp(info.Length, info.LastWriteTimeUtc);
        }
        return result;
    }

    private static IReadOnlyList<string> FindChangedFiles(
        IReadOnlyList<string> directories,
        IReadOnlySet<string> extensions,
        IReadOnlyDictionary<string, FileStamp> before)
    {
        var result = new List<string>();
        foreach (var path in EnumerateFiles(directories, extensions))
        {
            var info = new FileInfo(path);
            var stamp = new FileStamp(info.Length, info.LastWriteTimeUtc);
            if (!before.TryGetValue(path, out var previous) || previous != stamp)
                result.Add(path);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> FindNewScreenshots(
        IReadOnlyList<string> directories,
        IReadOnlySet<string> extensions,
        IReadOnlyDictionary<string, FileStamp> before)
    {
        var result = new List<string>();
        foreach (var path in EnumerateFiles(directories, extensions))
        {
            var info = new FileInfo(path);
            var stamp = new FileStamp(info.Length, info.LastWriteTimeUtc);
            if (!before.TryGetValue(path, out var previous) || previous != stamp)
                result.Add(path);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> BuildCaptureDirectories(
        string sourcePortDirectory,
        string sourcePortName,
        string? capabilityDirectories,
        string? globallyConfiguredDirectories,
        bool includeKnownDefaults)
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var engineNames = new[]
        {
            sourcePortName,
            "GZDoom",
            "UZDoom",
            "VKDoom",
            "LZDoom",
            "QZDoom",
            "ZDoom",
            "Zandronum",
            "Skulltag",
            "Eternity",
            "Doom Retro",
            "Woof",
            "DSDA-Doom",
            "Crispy Doom",
            "Chocolate Doom",
        }.Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var configured = SplitValues(capabilityDirectories)
            .Concat(SplitValues(globallyConfiguredDirectories));
        var defaults = new List<string> { sourcePortDirectory };
        if (includeKnownDefaults)
        {
            defaults.AddRange(engineNames.Select(name =>
                Path.Combine(user, "Pictures", "Screenshots", name)));
            defaults.AddRange(engineNames.Select(name =>
                Path.Combine(documents, "My Games", name)));
            defaults.AddRange(engineNames.Select(name =>
                Path.Combine(user, "Saved Games", name)));
            defaults.AddRange(engineNames.Select(name =>
                Path.Combine(appData, name)));
            defaults.AddRange(engineNames.Select(name =>
                Path.Combine(localAppData, name)));
        }
        return configured
            .Concat(defaults)
            .Select(path => Environment.ExpandEnvironmentVariables(path))
            .Select(path => Path.IsPathFullyQualified(path)
                ? path
                : Path.Combine(sourcePortDirectory, path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> EnumerateFiles(
        IEnumerable<string> directories,
        IReadOnlySet<string> extensions)
    {
        var result = new List<string>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
        };
        foreach (var directory in directories
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory))
                continue;
            try
            {
                foreach (var path in Directory.EnumerateFiles(directory, "*", options))
                {
                    if (extensions.Contains(Path.GetExtension(path)))
                        result.Add(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task TryImportZDoomStatisticsAsync(
        SqliteConnection connection,
        string saveGamePath,
        int gameFileId,
        int sourcePortId)
    {
        try
        {
            using var archive = ZipFile.OpenRead(saveGamePath);
            var globals = archive.Entries.FirstOrDefault(entry =>
                entry.Name.Equals("globals.json", StringComparison.OrdinalIgnoreCase));
            if (globals is null)
                return;
            await using var stream = globals.Open();
            using var document = await JsonDocument.ParseAsync(stream);
            if (!document.RootElement.TryGetProperty("statistics", out var statistics)
                || !statistics.TryGetProperty("levels", out var levels)
                || levels.ValueKind != JsonValueKind.Array)
            {
                return;
            }
            var skill = TryReadZDoomSkill(document.RootElement);
            foreach (var level in levels.EnumerateArray())
            {
                var mapName = JsonString(level, "levelname");
                if (string.IsNullOrWhiteSpace(mapName))
                    continue;
                var killCount = JsonInt(level, "killcount");
                var totalKills = JsonInt(level, "totalkills");
                var secretCount = JsonInt(level, "secretcount");
                var totalSecrets = JsonInt(level, "totalsecrets");
                var itemCount = JsonInt(level, "itemcount");
                var totalItems = JsonInt(level, "totalitems");
                var levelTime = JsonInt(level, "leveltime") / 35d;
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO Stats
                        (GameFileID, KillCount, TotalKills, SecretCount,
                         TotalSecrets, LevelTime, ItemCount, TotalItems,
                         SourcePortID, MapName, RecordTime, Skill)
                    SELECT $gameFileId, $killCount, $totalKills, $secretCount,
                           $totalSecrets, $levelTime, $itemCount, $totalItems,
                           $sourcePortId, $mapName, $recordTime, $skill
                    WHERE NOT EXISTS (
                        SELECT 1 FROM Stats
                        WHERE GameFileID=$gameFileId
                          AND SourcePortID=$sourcePortId
                          AND MapName=$mapName
                          AND KillCount=$killCount
                          AND TotalKills=$totalKills
                          AND SecretCount=$secretCount
                          AND TotalSecrets=$totalSecrets
                          AND ItemCount=$itemCount
                          AND TotalItems=$totalItems
                    );
                    """;
                command.Parameters.AddWithValue("$gameFileId", gameFileId);
                command.Parameters.AddWithValue("$killCount", killCount);
                command.Parameters.AddWithValue("$totalKills", totalKills);
                command.Parameters.AddWithValue("$secretCount", secretCount);
                command.Parameters.AddWithValue("$totalSecrets", totalSecrets);
                command.Parameters.AddWithValue("$levelTime", levelTime);
                command.Parameters.AddWithValue("$itemCount", itemCount);
                command.Parameters.AddWithValue("$totalItems", totalItems);
                command.Parameters.AddWithValue("$sourcePortId", sourcePortId);
                command.Parameters.AddWithValue("$mapName", mapName);
                command.Parameters.AddWithValue(
                    "$recordTime",
                    File.GetLastWriteTime(saveGamePath).ToString(
                        "yyyy-MM-dd HH:mm:ss.fffffff",
                        CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$skill", skill ?? (object)DBNull.Value);
                await command.ExecuteNonQueryAsync();
            }
        }
        catch (Exception exception)
            when (exception is IOException
                or InvalidDataException
                or JsonException
                or SqliteException)
        {
            // Statistics are optional. A malformed or still-locked save game
            // must never turn a successful play session into a launcher error.
        }
    }

    private static int? TryReadZDoomSkill(JsonElement root)
    {
        if (root.TryGetProperty("servercvars", out var cvars)
            && cvars.ValueKind == JsonValueKind.Object
            && cvars.TryGetProperty("skill", out var skill))
        {
            if (skill.TryGetInt32(out var numeric))
                return numeric + 1;
            if (int.TryParse(skill.ToString(), out numeric))
                return numeric + 1;
        }
        if (root.TryGetProperty("importantcvars", out var important))
        {
            var parts = important.ToString().Split('\\');
            for (var index = 0; index + 1 < parts.Length; index++)
            {
                if (parts[index].Equals("skill", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(parts[index + 1], out var numeric))
                    return numeric + 1;
            }
        }
        return null;
    }

    private static int JsonInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return 0;
        if (value.TryGetInt32(out var numeric))
            return numeric;
        return int.TryParse(value.ToString(), out numeric) ? numeric : 0;
    }

    private static string JsonString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
            ? value.ToString()
            : string.Empty;

    private static EffectiveSettings MergeSettings(GameRow game) =>
        new(
            game.Map,
            game.Skill,
            game.ExtraParameters,
            game.Files,
            game.FilesSourcePort,
            game.FilesIwad,
            game.SpecificFiles,
            game.ExtraParametersOnly);

    private static IReadOnlyList<string> Tokenize(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return [];
        var result = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        foreach (var character in commandLine)
        {
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (value.Length > 0)
                {
                    result.Add(value.ToString());
                    value.Clear();
                }
                continue;
            }
            value.Append(character);
        }
        if (value.Length > 0)
            result.Add(value.ToString());
        return result;
    }

    private static HashSet<string> SplitValues(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(
                    [';', ','],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(DatabaseTextSanitizer.SingleLine)
                .Where(item => item.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeExtension(string extension) =>
        extension.StartsWith('.') ? extension : "." + extension;

    private static string ResolvePath(string root, string path)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim());
        return Path.GetFullPath(Path.IsPathFullyQualified(path)
            ? path
            : Path.Combine(root, path));
    }

    private static bool ParseBool(string? value, bool defaultValue) =>
        bool.TryParse(value, out var parsed) ? parsed : defaultValue;

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static async Task<SqliteConnection> OpenAsync(
        string databasePath,
        bool readOnly,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = true,
            DefaultTimeout = 5,
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
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
            result[reader.GetString(0)] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        return result;
    }

    private static async Task<GameRow> LoadGameAsync(
        SqliteConnection connection,
        int gameFileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT GameFileID, FileName, SourcePortID, IWadID,
                   SettingsMap, SettingsSkill, SettingsExtraParams, SettingsFiles,
                   SettingsFilesSourcePort, SettingsFilesIWAD, SettingsSpecificFiles,
                   COALESCE(SettingsExtraParamsOnly, 0)
            FROM GameFiles WHERE GameFileID = $id;
            """;
        command.Parameters.AddWithValue("$id", gameFileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException($"GameFileID {gameFileId} wurde nicht gefunden.");
        return new GameRow(
            reader.GetInt32(0),
            reader.GetString(1),
            NullableInt(reader, 2),
            NullableInt(reader, 3),
            NullableString(reader, 4),
            NullableString(reader, 5),
            NullableString(reader, 6),
            NullableString(reader, 7),
            NullableString(reader, 8),
            NullableString(reader, 9),
            NullableString(reader, 10),
            reader.GetInt32(11) != 0);
    }

    private static async Task<SourcePortRow> LoadSourcePortAsync(
        SqliteConnection connection,
        int sourcePortId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT source.SourcePortID, source.Name, source.Executable,
                   source.SupportedExtensions, source.Directory,
                   source.SettingsFiles, source.FileOption, source.ExtraParameters,
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
            WHERE source.SourcePortID = $id
              AND COALESCE(source.Archived, 0) = 0;
            """;
        command.Parameters.AddWithValue("$id", sourcePortId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException($"Sourceport {sourcePortId} wurde nicht gefunden.");
        return new SourcePortRow(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            NullableString(reader, 5),
            NullableString(reader, 6),
            NullableString(reader, 7),
            NullableString(reader, 8) ?? "Auto",
            NullableString(reader, 9) ?? string.Empty,
            NullableString(reader, 10) ?? ".png,.jpg,.jpeg,.bmp",
            NullableString(reader, 11) ?? string.Empty,
            NullableString(reader, 12) ?? "None",
            NullableString(reader, 13) ?? string.Empty,
            NullableString(reader, 14) ?? ".zds");
    }

    private static async Task<IwadRow> LoadIwadAsync(
        SqliteConnection connection,
        int iwadId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT i.IWadID, i.GameFileID, i.FileName, gf.FileName
            FROM IWads i
            JOIN GameFiles gf ON gf.GameFileID = i.GameFileID
            WHERE i.IWadID = $id;
            """;
        command.Parameters.AddWithValue("$id", iwadId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException($"IWAD {iwadId} wurde nicht gefunden.");
        return new IwadRow(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3));
    }

    private static async Task<IwadRow?> LoadIwadByInternalNameAsync(
        SqliteConnection connection,
        string internalFileName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT i.IWadID, i.GameFileID, i.FileName, gf.FileName
            FROM IWads i
            JOIN GameFiles gf ON gf.GameFileID = i.GameFileID
            WHERE i.FileName = $fileName COLLATE NOCASE
            ORDER BY i.IWadID
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$fileName", internalFileName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new IwadRow(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3))
            : null;
    }

    private static async Task<GameFileReference?> FindGameFileAsync(
        SqliteConnection connection,
        string fileName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT GameFileID, FileName
            FROM GameFiles
            WHERE FileName = $fileName COLLATE NOCASE
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$fileName", fileName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new GameFileReference(reader.GetInt32(0), reader.GetString(1))
            : null;
    }

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? NullableInt(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Temp cleanup is best effort and must not hide the game result.
        }
    }

    private sealed record GameRow(
        int GameFileId,
        string FileName,
        int? SourcePortId,
        int? IwadId,
        string? Map,
        string? Skill,
        string? ExtraParameters,
        string? Files,
        string? FilesSourcePort,
        string? FilesIwad,
        string? SpecificFiles,
        bool ExtraParametersOnly);

    private sealed record SourcePortRow(
        int SourcePortId,
        string Name,
        string Executable,
        string SupportedExtensions,
        string Directory,
        string? SettingsFiles,
        string? FileOption,
        string? ExtraParameters,
        string ScreenshotSupport,
        string ScreenshotDirectories,
        string ScreenshotExtensions,
        string ScreenshotArgument,
        string StatisticsAdapter,
        string StatisticsDirectories,
        string SaveGameExtensions);

    private sealed record IwadRow(
        int IwadId,
        int GameFileId,
        string InternalFileName,
        string ArchiveFileName);

    private sealed record GameFileReference(int GameFileId, string FileName);

    private sealed record EffectiveSettings(
        string? Map,
        string? Skill,
        string? ExtraParameters,
        string? Files,
        string? FilesSourcePort,
        string? FilesIwad,
        string? SpecificFiles,
        bool ExtraParametersOnly);

    private sealed record NativeLaunchPlan(
        string DatabasePath,
        string ExecutablePath,
        string WorkingDirectory,
        string SourcePortName,
        int SourcePortId,
        IReadOnlyList<string> Arguments,
        string SessionDirectory,
        string ScreenshotDirectory,
        IReadOnlyList<string> ScreenshotCaptureDirectories,
        IReadOnlyList<string> StatisticsCaptureDirectories,
        IReadOnlySet<string> ScreenshotExtensions,
        IReadOnlySet<string> SaveGameExtensions,
        string StatisticsAdapter,
        bool ImportScreenshots,
        bool DeleteImportedScreenshots);

    private sealed record FileStamp(long Length, DateTime LastWriteTimeUtc);
}

internal sealed class NativeGameLaunchSession(
    IGameLaunchSession inner,
    Func<Task> onExited) : IGameLaunchSession
{
    public int ProcessId => inner.ProcessId;

    public async Task WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        await inner.WaitForExitAsync(cancellationToken);
        await onExited();
    }
}
