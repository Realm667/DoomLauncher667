using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DoomLauncher.WinUI.Services;

public interface IObsidianService
{
    string InstallDirectory { get; }

    Task<ObsidianInstallation> DetectAsync(
        CancellationToken cancellationToken = default);

    Task<ObsidianGenerationResult> GenerateAsync(
        ObsidianGenerationOptions options,
        IProgress<ObsidianGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class ObsidianService(
    IDoomLauncherDatabaseLocator databaseLocator,
    INativeLibraryService libraryService) : IObsidianService
{
    private static readonly Regex ProgressPattern = new(
        @"(?<!\d)(?<value>\d{1,3})(?:\.\d+)?\s*%",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SeedPattern = new(
        @"^\s*seed\s*=\s*(?<value>.+?)\s*$",
        RegexOptions.Compiled
        | RegexOptions.CultureInvariant
        | RegexOptions.IgnoreCase);

    public string InstallDirectory => Path.Combine(
        Path.GetDirectoryName(databaseLocator.FindDatabase())!,
        "Data",
        "Tools",
        "Obsidian");

    public Task<ObsidianInstallation> DetectAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(InstallDirectory);
        var executable = Directory
            .EnumerateFiles(
                InstallDirectory,
                "obsidian.exe",
                SearchOption.AllDirectories)
            .OrderBy(path => path.Count(character =>
                character == Path.DirectorySeparatorChar))
            .ThenByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (executable is null)
        {
            return Task.FromResult(new ObsidianInstallation(
                false,
                InstallDirectory,
                string.Empty,
                string.Empty));
        }

        var version = string.Empty;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(executable);
            version = DatabaseTextSanitizer.SingleLine(
                string.IsNullOrWhiteSpace(info.ProductVersion)
                    ? info.FileVersion
                    : info.ProductVersion);
        }
        catch
        {
            // Version resources are optional in community builds.
        }

        return Task.FromResult(new ObsidianInstallation(
            true,
            InstallDirectory,
            executable,
            version));
    }

    public async Task<ObsidianGenerationResult> GenerateAsync(
        ObsidianGenerationOptions options,
        IProgress<ObsidianGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (options.Seed <= 0)
        {
            options = options with
            {
                Seed = Random.Shared.Next(1, int.MaxValue),
            };
        }
        var installation = await DetectAsync(cancellationToken);
        if (!installation.IsAvailable)
        {
            throw new FileNotFoundException(
                "Obsidian was not found in the portable tools directory.",
                InstallDirectory);
        }

        progress?.Report(new ObsidianGenerationProgress(
            0.03,
            "Preparing Obsidian batch"));
        var databaseDirectory = Path.GetDirectoryName(databaseLocator.FindDatabase())!;
        var dataDirectory = Path.Combine(databaseDirectory, "Data");
        var workingDirectory = Path.Combine(dataDirectory, "Temp", "Obsidian");
        var homeDirectory = Path.Combine(dataDirectory, "UserState", "Obsidian");
        var generatedDirectory = Path.Combine(dataDirectory, "Mods", "Generated");
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(homeDirectory);
        Directory.CreateDirectory(generatedDirectory);

        var safeBaseName = SanitizeFileName(options.OutputName);
        if (new[] { ".wad", ".zip", ".pk3", ".pk7" }.Contains(
                Path.GetExtension(safeBaseName),
                StringComparer.OrdinalIgnoreCase))
        {
            safeBaseName = Path.GetFileNameWithoutExtension(safeBaseName);
        }
        if (string.IsNullOrWhiteSpace(safeBaseName))
        {
            safeBaseName = $"Obsidian-{DateTime.Now:yyyyMMdd-HHmmss}";
        }
        var temporaryOutputStem = $"{safeBaseName}-{Guid.NewGuid():N}";
        var temporaryOutput = Path.Combine(
            workingDirectory,
            temporaryOutputStem + ".wad");
        var logPath = Path.Combine(
            workingDirectory,
            $"obsidian-{Guid.NewGuid():N}.log");

        var outputLines = new Queue<string>();
        string? fatalOutput = null;
        string? actualSeed = null;
        var lastProgressReport = DateTimeOffset.MinValue;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = installation.ExecutablePath,
                WorkingDirectory = Path.GetDirectoryName(
                    installation.ExecutablePath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
            EnableRaisingEvents = true,
        };
        process.StartInfo.ArgumentList.Add("--home");
        process.StartInfo.ArgumentList.Add(homeDirectory);
        process.StartInfo.ArgumentList.Add("--install");
        process.StartInfo.ArgumentList.Add(Path.GetDirectoryName(
            installation.ExecutablePath)!);
        process.StartInfo.ArgumentList.Add("--log");
        process.StartInfo.ArgumentList.Add(logPath);
        process.StartInfo.ArgumentList.Add("--keep");
        process.StartInfo.ArgumentList.Add("--batch");
        process.StartInfo.ArgumentList.Add(temporaryOutput);
        process.StartInfo.ArgumentList.Add("--verbose");
        foreach (var setting in BuildBatchSettings(options))
            process.StartInfo.ArgumentList.Add(setting);

        void CaptureLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;
            lock (outputLines)
            {
                outputLines.Enqueue(line);
                while (outputLines.Count > 18)
                    outputLines.Dequeue();
            }
            var match = ProgressPattern.Match(line);
            var value = match.Success
                && double.TryParse(
                    match.Groups["value"].Value,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var parsed)
                ? Math.Clamp(parsed / 100d, 0, 1)
                : 0.35;
            var now = DateTimeOffset.UtcNow;
            if (match.Success || now - lastProgressReport >= TimeSpan.FromMilliseconds(200))
            {
                lastProgressReport = now;
                progress?.Report(new ObsidianGenerationProgress(
                    0.12 + (value * 0.68),
                    line.Trim()));
            }
            if (line.Contains("FAILED!", StringComparison.OrdinalIgnoreCase)
                || line.Contains("FATAL ERROR", StringComparison.OrdinalIgnoreCase)
                || line.Contains("OBSIDIAN ERROR", StringComparison.OrdinalIgnoreCase))
            {
                fatalOutput = line.Trim();
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
            }
            var seedMatch = SeedPattern.Match(line);
            if (seedMatch.Success)
                actualSeed = seedMatch.Groups["value"].Value.Trim();
        }

        process.OutputDataReceived += (_, args) => CaptureLine(args.Data);
        process.ErrorDataReceived += (_, args) => CaptureLine(args.Data);
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Obsidian could not be started.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            progress?.Report(new ObsidianGenerationProgress(
                0.12,
                "Generating level in Obsidian"));
            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
            });
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(fatalOutput))
            {
                string details;
                lock (outputLines)
                    details = string.Join(Environment.NewLine, outputLines);
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(details)
                        ? $"Obsidian exited with code {process.ExitCode}."
                        : details);
            }

            if (!File.Exists(temporaryOutput))
            {
                var candidate = Directory
                    .EnumerateFiles(workingDirectory, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => Path.GetFileNameWithoutExtension(path)
                            .Equals(
                                temporaryOutputStem,
                                StringComparison.OrdinalIgnoreCase)
                        && new[] { ".wad", ".zip", ".pk3", ".pk7" }
                        .Contains(
                            Path.GetExtension(path),
                            StringComparer.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (candidate is null)
                    throw new FileNotFoundException("Obsidian did not create an output WAD.");
                temporaryOutput = candidate;
            }

            progress?.Report(new ObsidianGenerationProgress(
                0.84,
                "Importing generated level"));
            var destinationPath = FindAvailablePath(
                generatedDirectory,
                safeBaseName,
                Path.GetExtension(temporaryOutput));
            File.Move(temporaryOutput, destinationPath);
            var import = await libraryService.ImportAsync(
                destinationPath,
                cancellationToken);
            await libraryService.TryImportTitlePicAsync(
                import.GameFileId,
                import.DestinationPath,
                cancellationToken);
            var generatedAt = DateTime.Now;
            var effectiveSeed = string.IsNullOrWhiteSpace(actualSeed)
                ? options.Seed.ToString(CultureInfo.InvariantCulture)
                : actualSeed;
            var mapCount = await libraryService.UpdateGeneratedMetadataAsync(
                import.GameFileId,
                import.DestinationPath,
                "Obsidian Level Maker",
                BuildGeneratedDescription(
                    options,
                    installation.Version,
                    effectiveSeed,
                    generatedAt),
                generatedAt,
                cancellationToken);
            await ApplyLaunchDefinitionsAsync(
                import.GameFileId,
                options,
                cancellationToken);
            await AddToGeneratedCollectionAsync(
                import.GameFileId,
                cancellationToken);
            progress?.Report(new ObsidianGenerationProgress(
                1,
                "Generated level imported"));
            return new ObsidianGenerationResult(
                import.GameFileId,
                import.FileName,
                import.DestinationPath,
                installation.Version,
                effectiveSeed,
                mapCount,
                generatedAt);
        }
        finally
        {
            TryDelete(logPath);
            TryDelete(temporaryOutput);
            foreach (var extension in new[] { ".zip", ".pk3", ".pk7" })
                TryDelete(Path.Combine(workingDirectory, temporaryOutputStem + extension));
        }
    }

    private async Task ApplyLaunchDefinitionsAsync(
        int gameFileId,
        ObsidianGenerationOptions options,
        CancellationToken cancellationToken)
    {
        var definitions = await libraryService.LoadLauncherDefinitionsAsync(
            cancellationToken);
        var iwadNames = options.Game switch
        {
            "doom1" or "ultdoom" => new[] { "DOOM.WAD", "The Ultimate Doom", "Doom" },
            "tnt" => new[] { "TNT.WAD", "TNT" },
            "plutonia" => new[] { "PLUTONIA.WAD", "Plutonia" },
            "heretic" => new[] { "HERETIC.WAD", "Heretic" },
            _ => new[] { "DOOM2.WAD", "Doom 2", "Doom II" },
        };
        var iwad = definitions.Iwads.FirstOrDefault(candidate =>
            iwadNames.Any(name =>
                candidate.InternalFileName.Equals(name, StringComparison.OrdinalIgnoreCase)
                || candidate.Name.Contains(name, StringComparison.OrdinalIgnoreCase)));
        NativeSourcePortDefinition? sourcePort = options.Port switch
        {
            "zdoom" => definitions.SourcePorts
                .Where(candidate =>
                    candidate.Name.Contains("ZDoom", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(candidate => candidate.Version)
                .FirstOrDefault(),
            "edge" => definitions.SourcePorts
                .Where(candidate =>
                    candidate.Name.Contains("EDGE", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(candidate => candidate.Version)
                .FirstOrDefault(),
            _ => definitions.SourcePorts
                .Where(candidate =>
                    candidate.Name.Contains("Boom", StringComparison.OrdinalIgnoreCase)
                    || candidate.Name.Contains("Eternity", StringComparison.OrdinalIgnoreCase)
                    || candidate.Name.Contains("PrBoom", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(candidate => candidate.Version)
                .FirstOrDefault(),
        };
        if (iwad is null && sourcePort is null)
            return;
        var game = await libraryService.LoadGameAsync(gameFileId, cancellationToken);
        await libraryService.UpdateGameAsync(
            game with
            {
                IwadId = iwad?.IwadId ?? game.IwadId,
                SourcePortId = sourcePort?.SourcePortId ?? game.SourcePortId,
            },
            cancellationToken);
    }

    private async Task AddToGeneratedCollectionAsync(
        int gameFileId,
        CancellationToken cancellationToken)
    {
        var collections = await libraryService.LoadGameCollectionsAsync(
            gameFileId,
            cancellationToken);
        var generated = collections.Collections.FirstOrDefault(collection =>
            collection.Name.Equals("Generated", StringComparison.OrdinalIgnoreCase));
        if (generated is null)
        {
            await libraryService.CreateCollectionAsync("Generated", cancellationToken);
            collections = await libraryService.LoadGameCollectionsAsync(
                gameFileId,
                cancellationToken);
            generated = collections.Collections.First(collection =>
                collection.Name.Equals("Generated", StringComparison.OrdinalIgnoreCase));
        }
        var selected = collections.SelectedTagIds.ToHashSet();
        selected.Add(generated.TagId);
        await libraryService.SaveGameCollectionsAsync(
            gameFileId,
            selected,
            null,
            cancellationToken);
    }

    internal static IReadOnlyList<string> BuildBatchSettings(
        ObsidianGenerationOptions options)
    {
        var seed = options.Seed > 0
            ? options.Seed
            : Random.Shared.Next(1, int.MaxValue);
        return
        [
            $"seed={seed}",
            $"game={options.Game}",
            $"port={options.Port}",
            $"length={options.Length}",
            $"theme={options.Theme}",
            $"float_size={options.MapSize}",
            $"outdoors={options.Outdoors}",
            $"caves={options.Caves}",
            $"liquids={options.Liquids}",
            $"hallways={options.Hallways}",
            $"steepness={options.Steepness}",
            $"float_mons={options.Monsters}",
            $"float_strength={options.MonsterStrength}",
            $"bosses={options.Bosses}",
            $"traps={options.Traps}",
            $"cages={options.Cages}",
            $"health={options.Health}",
            $"ammo={options.Ammo}",
            $"weapons={options.Weapons}",
            "@compress_output=0",
            $"@sky_generator={(options.GenerateSky ? 1 : 0)}",
            $"@music_swapper={(options.GenerateMusic ? 1 : 0)}",
        ];
    }

    internal static string BuildGeneratedDescription(
        ObsidianGenerationOptions options,
        string version,
        string seed,
        DateTime generatedAt)
    {
        var displayVersion = string.IsNullOrWhiteSpace(version)
            ? "unknown version"
            : version;
        var requestedSeed = options.Seed.ToString(CultureInfo.InvariantCulture);
        var seedDescription = seed.Equals(
            requestedSeed,
            StringComparison.OrdinalIgnoreCase)
            ? $"Seed: {seed}"
            : $"Seed: {seed}{Environment.NewLine}" +
              $"Requested seed: {requestedSeed} " +
              "(replaced by this Obsidian build during batch initialization)";
        return $$"""
            Generated with Obsidian Level Maker {{displayVersion}} on {{generatedAt:yyyy-MM-dd}}.

            {{seedDescription}}
            Game: {{options.Game}}
            Port: {{options.Port}}
            Length: {{options.Length}}
            Theme: {{options.Theme}}

            Architecture: map size={{options.MapSize}}; outdoors={{options.Outdoors}}; caves={{options.Caves}}; liquids={{options.Liquids}}; hallways={{options.Hallways}}; steepness={{options.Steepness}}
            Combat: monsters={{options.Monsters}}; strength={{options.MonsterStrength}}; bosses={{options.Bosses}}; traps={{options.Traps}}; cages={{options.Cages}}
            Pickups: health={{options.Health}}; ammo={{options.Ammo}}; weapons={{options.Weapons}}
            Modules: sky={{(options.GenerateSky ? "enabled" : "disabled")}}; music={{(options.GenerateMusic ? "enabled" : "disabled")}}
            """;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Trim()
            .Where(character => !invalid.Contains(character))
            .ToArray())
            .TrimEnd('.', ' ');
    }

    private static string FindAvailablePath(
        string directory,
        string baseName,
        string extension)
    {
        var path = Path.Combine(directory, baseName + extension);
        for (var suffix = 2; File.Exists(path); suffix++)
            path = Path.Combine(directory, $"{baseName} ({suffix}){extension}");
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}

public sealed record ObsidianInstallation(
    bool IsAvailable,
    string InstallDirectory,
    string ExecutablePath,
    string Version)
{
    public string DisplayVersion => string.IsNullOrWhiteSpace(Version)
        ? string.Empty
        : $"v{Version}";
}

public sealed record ObsidianGenerationOptions(
    string OutputName,
    string Game,
    string Port,
    string Length,
    string Theme,
    int Seed,
    string MapSize,
    string Outdoors,
    string Caves,
    string Liquids,
    string Hallways,
    string Steepness,
    string Monsters,
    string MonsterStrength,
    string Bosses,
    string Traps,
    string Cages,
    string Health,
    string Ammo,
    string Weapons,
    bool GenerateSky,
    bool GenerateMusic);

public sealed record ObsidianGenerationProgress(double Value, string Status);

public sealed record ObsidianGenerationResult(
    int GameFileId,
    string FileName,
    string DestinationPath,
    string ObsidianVersion,
    string Seed,
    int MapCount,
    DateTime GeneratedAt);
