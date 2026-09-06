using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using DoomLauncher.WinUI.Services;
using Microsoft.Data.Sqlite;

internal static class StabilizationTests
{
    private sealed class Locator(string path) : IDoomLauncherDatabaseLocator
    {
        public string FindDatabase() => path;
    }

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DoomLauncher stabilisation ä " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await VerifySchemaRollbackAsync();
            await VerifyMigrationCancellationAsync(root);
            var database = Path.Combine(root, "DoomLauncher.sqlite");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "DoomLauncher.Empty.sqlite"), database);
            var locator = new Locator(database);
            var library = new SqliteNativeLibraryService(locator);
            var setup = new FirstSetupService(locator, library);
            await setup.EnsureManagedLayoutAsync();
            var backups = Directory.GetFiles(Path.Combine(root, "Backups"), "schema-*.sqlite");
            await library.LoadSettingsAsync();
            Require(Directory.GetFiles(Path.Combine(root, "Backups"), "schema-*.sqlite").Length == backups.Length,
                "Schema initialization is idempotent and does not repeat backups");

            var incoming = Path.Combine(root, "Incoming");
            Directory.CreateDirectory(incoming);
            var archive = Path.Combine(incoming, "Jägermörder.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                using (var text = new StreamWriter(zip.CreateEntry("readme.txt").Open(), Encoding.UTF8))
                    text.Write("Title : Jägermörder\nAuthor : Test\nGame : Doom II\nAdvanced engine needed : GZDoom\n");
                using var wad = zip.CreateEntry("map.wad").Open();
                wad.Write(Wad("MAP01", "MAP02"));
            }
            var imported = await library.ImportAsync(archive);
            var originalBytes = await File.ReadAllBytesAsync(imported.DestinationPath);
            await File.WriteAllTextAsync(archive, "broken archive");
            try
            {
                await library.ImportAsync(archive, ImportFileConflictResolution.Overwrite);
                throw new Exception("Corrupt import unexpectedly succeeded");
            }
            catch (Exception exception) when (exception.Message != "Corrupt import unexpectedly succeeded") { }
            var restoredBytes = await File.ReadAllBytesAsync(imported.DestinationPath);
            Require(originalBytes.SequenceEqual(restoredBytes),
                "Failed overwrite restores the previous archive");
            Require((await library.LoadGameAsync(imported.GameFileId)).Title == "Jägermörder",
                "Failed overwrite preserves the original database entry");

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await ExpectAsync<OperationCanceledException>(() => setup.ScanModsAsync(cancelled.Token));
            }
            var mods = Path.Combine(root, "Data", "Mods");
            var offline = mods + "-offline";
            Directory.Move(mods, offline);
            await ExpectAsync<DirectoryNotFoundException>(() => setup.ScanModsAsync());
            Require((await library.LoadGameAsync(imported.GameFileId)).Title == "Jägermörder",
                "Unavailable Mods directory does not remove library entries");
            Directory.Move(offline, mods);
            File.Delete(imported.DestinationPath);
            var scan = await setup.ScanModsAsync();
            Require(scan.Removed == 1, "Complete scan removes actually missing entries");

            var file = Path.Combine(root, "journal.txt");
            await File.WriteAllTextAsync(file, "original");
            using (var journal = new FileChangeJournal())
            {
                journal.Track(file);
                await File.WriteAllTextAsync(file, "replacement");
                journal.Track(file + ".new");
                await File.WriteAllTextAsync(file + ".new", "new");
            }
            Require(await File.ReadAllTextAsync(file) == "original" && !File.Exists(file + ".new"),
                "Cancelled file transaction restores replacements and removes new files");
            VerifyMatcher();
            VerifyGenerator();
            VerifyLocalization();
            await VerifyLargeCatalogAsync(database);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task VerifySchemaRollbackAsync()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE Files (FileID INTEGER PRIMARY KEY, FileTypeID INTEGER, SourcePortID INTEGER);";
        await command.ExecuteNonQueryAsync();
        await ExpectAsync<SqliteException>(() => WinUiDatabaseSchema.EnsureAsync(connection, default));
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Files') WHERE name='DerivedFromFileID';";
        Require(Convert.ToInt32(await command.ExecuteScalarAsync()) == 0, "Failed schema migration rolls back ALTER TABLE");
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'WinUI_%';";
        Require(Convert.ToInt32(await command.ExecuteScalarAsync()) == 0, "Failed schema migration leaves no partial tables");
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private static async Task VerifyMigrationCancellationAsync(string root)
    {
        var legacy = Path.Combine(root, "Legacy");
        var destination = Path.Combine(root, "Destination");
        Directory.CreateDirectory(Path.Combine(legacy, "GameFiles"));
        Directory.CreateDirectory(Path.Combine(destination, "Data", "Mods"));
        var oldDatabase = Path.Combine(legacy, "DoomLauncher.sqlite");
        var targetDatabase = Path.Combine(destination, "DoomLauncher.sqlite");
        var template = Path.Combine(AppContext.BaseDirectory, "Assets", "DoomLauncher.Empty.sqlite");
        File.Copy(template, oldDatabase);
        File.Copy(template, targetDatabase);
        var originalDatabase = File.ReadAllBytes(targetDatabase);
        await using (var connection = new SqliteConnection($"Data Source={oldDatabase};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE Configuration SET Value='GameFiles' WHERE Name='GameFileDirectory';";
            await command.ExecuteNonQueryAsync();
        }
        var existing = Path.Combine(destination, "Data", "Mods", "marker.wad");
        File.WriteAllText(existing, "keep me");
        File.WriteAllText(Path.Combine(legacy, "GameFiles", "marker.wad"), "migrated content");
        var previous = Environment.GetEnvironmentVariable(DoomLauncherDatabaseLocator.DatabaseEnvironmentVariable);
        Environment.SetEnvironmentVariable(DoomLauncherDatabaseLocator.DatabaseEnvironmentVariable, targetDatabase);
        try
        {
            using var cancellation = new CancellationTokenSource();
            var service = new LegacyInstallationMigrationService(new Locator(targetDatabase));
            await ExpectAsync<OperationCanceledException>(() => service.MigrateAsync(legacy,
                new InlineProgress(value => { if (value >= 70) cancellation.Cancel(); }), cancellation.Token));
            Require(File.ReadAllText(existing) == "keep me", "Cancelled migration restores overwritten destination files");
            Require(originalDatabase.SequenceEqual(File.ReadAllBytes(targetDatabase)), "Cancelled migration preserves destination database");
            Require(File.ReadAllText(Path.Combine(legacy, "GameFiles", "marker.wad")) == "migrated content",
                "Migration never modifies source files");
        }
        finally
        {
            Environment.SetEnvironmentVariable(DoomLauncherDatabaseLocator.DatabaseEnvironmentVariable, previous);
        }
    }

    private static void VerifyMatcher()
    {
        var metadata = new ArchiveTextMetadata("", "", "", null, "Doom II", "GZDoom");
        NativeSourcePortDefinition Port(int id, string name, string executable, string version) =>
            new(id, name, "ports", executable, ".wad", "-file", "", version);
        var definitions = new LauncherDefinitionsData(
            [Port(1, "GZDoom 4.9", "gzdoom.exe", "4.9"), Port(2, "Custom installation", "gzdoom.exe", "4.10"),
                Port(3, "UZDoom", "uzdoom.exe", "999.0")], []);
        Require(LaunchDefinitionMatcher.Infer(metadata, definitions).SourcePortId == 2,
            "Port family uses executable and natural version ordering, not label length");
        Require(LaunchDefinitionMatcher.Infer(metadata with { SourcePort = "unknown engine" }, definitions).SourcePortId is null,
            "Unknown source-port requirements remain unassigned");
    }

    internal static ObsidianGenerationOptions Options => new("test", "doom1", "zdoom", "single", "original", 667,
        "22", "few", "few", "some", "mixed", "some", "0.5", "0.75", "easier", "few", "few", "bit_more", "bit_more", "normal", true, false);

    private static void VerifyGenerator()
    {
        ObsidianService.ValidateGeneratedMaps(Options, ["E1M1"]);
        Expect<InvalidDataException>(() => ObsidianService.ValidateGeneratedMaps(Options, ["MAP01"]));
        Expect<InvalidDataException>(() => ObsidianService.ValidateGeneratedMaps(Options, ["E1M1", "E1M2"]));
        Expect<InvalidDataException>(() => ObsidianService.ValidateGeneratedMaps(Options, []));
        Require(ObsidianService.BuildGeneratedDescription(Options, "21", "not reported", DateTime.Today)
            .Contains("not verified"), "Unknown effective seed is not presented as the requested seed");
        Console.WriteLine("PASS Generated map game, count and empty-output validation");
    }

    private static void VerifyLocalization()
    {
        var localization = new UiLocalization();
        foreach (var language in new[] { "en-US", "de-DE", "fr-FR", "es-ES" })
        {
            localization.SetLanguage(language);
            Require(!localization.Get("GeneratorChoiceSinglelevel").StartsWith("GeneratorChoice"),
                "Generator choices localized: " + language);
        }
        localization.SetLanguage("en-US");
    }

    private static async Task VerifyLargeCatalogAsync(string database)
    {
        await using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<5000) INSERT INTO GameFiles (FileName, Title, MapCount) SELECT 'Mods/test-'||x||'.wad', 'Test '||x, 1 FROM n;";
            await command.ExecuteNonQueryAsync();
        }
        var catalog = new SqliteLibraryCatalog(new Locator(database), new UiLocalization());
        var clock = Stopwatch.StartNew();
        var first = await catalog.LoadAsync();
        var cold = clock.ElapsedMilliseconds;
        clock.Restart();
        var second = await catalog.LoadAsync();
        Require(first.Entries.Count == 5000 && second.Entries.Count == 5000, "Large catalog reload preserves all 5000 entries");
        Console.WriteLine($"BENCHMARK catalog-5000 cold={cold}ms warm={clock.ElapsedMilliseconds}ms");
    }

    private static byte[] Wad(params string[] maps)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write(Encoding.ASCII.GetBytes("PWAD"));
        writer.Write(maps.Length);
        writer.Write(12);
        foreach (var map in maps)
        {
            writer.Write(12); writer.Write(0);
            writer.Write(Encoding.ASCII.GetBytes(map.PadRight(8, '\0')));
        }
        return bytes.ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
