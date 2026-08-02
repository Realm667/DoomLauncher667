using DoomLauncher.Modern.Core.Launch;
using DoomLauncher.WinUI.Services;
using Microsoft.Data.Sqlite;

if (args.Length == 1
    && args[0].Equals("--verify-first-setup", StringComparison.OrdinalIgnoreCase))
{
    await VerifyFirstSetupAsync();
    Console.WriteLine("FIRST_SETUP PASS");
    return 0;
}

if (args.Length == 2
    && args[0].Equals("--migrate-layout", StringComparison.OrdinalIgnoreCase))
{
    var targetDatabase = Path.GetFullPath(args[1]);
    Environment.SetEnvironmentVariable(
        DoomLauncherDatabaseLocator.DatabaseEnvironmentVariable,
        targetDatabase);
    try
    {
        var locator = new DoomLauncherDatabaseLocator();
        var service = new FirstSetupService(
            locator,
            new SqliteNativeLibraryService(locator));
        var result = await service.EnsureManagedLayoutAsync();
        Console.WriteLine(
            $"LAYOUT references={result.UpdatedReferences} moved={result.MovedFiles}");
        return 0;
    }
    finally
    {
        Environment.SetEnvironmentVariable(
            DoomLauncherDatabaseLocator.DatabaseEnvironmentVariable,
            null);
    }
}

if (args.Length == 2
    && args[0].Equals("--scan-iwads", StringComparison.OrdinalIgnoreCase))
{
    var targetDatabase = Path.GetFullPath(args[1]);
    Environment.SetEnvironmentVariable(
        DoomLauncherDatabaseLocator.DatabaseEnvironmentVariable,
        targetDatabase);
    try
    {
        var locator = new DoomLauncherDatabaseLocator();
        var service = new FirstSetupService(
            locator,
            new SqliteNativeLibraryService(locator));
        var result = await service.ScanIwadsAsync();
        Console.WriteLine(
            $"IWAD_SCAN imported={result.Imported} updated={result.Updated} " +
            $"removed={result.Removed} skipped={result.Skipped}");
        foreach (var removed in result.RemovedItems)
            Console.WriteLine($"IWAD_SCAN removed-item={removed}");
        foreach (var warning in result.Warnings)
            Console.WriteLine($"IWAD_SCAN warning={warning}");
        return result.Warnings.Count == 0 ? 0 : 1;
    }
    finally
    {
        Environment.SetEnvironmentVariable(
            DoomLauncherDatabaseLocator.DatabaseEnvironmentVariable,
            null);
    }
}

if (args.Length == 2
    && args[0].Equals(
        "--consolidate-generated-duplicates",
        StringComparison.OrdinalIgnoreCase))
{
    var targetDatabase = Path.GetFullPath(args[1]);
    Environment.SetEnvironmentVariable(
        DoomLauncherDatabaseLocator.DatabaseEnvironmentVariable,
        targetDatabase);
    try
    {
        var service = new SqliteNativeLibraryService(
            new DoomLauncherDatabaseLocator());
        var result = await service.ConsolidateGeneratedNameDuplicatesAsync();
        Console.WriteLine(
            $"DUPLICATES removed={result.RemovedEntries} renamed={result.RenamedEntries}");
        foreach (var fileName in result.RemovedFileNames)
            Console.WriteLine($"DUPLICATES removed-file={fileName}");
        foreach (var fileName in result.RenamedFileNames)
            Console.WriteLine($"DUPLICATES renamed-file={fileName}");
        return 0;
    }
    finally
    {
        Environment.SetEnvironmentVariable(
            DoomLauncherDatabaseLocator.DatabaseEnvironmentVariable,
            null);
    }
}

if (args.Length > 2)
{
    Console.Error.WriteLine(
        "Usage: DoomLauncher.Modern.Tests [DoomLauncher.sqlite] [expected-count] " +
        "| --migrate-layout DoomLauncher.sqlite");
    return 2;
}

var databasePath = args.Length >= 1 ? Path.GetFullPath(args[0]) : null;
var expectedCount = args.Length >= 2 ? int.Parse(args[1]) : (int?)null;
var failures = new List<string>();

void Check(bool condition, string message)
{
    if (condition)
        Console.WriteLine($"PASS {message}");
    else
    {
        Console.WriteLine($"FAIL {message}");
        failures.Add(message);
    }
}

try
{
    Check(
        DatabaseTextSanitizer.SingleLine("Alpha\t\t\tBeta") == "Alpha Beta",
        "Mehrere Tabulatoren werden zu einem Leerzeichen normalisiert");
    Check(
        DatabaseTextSanitizer.Multiline("Alpha\t\tBeta\r\nGamma")
            == $"Alpha Beta{Environment.NewLine}Gamma",
        "Mehrzeilige Importtexte werden ohne Tabulatoren gespeichert");
    Check(
        MapNameExtractor.ParseStored("MAP01-MAP03, E1M1")
            .SequenceEqual(["E1M1", "MAP01", "MAP02", "MAP03"]),
        "Map-Bereiche werden für die Startauswahl expandiert und sortiert");
    var statePath = Path.Combine(
        Path.GetTempPath(),
        $"DoomLauncher-State-{Guid.NewGuid():N}.json");
    Environment.SetEnvironmentVariable(
        JsonUserLibraryStateStore.StateEnvironmentVariable,
        statePath);
    try
    {
        var stateStore = new JsonUserLibraryStateStore();
        await stateStore.SaveAsync(UserLibraryState.Empty with
        {
            CollapsedCollectionNames = new HashSet<string>(
                ["Cacowards 2019"],
                StringComparer.OrdinalIgnoreCase),
            CollectionArtworkPaths = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["Cacowards 2019"] =
                    Path.Combine("Data", "CollectionArtworks", "cacowards.png"),
            },
            WindowWidth = 1280,
            WindowHeight = 760,
        });
        var restoredState = await stateStore.LoadAsync();
        Check(
            restoredState.CollapsedCollectionNames.Contains("cacowards 2019"),
            "Accordion-Zustände der Sammlungen werden persistent gespeichert");
        Check(
            restoredState.CollectionArtworkPaths.TryGetValue(
                "CACOWARDS 2019",
                out var artworkPath)
            && artworkPath.EndsWith(
                Path.Combine("Data", "CollectionArtworks", "cacowards.png"),
                StringComparison.OrdinalIgnoreCase),
            "Collection-Artworks werden portabel und persistent gespeichert");
        Check(
            restoredState.WindowWidth == 1280
            && restoredState.WindowHeight == 760,
            "Die Fenstergröße wird portabel und persistent gespeichert");
    }
    finally
    {
        Environment.SetEnvironmentVariable(
            JsonUserLibraryStateStore.StateEnvironmentVariable,
            null);
        if (File.Exists(statePath))
            File.Delete(statePath);
    }

    if (databasePath is null)
    {
        Console.WriteLine("SKIP Reale Datenbankintegration (keine Pfade angegeben)");
    }
    else
    {
        Check(File.Exists(databasePath), "Datenbank vorhanden");

        await using (var connection = await OpenReadOnlyAsync(databasePath))
        {
            Check(await ScalarStringAsync(connection, "PRAGMA integrity_check;") == "ok", "SQLite-Integrität");
            var gameCount = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM GameFiles;");
            Console.WriteLine($"INFO GameFiles={gameCount}");
            Check(gameCount > 0, "Bibliothek ist nicht leer");
            if (expectedCount.HasValue)
                Check(gameCount == expectedCount.Value, $"Bibliothek enthält {expectedCount.Value} Einträge");

            var storedMapCount = await ScalarIntAsync(
                connection,
                "SELECT COALESCE(SUM(COALESCE(MapCount, 0)), 0) FROM GameFiles;");
            var derivedMapCount = await ScalarIntAsync(
                connection,
                """
                SELECT COALESCE(SUM(
                    CASE
                        WHEN NULLIF(TRIM(Map), '') IS NULL THEN 0
                        ELSE LENGTH(Map) - LENGTH(REPLACE(Map, ',', '')) + 1
                    END), 0)
                FROM GameFiles;
                """);
            var entriesWithMaps = await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM GameFiles WHERE COALESCE(MapCount, 0) > 0;");
            var mapCountMismatches = await ScalarIntAsync(
                connection,
                """
                SELECT COUNT(*)
                FROM GameFiles
                WHERE COALESCE(MapCount, 0) !=
                    CASE
                        WHEN NULLIF(TRIM(Map), '') IS NULL THEN 0
                        ELSE LENGTH(Map) - LENGTH(REPLACE(Map, ',', '')) + 1
                    END;
                """);
            var mapCountUndercounts = await ScalarIntAsync(
                connection,
                """
                SELECT COUNT(*)
                FROM GameFiles
                WHERE COALESCE(MapCount, 0) <
                    CASE
                        WHEN NULLIF(TRIM(Map), '') IS NULL THEN 0
                        ELSE LENGTH(Map) - LENGTH(REPLACE(Map, ',', '')) + 1
                    END;
                """);
            Console.WriteLine(
                $"INFO Maps stored={storedMapCount}, derived={derivedMapCount}, " +
                $"entriesWithMaps={entriesWithMaps}, mismatches={mapCountMismatches}, " +
                $"undercounts={mapCountUndercounts}");
            await using (var audit = connection.CreateCommand())
            {
                audit.CommandText =
                    """
                    SELECT GameFileID,
                           COALESCE(NULLIF(Title, ''), FileName),
                           COALESCE(MapCount, 0),
                           CASE
                               WHEN NULLIF(TRIM(Map), '') IS NULL THEN 0
                               ELSE LENGTH(Map) - LENGTH(REPLACE(Map, ',', '')) + 1
                           END
                    FROM GameFiles
                    ORDER BY COALESCE(MapCount, 0) DESC
                    LIMIT 12;
                    """;
                await using var reader = await audit.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    Console.WriteLine(
                        $"INFO MapTop ID={reader.GetInt32(0)} " +
                        $"stored={reader.GetInt32(2)} derived={reader.GetInt32(3)} " +
                        $"title={reader.GetString(1)}");
                }
            }
            Check(
                storedMapCount >= derivedMapCount && mapCountUndercounts == 0,
                "MapCount deckt alle gespeicherten Map-Listen ab");

            var dataDirectory = Path.GetDirectoryName(databasePath)!;
            var configuredGameDirectory = await ScalarStringAsync(
                connection,
                "SELECT Value FROM Configuration WHERE Name='GameFileDirectory';");
            var gameDirectory = ResolvePath(dataDirectory, configuredGameDirectory ?? "Data");
            var missingGames = await CountMissingAsync(
                connection,
                """
                SELECT game.FileName,
                       game.GameFileID,
                       COALESCE(game.Title, ''),
                       CASE WHEN iwad.IWadID IS NULL THEN 0 ELSE 1 END
                FROM GameFiles game
                LEFT JOIN IWads iwad ON iwad.GameFileID = game.GameFileID;
                """,
                fileName => Path.Combine(gameDirectory, fileName));
            var missingTitlePics = await CountMissingAsync(
                connection,
                "SELECT FileName FROM Files WHERE FileTypeID=6;",
                fileName => Path.Combine(gameDirectory, "TitlePics", fileName));
            var thumbnailCount = await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM Files WHERE FileTypeID=4;");
            var titlePicCount = await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM Files WHERE FileTypeID=6;");
            Console.WriteLine(
                $"INFO Artwork titlePics={titlePicCount}, legacyThumbnails={thumbnailCount}");
            var missingPorts = await CountMissingPairsAsync(
                connection,
                "SELECT Directory, Executable FROM SourcePorts;",
                (directory, executable) =>
                    Path.Combine(ResolvePath(dataDirectory, directory), executable));
            Check(missingGames == 0, "Alle GameFiles sind referenziert");
            Check(missingTitlePics == 0, "Alle TITLEPIC-Originale sind referenziert");
            Check(missingPorts == 0, "Alle Source-Port-Executables sind referenziert");
        }

        await ExerciseNativeWritesAsync(databasePath);
    }
}
catch (Exception exception)
{
    failures.Add(exception.Message);
    Console.Error.WriteLine(exception);
}

Console.WriteLine(failures.Count == 0
    ? "RESULT PASS"
    : $"RESULT FAIL ({failures.Count})");
return failures.Count == 0 ? 0 : 1;

static async Task ExerciseNativeWritesAsync(string sourceDatabasePath)
{
    var scratchRoot = Path.Combine(
        Path.GetTempPath(),
        $"DoomLauncher-ModernTests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(scratchRoot);
    try
    {
        var scratchDatabase = Path.Combine(scratchRoot, "DoomLauncher.sqlite");
        File.Copy(sourceDatabasePath, scratchDatabase);
        var managedDirectory = Path.Combine(scratchRoot, "GameFiles");
        Directory.CreateDirectory(managedDirectory);
        var migrationGameFileId = 0;

        await using (var connection = new SqliteConnection(
                         new SqliteConnectionStringBuilder
                         {
                             DataSource = scratchDatabase,
                             Mode = SqliteOpenMode.ReadWrite,
                         }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE Configuration SET Value=$value WHERE Name='GameFileDirectory';";
            command.Parameters.AddWithValue("$value", managedDirectory);
            await command.ExecuteNonQueryAsync();
            command.CommandText = "SELECT MIN(GameFileID) FROM GameFiles;";
            command.Parameters.Clear();
            migrationGameFileId = Convert.ToInt32(await command.ExecuteScalarAsync());
            command.CommandText =
                """
                INSERT INTO Tags (Name, HasTab)
                SELECT 'Finished', 0
                WHERE NOT EXISTS (
                    SELECT 1 FROM Tags WHERE Name='Finished' COLLATE NOCASE
                );
                INSERT INTO TagMapping (FileID, TagID)
                SELECT $gameFileId, TagID
                FROM Tags
                WHERE Name='Finished' COLLATE NOCASE
                  AND NOT EXISTS (
                      SELECT 1 FROM TagMapping
                      WHERE FileID=$gameFileId AND TagID=Tags.TagID
                  );
                """;
            command.Parameters.AddWithValue("$gameFileId", migrationGameFileId);
            await command.ExecuteNonQueryAsync();
        }

        Environment.SetEnvironmentVariable(
            DoomLauncherDatabaseLocator.DatabaseEnvironmentVariable,
            scratchDatabase);
        var service = new SqliteNativeLibraryService(new DoomLauncherDatabaseLocator());
        var importSource = Path.Combine(scratchRoot, "native-import.wad");
        await File.WriteAllBytesAsync(
            importSource,
            [
                0x50, 0x57, 0x41, 0x44,
                0x01, 0x00, 0x00, 0x00,
                0x0C, 0x00, 0x00, 0x00,
                0x0C, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x4D, 0x41, 0x50, 0x30, 0x31, 0x00, 0x00, 0x00,
            ]);
        var import = await service.ImportAsync(importSource);
        if (!File.Exists(import.DestinationPath))
            throw new InvalidOperationException("Der native Import hat keine verwaltete Datei erzeugt.");
        var unknownIwad = await service.DetectIwadVersionAsync(
            importSource,
            Path.GetFileName(importSource));
        if (unknownIwad.IsKnown
            || string.IsNullOrWhiteSpace(unknownIwad.Md5)
            || unknownIwad.FileSize != new FileInfo(importSource).Length)
        {
            throw new InvalidOperationException(
                "Die IWAD-Hash-Erkennung hat einen unbekannten Test-WAD nicht transparent ausgewertet.");
        }
        await using (var connection = await OpenReadOnlyAsync(scratchDatabase))
        {
            var legacyFinishedTags = await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM Tags WHERE Name='Finished' COLLATE NOCASE;");
            var migratedFinished = await ScalarIntAsync(
                connection,
                $"SELECT Finished FROM WinUI_GameState WHERE GameFileID={migrationGameFileId};");
            var importedMapCount = await ScalarIntAsync(
                connection,
                $"SELECT MapCount FROM GameFiles WHERE GameFileID={import.GameFileId};");
            if (legacyFinishedTags != 0 || migratedFinished != 1)
            {
                throw new InvalidOperationException(
                    "Die alte Finished-Sammlung wurde nicht in den nativen Abschlussstatus migriert.");
            }
            if (importedMapCount != 1)
                throw new InvalidOperationException("MAP01 wurde beim WAD-Import nicht erkannt.");
        }

        var game = await service.LoadGameAsync(import.GameFileId);
        await service.UpdateGameAsync(
            game with
            {
                Title = "\tNative\t Integration \r\n Test",
                Author = "\tCodex\t",
                Description = "Transactional\twrite\r\ntest",
            });
        var updated = await service.LoadGameAsync(import.GameFileId);
        if (updated.Title != "Native Integration Test"
            || updated.Author != "Codex"
            || updated.Description != $"Transactional write{Environment.NewLine}test")
            throw new InvalidOperationException("Die native Bearbeitung wurde nicht gespeichert.");

        var settings = await service.LoadSettingsAsync();
        await service.UpdateSettingsAsync(
            settings with
            {
                ItemsPerPage = 90,
                HomeItemsPerGroup = 17,
                ShowPlayDialog = !settings.ShowPlayDialog,
            });
        var updatedSettings = await service.LoadSettingsAsync();
        if (updatedSettings.ItemsPerPage != 90
            || updatedSettings.HomeItemsPerGroup != 17
            || updatedSettings.ShowPlayDialog == settings.ShowPlayDialog)
        {
            throw new InvalidOperationException("Die nativen Einstellungen wurden nicht gespeichert.");
        }

        await service.SetGameFinishedAsync(import.GameFileId, true);
        await using (var connection = await OpenReadOnlyAsync(scratchDatabase))
        {
            var finished = await ScalarIntAsync(
                connection,
                $"SELECT Finished FROM WinUI_GameState WHERE GameFileID={import.GameFileId};");
            if (finished != 1)
                throw new InvalidOperationException("Der Finished-Status wurde nicht gespeichert.");
        }

        await service.SaveGameCollectionsAsync(
            import.GameFileId,
            new HashSet<int>(),
            "Modern Integration Test");
        var collections = await service.LoadGameCollectionsAsync(import.GameFileId);
        if (!collections.Collections.Any(tag => tag.Name == "Modern Integration Test")
            || !collections.Collections
                .Where(tag => tag.Name == "Modern Integration Test")
                .All(tag => collections.SelectedTagIds.Contains(tag.TagId)))
        {
            throw new InvalidOperationException("Die Collection-Zuordnung wurde nicht gespeichert.");
        }
        var disposableTag = collections.Collections.First(
            tag => tag.Name == "Modern Integration Test");
        await service.DeleteCollectionAsync(disposableTag.TagId);
        await using (var connection = await OpenReadOnlyAsync(scratchDatabase))
        {
            var collectionRows = await ScalarIntAsync(
                connection,
                $"SELECT COUNT(*) FROM Tags WHERE TagID={disposableTag.TagId};");
            var gameRows = await ScalarIntAsync(
                connection,
                $"SELECT COUNT(*) FROM GameFiles WHERE GameFileID={import.GameFileId};");
            if (collectionRows != 0 || gameRows != 1)
            {
                throw new InvalidOperationException(
                    "Das Löschen einer Sammlung muss ihre Zuordnung entfernen und den Mod behalten.");
            }
        }

        await service.UpdateGameFromIdGamesAsync(
            import.GameFileId,
            new DoomLauncher.WinUI.Models.IdGamesItem
            {
                Id = 424242,
                Title = "Improved\t\tTitle",
                Author = "Better\tAuthor",
                Description = "Updated\t\tmetadata\r\nwithout local state loss",
                FileName = "native-import.wad",
                Directory = "levels/doom2/Ports/",
                ReleaseDate = new DateTime(2025, 12, 24),
                Rating = 4.25,
            });
        await using (var connection = await OpenReadOnlyAsync(scratchDatabase))
        {
            var title = await ScalarStringAsync(
                connection,
                $"SELECT Title FROM GameFiles WHERE GameFileID={import.GameFileId};");
            var author = await ScalarStringAsync(
                connection,
                $"SELECT Author FROM GameFiles WHERE GameFileID={import.GameFileId};");
            var description = await ScalarStringAsync(
                connection,
                $"SELECT Description FROM GameFiles WHERE GameFileID={import.GameFileId};");
            var metadataCount = await ScalarIntAsync(
                connection,
                $"SELECT COUNT(*) FROM WinUI_IdGamesMetadata " +
                $"WHERE GameFileID={import.GameFileId} AND IdGamesID=424242;");
            var finished = await ScalarIntAsync(
                connection,
                $"SELECT Finished FROM WinUI_GameState WHERE GameFileID={import.GameFileId};");
            if (title != "Improved Title"
                || author != "Better Author"
                || description != $"Updated metadata{Environment.NewLine}without local state loss"
                || metadataCount != 1
                || finished != 1)
            {
                throw new InvalidOperationException(
                    "Die /idgames-Metadatenverbesserung oder der lokale Zustand wurde nicht korrekt gespeichert.");
            }
        }

        var manualScreenshot1 = Path.Combine(scratchRoot, "manual-shot-1.png");
        var manualScreenshot2 = Path.Combine(scratchRoot, "manual-shot-2.png");
        using (var bitmap = new System.Drawing.Bitmap(640, 480))
        {
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.Clear(System.Drawing.Color.DarkBlue);
            bitmap.Save(
                manualScreenshot1,
                System.Drawing.Imaging.ImageFormat.Png);
            graphics.Clear(System.Drawing.Color.DarkGreen);
            bitmap.Save(
                manualScreenshot2,
                System.Drawing.Imaging.ImageFormat.Png);
        }
        await service.AddScreenshotsAsync(
            import.GameFileId,
            [manualScreenshot1, manualScreenshot2]);
        var media = await service.LoadGameMediaAsync(import.GameFileId);
        if (media.Screenshots.Count != 2)
            throw new InvalidOperationException("Manuelle Screenshots wurden nicht gespeichert.");
        await service.SetScreenshotOrderAsync(
            import.GameFileId,
            media.Screenshots.Reverse().Select(item => item.FileId).ToArray());
        media = await service.LoadGameMediaAsync(import.GameFileId);
        await service.SetScreenshotAsTitleArtworkAsync(
            import.GameFileId,
            media.Screenshots[0].FileId);
        media = await service.LoadGameMediaAsync(import.GameFileId);
        if (media.TitleArtwork is null
            || media.Screenshots.Count != 1)
        {
            throw new InvalidOperationException(
                "Medienreihenfolge oder Titelbild-Zuweisung wurde nicht gespeichert.");
        }

        var artworkArchive = Path.Combine(scratchRoot, "titlepic-integration.zip");
        using (var archiveStream = File.Create(artworkArchive))
        using (var archive = new System.IO.Compression.ZipArchive(
                   archiveStream,
                   System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("graphics/TITLEPIC.png");
            await using var entryStream = entry.Open();
            using var artwork = new System.Drawing.Bitmap(320, 200);
            using (var graphics = System.Drawing.Graphics.FromImage(artwork))
                graphics.Clear(System.Drawing.Color.DarkRed);
            artwork.Save(entryStream, System.Drawing.Imaging.ImageFormat.Png);
        }
        if (!await service.TryImportTitlePicAsync(
                import.GameFileId,
                artworkArchive))
        {
            throw new InvalidOperationException("TITLEPIC wurde nicht erkannt.");
        }
        var lzmaArtworkArchive = Path.Combine(
            scratchRoot,
            "titlepic-lzma-integration.zip");
        await File.WriteAllBytesAsync(
            lzmaArtworkArchive,
            Convert.FromBase64String(
                "UEsDBD8AAgAOAKAB/lw4aLZhkgAAAE0CAAAVAAAAZ3JhcGhpY3MvVElUTEVQSUMucG5nCQQFAF0AAIAAAESUBcR6J/b37omOUJCIs6rMGy5/yfdXCfHY3/i4uT2u1VxN+Id7l0jk9bbtVdqyKGznuwZeu5j91H3RNpMQm6L4AoQXv1iVJCZnToXXk1Ubq03y7rH2/KC50ZRq5MwKi/3oAs42gPoJX3XkMD2HSoNULpd5mnfqsMzpDawsB68bEa///zoWmgBQSwECPwA/AAIADgCgAf5cOGi2YZIAAABNAgAAFQAAAAAAAAAAAAAAgAEAAAAAZ3JhcGhpY3MvVElUTEVQSUMucG5nUEsFBgAAAAABAAEAQwAAAMUAAAAAAA=="));
        if (!await service.TryImportTitlePicAsync(
                import.GameFileId,
                lzmaArtworkArchive))
        {
            throw new InvalidOperationException(
                "LZMA-komprimierter TITLEPIC wurde nicht erkannt.");
        }
        var classicArtwork = Path.Combine(scratchRoot, "classic-titlepic.wad");
        CreateFlatTitlePicWad(classicArtwork);
        if (!await service.TryImportTitlePicAsync(
                import.GameFileId,
                classicArtwork))
        {
            throw new InvalidOperationException(
                "Klassischer TITLEPIC-Lump wurde nicht erkannt.");
        }
        await using (var connection = await OpenReadOnlyAsync(scratchDatabase))
        {
            var titlePicName = await ScalarStringAsync(
                connection,
                $"SELECT FileName FROM Files WHERE GameFileID={import.GameFileId} AND FileTypeID=6;");
            var thumbnailCount = await ScalarIntAsync(
                connection,
                $"SELECT COUNT(*) FROM Files WHERE GameFileID={import.GameFileId} AND FileTypeID=4;");
            if (string.IsNullOrWhiteSpace(titlePicName)
                || !File.Exists(Path.Combine(managedDirectory, "TitlePics", titlePicName))
                || thumbnailCount != 0)
            {
                throw new InvalidOperationException(
                    "Das TITLEPIC-Original wurde nicht ohne Thumbnail gespeichert.");
            }
        }

        const string portableCollection = "Portable Integration Test";
        await service.SaveGameCollectionsAsync(
            import.GameFileId,
            new HashSet<int>(),
            portableCollection);
        var personalBundle = Path.Combine(scratchRoot, "personal.dl667pack");
        await service.ExportPortableBundleAsync(
            [import.GameFileId],
            personalBundle,
            new PortableBundleExportOptions(
                IncludeGeneralMetadata: true,
                IncludePersonalMetadata: true,
                IncludeScreenshots: true,
                IncludeTitleArtwork: true,
                IncludeCollections: true,
                FavoriteGameFileIds: new HashSet<int> { import.GameFileId },
                CollectionArtworkPaths: new Dictionary<string, string>
                {
                    [portableCollection] = manualScreenshot1,
                },
                LibraryFilterCollections: new HashSet<string>
                {
                    portableCollection,
                }));
        using (var package = System.IO.Compression.ZipFile.OpenRead(personalBundle))
        {
            var manifestEntry = package.GetEntry("manifest.json")
                ?? throw new InvalidOperationException(
                    "Das portable Paket enthält kein Manifest.");
            using var manifestStream = manifestEntry.Open();
            using var manifest = await System.Text.Json.JsonDocument.ParseAsync(
                manifestStream);
            var root = manifest.RootElement;
            var packageEntry = root.GetProperty("Entries")[0];
            var collection = root.GetProperty("Collections")[0];
            if (root.GetProperty("FormatVersion").GetInt32() != 3
                || !root.GetProperty("ContainsPersonalMetadata").GetBoolean()
                || !packageEntry.GetProperty("IsFavorite").GetBoolean()
                || !collection.GetProperty("ShowAsLibraryFilter").GetBoolean()
                || string.IsNullOrWhiteSpace(
                    collection.GetProperty("Artwork").GetString()))
            {
                throw new InvalidOperationException(
                    "Das persönliche Paket enthält nicht alle Metadaten.");
            }
        }
        var bundleInspection = await service.InspectPortableBundleAsync(
            personalBundle);
        if (bundleInspection.FormatVersion != 3
            || !bundleInspection.ContainsGeneralMetadata
            || !bundleInspection.ContainsPersonalMetadata
            || !bundleInspection.ContainsScreenshots
            || !bundleInspection.ContainsTitleArtwork
            || !bundleInspection.ContainsCollections
            || bundleInspection.Entries.Single().Conflict is null)
        {
            throw new InvalidOperationException(
                "Die Paketinspektion erkennt Inhalte oder Dateikonflikte nicht korrekt.");
        }

        var cleanBundle = Path.Combine(scratchRoot, "clean.dl667pack");
        await service.ExportPortableBundleAsync(
            [import.GameFileId],
            cleanBundle,
            new PortableBundleExportOptions(
                IncludeGeneralMetadata: true,
                IncludePersonalMetadata: false,
                IncludeScreenshots: true,
                IncludeTitleArtwork: true,
                IncludeCollections: true,
                FavoriteGameFileIds: new HashSet<int> { import.GameFileId },
                CollectionArtworkPaths: new Dictionary<string, string>(),
                LibraryFilterCollections: new HashSet<string>()));
        using (var package = System.IO.Compression.ZipFile.OpenRead(cleanBundle))
        {
            var manifestEntry = package.GetEntry("manifest.json")
                ?? throw new InvalidOperationException(
                    "Das saubere Paket enthält kein Manifest.");
            using var manifestStream = manifestEntry.Open();
            using var manifest = await System.Text.Json.JsonDocument.ParseAsync(
                manifestStream);
            var root = manifest.RootElement;
            var packageEntry = root.GetProperty("Entries")[0];
            if (root.GetProperty("ContainsPersonalMetadata").GetBoolean()
                || packageEntry.GetProperty("MinutesPlayed").GetInt32() != 0
                || packageEntry.GetProperty("IsFinished").GetBoolean()
                || packageEntry.GetProperty("IsFavorite").GetBoolean())
            {
                throw new InvalidOperationException(
                    "Das saubere Paket enthält persönliche Metadaten.");
            }
        }
        var granularImport = await service.ImportPortableBundleAsync(
            cleanBundle,
            new PortableBundleImportOptions(
                IncludeGeneralMetadata: true,
                IncludePersonalMetadata: false,
                IncludeScreenshots: false,
                IncludeTitleArtwork: false,
                IncludeCollections: false,
                ConflictResolutions: new Dictionary<string, ImportFileConflictResolution>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [Path.GetFileName(importSource)] =
                        ImportFileConflictResolution.Overwrite,
                }));
        if (granularImport.ImportedEntries != 1
            || granularImport.ImportedMediaFiles != 0
            || granularImport.Collections.Count != 0
            || granularImport.FavoriteGameFileIds.Count != 0)
        {
            throw new InvalidOperationException(
                "Die granularen Importoptionen wurden nicht eingehalten.");
        }
        await using (var connection = await OpenReadOnlyAsync(scratchDatabase))
        {
            var originalNameCount = await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM GameFiles WHERE FileName=" +
                $"'{Path.Combine("Mods", Path.GetFileName(importSource)).Replace("'", "''")}' " +
                "COLLATE NOCASE;");
            if (originalNameCount != 1)
            {
                throw new InvalidOperationException(
                    "Der Paketimport hat den Originaldateinamen nicht konfliktfrei wiederverwendet.");
            }
        }

        var idGamesSource = Path.Combine(scratchRoot, "idgames-integration.zip");
        await File.WriteAllBytesAsync(
            idGamesSource,
            [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        var idGamesImport = await service.ImportIdGamesAsync(
            new DoomLauncher.WinUI.Models.IdGamesItem
            {
                Id = 999999,
                Title = "IdGames\t\tIntegration\tTest",
                Author = "Codex\t\tTeam",
                Description = "Metadata\t\tand source mapping\r\ntest",
                FileName = "idgames-integration.zip",
                Directory = "levels/doom2/Ports/",
                ReleaseDate = new DateTime(2026, 1, 2),
                Rating = 4.5,
                SizeBytes = 22,
            },
            idGamesSource);
        await using (var connection = await OpenReadOnlyAsync(scratchDatabase))
        {
            var title = await ScalarStringAsync(
                connection,
                $"SELECT Title FROM GameFiles WHERE GameFileID={idGamesImport.GameFileId};");
            var author = await ScalarStringAsync(
                connection,
                $"SELECT Author FROM GameFiles WHERE GameFileID={idGamesImport.GameFileId};");
            var description = await ScalarStringAsync(
                connection,
                $"SELECT Description FROM GameFiles WHERE GameFileID={idGamesImport.GameFileId};");
            var mappingCount = await ScalarIntAsync(
                connection,
                $"SELECT COUNT(*) FROM WinUI_IdGamesDownloads " +
                $"WHERE GameFileID={idGamesImport.GameFileId} AND IdGamesID=999999;");
            if (title != "IdGames Integration Test"
                || author != "Codex Team"
                || description != $"Metadata and source mapping{Environment.NewLine}test"
                || mappingCount != 1)
                throw new InvalidOperationException("Der /idgames-Import wurde nicht korrekt gespeichert.");
        }

        var systemDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.System);
        await service.SaveSourcePortAsync(
            new NativeSourcePortDefinition(
                null,
                "Native Integration Port",
                systemDirectory,
                "cmd.exe",
                ".wad,.pk3,.deh,.bex",
                "-file",
                string.Empty,
                "1.2.3-test",
                "Auto",
                string.Empty,
                ".png,.jpg",
                string.Empty,
                "ZDoomSave",
                string.Empty,
                ".zds"));
        await service.SaveIwadAsync(
            new NativeIwadDefinition(
                null,
                "Native Integration IWAD",
                import.FileName,
                import.FileName,
                "manual-test",
                unknownIwad.Md5,
                unknownIwad.FileSize));
        var hasHexenDefinition = false;
        await using (var connection = await OpenReadOnlyAsync(scratchDatabase))
        {
            hasHexenDefinition = await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM IWads WHERE FileName='HEXEN.WAD' COLLATE NOCASE;") > 0;
        }
        var hexddRejected = false;
        try
        {
            await service.SaveIwadAsync(
                new NativeIwadDefinition(
                    null,
                    "Deathkings integration",
                    import.FileName,
                    "HEXDD.WAD"));
        }
        catch (InvalidOperationException exception)
            when (exception.Message.Contains("HEXEN.WAD", StringComparison.Ordinal))
        {
            hexddRejected = true;
        }
        if (hexddRejected == hasHexenDefinition)
        {
            throw new InvalidOperationException(
                "Die HEXDD.WAD-Abhängigkeit von HEXEN.WAD wurde nicht korrekt validiert.");
        }
        var definitions = await service.LoadLauncherDefinitionsAsync();
        var testPort = definitions.SourcePorts.Single(
            item => item.Name == "Native Integration Port");
        var testIwad = definitions.Iwads.Single(
            item => item.Name == "Native Integration IWAD");
        if (testPort.Version != "1.2.3-test"
            || testPort.ScreenshotSupport != "Auto"
            || testPort.ScreenshotExtensions != ".png,.jpg"
            || testPort.StatisticsAdapter != "ZDoomSave"
            || testPort.SaveGameExtensions != ".zds"
            || testIwad.Version != "manual-test"
            || testIwad.Md5 != unknownIwad.Md5)
        {
            throw new InvalidOperationException(
                "Die Sourceport-Fähigkeiten wurden nicht vollständig gespeichert.");
        }
        var versionedSettings = await service.LoadSettingsAsync();
        if (!versionedSettings.SourcePorts.Any(item =>
                item.Id == testPort.SourcePortId
                && item.Name.Contains("1.2.3-test", StringComparison.Ordinal))
            || !versionedSettings.Iwads.Any(item =>
                item.Id == testIwad.IwadId
                && item.Name.Contains("manual-test", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Versionen fehlen in den Sourceport- oder IWAD-Auswahllisten.");
        }
        definitions = await service.LoadLauncherDefinitionsAsync();
        if (!definitions.SourcePorts.Any(item =>
                item.SourcePortId == testPort.SourcePortId)
            || !definitions.Iwads.Any(item =>
                item.IwadId == testIwad.IwadId))
        {
            throw new InvalidOperationException(
                "Die nativen Launcher-Definitionen wurden nicht gespeichert.");
        }

        var captureDirectory = Path.Combine(scratchRoot, "captures");
        var screenshotDirectory = Path.Combine(scratchRoot, "screenshots");
        Directory.CreateDirectory(captureDirectory);
        await using (var connection = new SqliteConnection(
                         new SqliteConnectionStringBuilder
                         {
                             DataSource = scratchDatabase,
                             Mode = SqliteOpenMode.ReadWrite,
                         }.ToString()))
        {
            await connection.OpenAsync();
            await UpsertConfigurationAsync(
                connection,
                "ScreenshotCaptureDirectories",
                captureDirectory);
            await UpsertConfigurationAsync(
                connection,
                "ScreenshotDirectory",
                screenshotDirectory);
            await UpsertConfigurationAsync(connection, "ImportScreenshots", "true");
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE GameFiles
                SET SourcePortID=$sourcePortId, IWadID=$iwadId,
                    SettingsExtraParamsOnly=1,
                    SettingsExtraParams='/c ping 127.0.0.1 -n 3 >nul'
                WHERE GameFileID=$gameFileId;
                """;
            command.Parameters.AddWithValue("$sourcePortId", testPort.SourcePortId!.Value);
            command.Parameters.AddWithValue("$iwadId", testIwad.IwadId!.Value);
            command.Parameters.AddWithValue("$gameFileId", import.GameFileId);
            await command.ExecuteNonQueryAsync();
        }

        var launcher = new NativeGameLaunchService(
            new DoomLauncherDatabaseLocator(),
            new SystemProcessStarter());
        var launchResult = await launcher.LaunchAsync(
            new GameLaunchRequest(import.GameFileId, "Native Integration"));
        await Task.Delay(250);
        var nestedCaptureDirectory = Path.Combine(captureDirectory, "Screenshots");
        Directory.CreateDirectory(nestedCaptureDirectory);
        var capturedScreenshot = Path.Combine(nestedCaptureDirectory, "native-shot.png");
        await File.WriteAllBytesAsync(capturedScreenshot, [0x89, 0x50, 0x4E, 0x47]);
        await launchResult.Session.WaitForExitAsync();
        await using (var connection = await OpenReadOnlyAsync(scratchDatabase))
        {
            var lastPlayed = await ScalarStringAsync(
                connection,
                $"SELECT LastPlayed FROM GameFiles WHERE GameFileID={import.GameFileId};");
            var screenshotCount = await ScalarIntAsync(
                connection,
                $"SELECT COUNT(*) FROM Files " +
                $"WHERE GameFileID={import.GameFileId} AND FileTypeID=1;");
            if (string.IsNullOrWhiteSpace(lastPlayed)
                || screenshotCount == 0
                || !Directory.EnumerateFiles(screenshotDirectory).Any())
            {
                throw new InvalidOperationException(
                    "Nativer Spielstart, Spielzeit oder Screenshot-Import war nicht erfolgreich.");
            }
        }

        await ExerciseMigrationAsync(scratchRoot, scratchDatabase);
        Console.WriteLine(
            "PASS Import, Tab-Bereinigung, /idgames-IDs, LZMA-TITLEPIC, Paketmodi, Definitionen, nativer Start, Spielzeit, Screenshots und Migration (isolierte DB-Kopie)");
    }
    finally
    {
        Environment.SetEnvironmentVariable(
            DoomLauncherDatabaseLocator.DatabaseEnvironmentVariable,
            null);
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(scratchRoot))
            Directory.Delete(scratchRoot, recursive: true);
    }
}

static async Task ExerciseMigrationAsync(
    string scratchRoot,
    string sourceDatabase)
{
    var sourceRoot = Path.Combine(scratchRoot, "legacy-source");
    var sourceFiles = Path.Combine(sourceRoot, "GameFiles");
    Directory.CreateDirectory(sourceFiles);
    var sourceIwads = Path.Combine(sourceRoot, "IWADs");
    var sourcePorts = Path.Combine(sourceRoot, "Sourceports", "LegacyPort");
    var sourceTiles = Path.Combine(sourceRoot, "TileImages");
    var sourceScreenshots = Path.Combine(sourceFiles, "Screenshots");
    var sourceTitlePics = Path.Combine(sourceFiles, "TitlePics");
    Directory.CreateDirectory(sourceIwads);
    Directory.CreateDirectory(sourcePorts);
    Directory.CreateDirectory(sourceTiles);
    Directory.CreateDirectory(sourceScreenshots);
    Directory.CreateDirectory(sourceTitlePics);
    var sourceCopy = Path.Combine(sourceRoot, "DoomLauncher.sqlite");
    SqliteConnection.ClearAllPools();
    File.Copy(sourceDatabase, sourceCopy);
    await File.WriteAllTextAsync(
        Path.Combine(sourceFiles, "migration-marker.wad"),
        "migration");
    await File.WriteAllTextAsync(Path.Combine(sourceIwads, "legacy-iwad.zip"), "iwad");
    await File.WriteAllTextAsync(Path.Combine(sourcePorts, "legacy-port.exe"), "port");
    await File.WriteAllTextAsync(Path.Combine(sourceTiles, "doom.png"), "tile");
    await File.WriteAllTextAsync(Path.Combine(sourceScreenshots, "legacy-shot.png"), "shot");
    await File.WriteAllTextAsync(Path.Combine(sourceTitlePics, "legacy-title.png"), "title");
    await using (var connection = new SqliteConnection(
                     new SqliteConnectionStringBuilder
                     {
                         DataSource = sourceCopy,
                         Mode = SqliteOpenMode.ReadWrite,
                         Pooling = false,
                     }.ToString()))
    {
        await connection.OpenAsync();
        await UpsertConfigurationAsync(connection, "GameFileDirectory", "GameFiles");
        await UpsertConfigurationAsync(connection, "GameWadDirectory", "IWADs");
        await UpsertConfigurationAsync(
            connection,
            "ScreenshotDirectory",
            @"GameFiles\Screenshots");
        await using var legacySchema = connection.CreateCommand();
        legacySchema.CommandText =
            "ALTER TABLE Files DROP COLUMN DerivedFromFileID;";
        await legacySchema.ExecuteNonQueryAsync();
    }

    var migrationRoot = Path.Combine(scratchRoot, "migration-target");
    var migrationDatabase = Path.Combine(migrationRoot, "DoomLauncher.sqlite");
    Environment.SetEnvironmentVariable(
        DoomLauncherDatabaseLocator.DatabaseEnvironmentVariable,
        migrationDatabase);
    var migration = new LegacyInstallationMigrationService(
        new DoomLauncherDatabaseLocator());
    var migrationProgress = new RecordedProgress();
    var result = await migration.MigrateAsync(
        sourceRoot,
        migrationProgress);
    if (!File.Exists(result.DatabasePath)
        || !File.Exists(Path.Combine(
            migrationRoot,
            "Data",
            "Mods",
            "migration-marker.wad"))
        || !File.Exists(Path.Combine(
            migrationRoot,
            "Data",
            "GameWads",
            "legacy-iwad.zip"))
        || !File.Exists(Path.Combine(
            migrationRoot,
            "Data",
            "Sourceports",
            "LegacyPort",
            "legacy-port.exe"))
        || !File.Exists(Path.Combine(
            migrationRoot,
            "Data",
            "TileImages",
            "doom.png"))
        || !File.Exists(Path.Combine(
            migrationRoot,
            "Data",
            "Screenshots",
            "legacy-shot.png"))
        || !File.Exists(Path.Combine(
            migrationRoot,
            "Data",
            "TitlePics",
            "legacy-title.png")))
    {
        throw new InvalidOperationException(
            "Die Migration hat Datenbank oder referenzierte Dateien nicht übernommen.");
    }
    await using var migrated = await OpenReadOnlyAsync(result.DatabasePath);
    if (await ScalarStringAsync(migrated, "PRAGMA integrity_check;") != "ok")
        throw new InvalidOperationException("Die migrierte Datenbank ist nicht integer.");
    if (await ScalarIntAsync(
            migrated,
            "SELECT COUNT(*) FROM pragma_table_info('Files') " +
            "WHERE name='DerivedFromFileID';") != 1)
    {
        throw new InvalidOperationException(
            "Die Migration hat die fehlende DerivedFromFileID-Spalte nicht repariert.");
    }
    if (migrationProgress.Values.Count == 0
        || migrationProgress.Values[^1] != 100
        || migrationProgress.Values.Zip(
                migrationProgress.Values.Skip(1),
                (left, right) => right >= left)
            .Any(monotonic => !monotonic))
    {
        throw new InvalidOperationException(
            "Der Migrationsfortschritt ist unvollständig oder nicht monoton.");
    }
}

static async Task UpsertConfigurationAsync(
    SqliteConnection connection,
    string name,
    string value)
{
    await using var command = connection.CreateCommand();
    command.CommandText =
        """
        UPDATE Configuration SET Value=$value WHERE Name=$name;
        """;
    command.Parameters.AddWithValue("$name", name);
    command.Parameters.AddWithValue("$value", value);
    if (await command.ExecuteNonQueryAsync() != 0)
        return;
    command.CommandText =
        "INSERT INTO Configuration (Name, Value) VALUES ($name, $value);";
    await command.ExecuteNonQueryAsync();
}

static async Task<SqliteConnection> OpenReadOnlyAsync(string path)
{
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadOnly,
    }.ToString());
    await connection.OpenAsync();
    return connection;
}

static async Task<int> ScalarIntAsync(SqliteConnection connection, string sql)
{
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    return Convert.ToInt32(await command.ExecuteScalarAsync());
}

static async Task<string?> ScalarStringAsync(SqliteConnection connection, string sql)
{
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    return Convert.ToString(await command.ExecuteScalarAsync());
}

static async Task<int> CountMissingAsync(
    SqliteConnection connection,
    string sql,
    Func<string, string> resolve)
{
    var missing = 0;
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var value = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
        if (!string.IsNullOrWhiteSpace(value) && !File.Exists(resolve(value)))
        {
            var context = reader.FieldCount <= 1
                ? string.Empty
                : " [" + string.Join(
                    ", ",
                    Enumerable.Range(1, reader.FieldCount - 1)
                        .Select(index => reader.IsDBNull(index)
                            ? "NULL"
                            : Convert.ToString(reader.GetValue(index)) ?? string.Empty))
                    + "]";
            Console.WriteLine(
                $"INFO Missing reference: {value}{context} -> {resolve(value)}");
            missing++;
        }
    }
    return missing;
}

static async Task<int> CountMissingPairsAsync(
    SqliteConnection connection,
    string sql,
    Func<string, string, string> resolve)
{
    var missing = 0;
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var directory = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
        var executable = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        var resolved = resolve(directory, executable);
        if (!string.IsNullOrWhiteSpace(executable) && !File.Exists(resolved))
        {
            Console.WriteLine(
                $"INFO Missing source port: {directory} + {executable} -> {resolved}");
            missing++;
        }
    }
    return missing;
}

static string ResolvePath(string baseDirectory, string value)
{
    value = Environment.ExpandEnvironmentVariables(value);
    return Path.GetFullPath(
        Path.IsPathFullyQualified(value)
            ? value
            : Path.Combine(baseDirectory, value));
}

static void CreateFlatTitlePicWad(string path)
{
    var palette = new byte[256 * 3];
    for (var index = 0; index < 256; index++)
    {
        palette[index * 3] = (byte)index;
        palette[(index * 3) + 1] = (byte)(255 - index);
        palette[(index * 3) + 2] = (byte)(index / 2);
    }
    var titlePic = Enumerable.Repeat((byte)160, 320 * 200).ToArray();
    var directoryOffset = 12 + palette.Length + titlePic.Length;
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII);
    writer.Write(System.Text.Encoding.ASCII.GetBytes("PWAD"));
    writer.Write(2);
    writer.Write(directoryOffset);
    writer.Write(palette);
    writer.Write(titlePic);
    WriteDirectoryEntry(writer, 12, palette.Length, "PLAYPAL");
    WriteDirectoryEntry(writer, 12 + palette.Length, titlePic.Length, "TITLEPIC");

    static void WriteDirectoryEntry(
        BinaryWriter writer,
        int offset,
        int length,
        string name)
    {
        writer.Write(offset);
        writer.Write(length);
        var bytes = new byte[8];
        System.Text.Encoding.ASCII.GetBytes(name, bytes);
        writer.Write(bytes);
    }
}

static async Task VerifyFirstSetupAsync()
{
    var root = Path.Combine(
        Path.GetTempPath(),
        $"DoomLauncher-FirstSetup-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var database = Path.Combine(root, "DoomLauncher.sqlite");
    Environment.SetEnvironmentVariable(
        DoomLauncherDatabaseLocator.DatabaseEnvironmentVariable,
        database);
    try
    {
        var locator = new DoomLauncherDatabaseLocator();
        var library = new SqliteNativeLibraryService(locator);
        var setup = new FirstSetupService(locator, library);
        await setup.EnsureDatabaseAsync();
        var legacyGameFiles = Path.Combine(root, "Data", "GameFiles");
        var legacyMods = Path.Combine(legacyGameFiles, "Mods");
        Directory.CreateDirectory(legacyMods);
        await File.WriteAllTextAsync(
            Path.Combine(legacyMods, "layout-migration-marker.pk3"),
            "layout migration");
        await using (var legacyLayoutConnection = new SqliteConnection(
                         new SqliteConnectionStringBuilder
                         {
                             DataSource = database,
                             Mode = SqliteOpenMode.ReadWrite,
                         }.ToString()))
        {
            await legacyLayoutConnection.OpenAsync();
            await UpsertConfigurationAsync(
                legacyLayoutConnection,
                "GameFileDirectory",
                "Data\\GameFiles\\");
        }
        await setup.EnsureManagedLayoutAsync();
        var gameFiles = Path.Combine(root, "Data");
        if (!File.Exists(Path.Combine(
                gameFiles,
                "Mods",
                "layout-migration-marker.pk3"))
            || Directory.Exists(legacyGameFiles))
        {
            throw new InvalidOperationException(
                "Das alte Data\\GameFiles-Layout wurde nicht vollständig nach Data migriert.");
        }
        await using (var migratedConnection = await OpenReadOnlyAsync(database))
        {
            var configuredRoot = await ScalarStringAsync(
                migratedConnection,
                "SELECT Value FROM Configuration WHERE Name='GameFileDirectory';");
            if (!string.Equals(
                    configuredRoot,
                    "Data\\",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "GameFileDirectory verweist nach der Layoutmigration nicht auf Data.");
            }
        }

        var iwadDirectory = Path.Combine(gameFiles, "GameWads");
        Directory.CreateDirectory(iwadDirectory);
        var iwadPath = Path.Combine(iwadDirectory, "TESTIWAD.WAD");
        using (var stream = File.Create(iwadPath))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("IWAD"));
            writer.Write(0);
            writer.Write(12);
        }

        var sourcePortDirectory = Path.Combine(
            gameFiles,
            "Sourceports",
            "TestPort");
        Directory.CreateDirectory(sourcePortDirectory);
        File.Copy(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe"),
            Path.Combine(sourcePortDirectory, "TestPort.exe"));

        var modsDirectory = Path.Combine(gameFiles, "Mods");
        Directory.CreateDirectory(modsDirectory);
        var modPath = Path.Combine(modsDirectory, "sample.pk3");
        using (var stream = File.Create(modPath))
        using (var archive = new System.IO.Compression.ZipArchive(
                   stream,
                   System.IO.Compression.ZipArchiveMode.Create))
        {
            _ = archive.CreateEntry("README.txt");
        }

        var iwadProgress = new RecordedProgress();
        var sourcePortProgress = new RecordedProgress();
        var modProgress = new RecordedProgress();
        var iwads = await setup.ScanIwadsAsync(default, iwadProgress);
        var sourcePorts = await setup.ScanSourcePortsAsync(
            default,
            sourcePortProgress);
        var mods = await setup.ScanModsAsync(default, modProgress);
        if (iwads.Imported != 1
            || sourcePorts.Imported != 1
            || mods.Imported != 1
            || iwadProgress.Values.LastOrDefault() != 100
            || sourcePortProgress.Values.LastOrDefault() != 100
            || modProgress.Values.LastOrDefault() != 100)
        {
            throw new InvalidOperationException(
                $"First setup scan failed: IWAD={iwads.Imported}, " +
                $"SourcePort={sourcePorts.Imported}, Mod={mods.Imported}");
        }

        int sampleModId;
        await using (var modConnection = await OpenReadOnlyAsync(database))
        {
            sampleModId = await ScalarIntAsync(
                modConnection,
                "SELECT GameFileID FROM GameFiles " +
                "WHERE FileName='Mods\\sample.pk3';");
        }
        var mediaSource = Path.Combine(root, "media-source.png");
        await File.WriteAllBytesAsync(
            mediaSource,
            Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        await library.SetTitleArtworkAsync(sampleModId, mediaSource);
        await library.AddScreenshotsAsync(sampleModId, [mediaSource]);
        var media = await library.LoadGameMediaAsync(sampleModId);
        if (media.TitleArtwork is null || media.Screenshots.Count != 1)
        {
            throw new InvalidOperationException(
                "Managed title artwork and screenshot were not created.");
        }
        var titleArtworkPath = media.TitleArtwork.FullPath;
        var screenshotPath = media.Screenshots[0].FullPath;
        await library.RemoveTitleArtworkAsync(sampleModId);
        await library.RemoveScreenshotAsync(
            sampleModId,
            media.Screenshots[0].FileId);
        media = await library.LoadGameMediaAsync(sampleModId);
        if (media.TitleArtwork is not null
            || media.Screenshots.Count != 0
            || File.Exists(titleArtworkPath)
            || File.Exists(screenshotPath))
        {
            throw new InvalidOperationException(
                "Managed media deletion left database rows or physical files behind.");
        }

        var definitions = await library.LoadLauncherDefinitionsAsync();
        var iwadBytes = await File.ReadAllBytesAsync(iwadPath);
        var iwadArchivePath = Path.Combine(iwadDirectory, "TESTIWAD.zip");
        using (var stream = File.Create(iwadArchivePath))
        using (var archive = new System.IO.Compression.ZipArchive(
                   stream,
                   System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("TESTIWAD.WAD");
            await using var destination = entry.Open();
            await using var source = File.OpenRead(iwadPath);
            await source.CopyToAsync(destination);
        }
        File.Delete(iwadPath);

        iwads = await setup.ScanIwadsAsync();
        definitions = await library.LoadLauncherDefinitionsAsync();
        var relocatedIwad = definitions.Iwads.Single();
        if (iwads.Updated != 1
            || iwads.Removed != 0
            || !relocatedIwad.ArchiveFileName.Equals(
                @"GameWads\TESTIWAD.zip",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "An existing IWAD hash was not reassigned to its new archive " +
                "during the first scan.");
        }

        await library.DeleteIwadAsync(definitions.Iwads.Single().IwadId!.Value);
        await library.DeleteSourcePortAsync(
            definitions.SourcePorts.Single().SourcePortId!.Value);
        await using (var deletedConnection = await OpenReadOnlyAsync(database))
        {
            if (await ScalarIntAsync(deletedConnection, "SELECT COUNT(*) FROM IWads;") != 0
                || await ScalarIntAsync(
                    deletedConnection,
                    "SELECT COUNT(*) FROM SourcePorts;") != 0)
            {
                throw new InvalidOperationException(
                    "Manual launcher-definition deletion did not remove both definitions.");
            }
        }

        iwads = await setup.ScanIwadsAsync();
        sourcePorts = await setup.ScanSourcePortsAsync();
        if (iwads.Imported != 1 || sourcePorts.Imported != 1)
        {
            throw new InvalidOperationException(
                "Definitions were not recreated from files after manual deletion.");
        }

        File.Delete(iwadArchivePath);
        File.Delete(Path.Combine(sourcePortDirectory, "TestPort.exe"));
        iwads = await setup.ScanIwadsAsync();
        sourcePorts = await setup.ScanSourcePortsAsync();
        if (iwads.Removed != 1
            || sourcePorts.Removed != 1
            || iwads.RemovedItems.Count != 1
            || sourcePorts.RemovedItems.Count != 1)
        {
            throw new InvalidOperationException(
                $"Missing-definition reconciliation failed: IWAD={iwads.Removed}, " +
                $"SourcePort={sourcePorts.Removed}");
        }

        using (var stream = File.Create(iwadArchivePath))
        using (var archive = new System.IO.Compression.ZipArchive(
                   stream,
                   System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("TESTIWAD.WAD");
            await using var destination = entry.Open();
            await destination.WriteAsync(iwadBytes);
        }
        Directory.CreateDirectory(sourcePortDirectory);
        File.Copy(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe"),
            Path.Combine(sourcePortDirectory, "TestPort.exe"));
        iwads = await setup.ScanIwadsAsync();
        sourcePorts = await setup.ScanSourcePortsAsync();
        definitions = await library.LoadLauncherDefinitionsAsync();
        await library.DeleteIwadAsync(
            definitions.Iwads.Single().IwadId!.Value,
            deletePhysicalFiles: true);
        await library.DeleteSourcePortAsync(
            definitions.SourcePorts.Single().SourcePortId!.Value,
            deletePhysicalFiles: true);
        if (File.Exists(iwadArchivePath)
            || Directory.Exists(sourcePortDirectory))
        {
            throw new InvalidOperationException(
                "Optional physical definition deletion left an IWAD archive " +
                "or source-port directory behind.");
        }

        var deletableModPath = Path.Combine(modsDirectory, "delete-me.pk3");
        using (var stream = File.Create(deletableModPath))
        using (var archive = new System.IO.Compression.ZipArchive(
                   stream,
                   System.IO.Compression.ZipArchiveMode.Create))
        {
            _ = archive.CreateEntry("DELETE-ME.txt");
        }
        mods = await setup.ScanModsAsync();
        if (mods.Imported != 1)
        {
            throw new InvalidOperationException(
                "The disposable mod was not imported for physical deletion testing.");
        }
        int deletableModId;
        await using (var modConnection = await OpenReadOnlyAsync(database))
        {
            deletableModId = await ScalarIntAsync(
                modConnection,
                "SELECT GameFileID FROM GameFiles " +
                "WHERE FileName='Mods\\delete-me.pk3';");
        }
        await library.DeleteGameAsync(
            deletableModId,
            deletePhysicalFiles: true);
        if (File.Exists(deletableModPath))
        {
            throw new InvalidOperationException(
                "Optional physical mod deletion left the archive behind.");
        }
        await using (var deletedModConnection = await OpenReadOnlyAsync(database))
        {
            if (await ScalarIntAsync(
                    deletedModConnection,
                    "SELECT COUNT(*) FROM GameFiles " +
                    "WHERE GameFileID=" +
                    deletableModId.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) +
                    ";") != 0)
            {
                throw new InvalidOperationException(
                    "Physical mod deletion left the database entry behind.");
            }
        }

        await setup.CompleteWizardAsync();
        if (await setup.ShouldRunWizardAsync())
            throw new InvalidOperationException("First setup marker was not stored.");
        await using var connection = await OpenReadOnlyAsync(database);
        if (await ScalarIntAsync(connection, "SELECT COUNT(*) FROM IWads;") != 0
            || await ScalarIntAsync(connection, "SELECT COUNT(*) FROM SourcePorts;") != 0
            || await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM GameFiles WHERE FileName='Mods\\sample.pk3';") != 1)
        {
            throw new InvalidOperationException(
                "First setup did not create the expected portable database references.");
        }
    }
    finally
    {
        Environment.SetEnvironmentVariable(
            DoomLauncherDatabaseLocator.DatabaseEnvironmentVariable,
            null);
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}

sealed class RecordedProgress : IProgress<double>
{
    public List<double> Values { get; } = [];

    public void Report(double value) => Values.Add(value);
}
