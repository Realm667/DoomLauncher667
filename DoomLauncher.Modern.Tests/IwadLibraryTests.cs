using System.IO.Compression;
using System.Reflection;
using System.Text;
using DoomLauncher.WinUI.Services;
using Microsoft.Data.Sqlite;

internal static class IwadLibraryTests
{
    public static async Task VerifyAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DL667-iwad-versions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = Path.Combine(root, "DoomLauncher.sqlite");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "DoomLauncher.Empty.sqlite"), database);
            var locator = new Locator(database);
            var library = new SqliteNativeLibraryService(locator);
            await new FirstSetupService(locator, library).EnsureManagedLayoutAsync();
            var first = MakeArchive("one", 1);
            var second = MakeArchive("two", 2);
            var imported = await library.ImportAsync(first);
            await library.SaveIwadAsync(new(null, "Test Doom", imported.FileName, "DOOM.WAD", "1.6"));
            var entry = (await new SqliteLibraryCatalog(locator, new UiLocalization()).LoadAsync()).Entries.Single();
            Require(typeof(LibraryCatalogEntry).GetProperty("Version")?.GetValue(entry) as string == "1.6",
                "IWAD library rows must expose the stored version, independently of the selected base IWAD.");

            Require(entry.Author == "—", "An unknown author must be shown as a dash, never as the archive filename.");
            // Metadata fixture only: the synthetic payload is not a real Doom checksum match.
            var authorDefinition = (await library.LoadLauncherDefinitionsAsync()).Iwads.Single();
            var knownAuthor = authorDefinition with { Md5 = "1cd63c5ddff1bf8ce844237f580e9cf3", FileSize = 11159840 };
            await using (var authors = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString()))
            {
                await authors.OpenAsync();
                async Task Execute(string sql)
                { await using var command = authors.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
                async Task<string> DisplayAuthor() => (await new SqliteLibraryCatalog(locator, new UiLocalization()).LoadAsync()).Entries.Single().Author;
                await library.SaveIwadAsync(knownAuthor);
                Require(await DisplayAuthor() == "id Software", "Known IWAD credits must be populated from the checksum catalog.");
                await Execute("UPDATE GameFiles SET Author=NULL;");
                Require(await DisplayAuthor() == "id Software", "Existing blank IWAD authors must project verified catalog credits without archive reads.");
                await Execute("INSERT INTO WinUI_MetadataLocks(GameFileID,Field) SELECT GameFileID,'Author' FROM GameFiles;");
                await library.SaveIwadAsync(knownAuthor);
                Require(await DisplayAuthor() == "—", "An explicitly locked blank author must stay blank in storage and display.");
                await Execute("DELETE FROM WinUI_MetadataLocks; UPDATE GameFiles SET Author='My credit';");
                await library.SaveIwadAsync(knownAuthor);
                Require(await DisplayAuthor() == "My credit", "User authors must survive verified IWAD metadata updates.");
                await Execute("UPDATE GameFiles SET Author=NULL;");
                await library.SaveIwadAsync(authorDefinition);
                Require(await DisplayAuthor() == "—", "Structural IWAD recognition must not invent authors.");
            }
            await IwadVersionDetector.EnsureLoadedAsync();
            var registeredGrouping = IwadVersionDetector.StoredGrouping("54978d12de87f162b9bcc011676cb3c0", "");
            var latestGrouping = IwadVersionDetector.StoredGrouping("1cd63c5ddff1bf8ce844237f580e9cf3", "");
            Require(registeredGrouping.Key == "Doom Registered" && latestGrouping.Order > registeredGrouping.Order,
                "Doom 1.666 must precede 1.9 according to the curated chronology, not ordinary version parsing.");
            Require(IwadVersionDetector.StoredGrouping("unknown", "").Key == "", "Unknown contents must never group by title or filename.");
            var groupInput = new[] {
                entry with { GameFileId=10, Title="Renamed old Doom", Version="1.666", VersionGroup=registeredGrouping.Key, ReleaseOrder=registeredGrouping.Order },
                entry with { GameFileId=11, Title="My Doom", Version="1.9", VersionGroup=latestGrouping.Key, ReleaseOrder=latestGrouping.Order },
                entry with { GameFileId=12, Edition="The Ultimate Doom", VersionGroup="The Ultimate Doom", ReleaseOrder=1 },
                entry with { GameFileId=13, Title="My Doom", VersionGroup="", ReleaseOrder=null },
                entry with { GameFileId=14, Category="Mod", VersionGroup=latestGrouping.Key, ReleaseOrder=100 }
            };
            var groups = IwadVersionGroups.Build(groupInput);
            Require(groups.Count == 1 && groups["Doom Registered"].Select(e => e.GameFileId).SequenceEqual(new[] {11,10}),
                "Group exact verified editions, preserve IDs/custom titles and exclude Ultimate, unknown contents and Mods.");
            var statePath=Path.Combine(root,"group-state.json");
            var previousState=Environment.GetEnvironmentVariable(JsonUserLibraryStateStore.StateEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(JsonUserLibraryStateStore.StateEnvironmentVariable,statePath);
                var stateStore=new JsonUserLibraryStateStore();
                await stateStore.SaveAsync(UserLibraryState.Empty with {GroupIwadVersions=false, ExpandedIwadVersionGroups=new HashSet<string>{"Doom Registered"}});
                var restored=await stateStore.LoadAsync();
                Require(!restored.GroupIwadVersions && restored.ExpandedIwadVersionGroups!.SetEquals(new[]{"Doom Registered"}),
                    "Grouping preference and expansion keys must survive a portable state roundtrip.");
            }
            finally {Environment.SetEnvironmentVariable(JsonUserLibraryStateStore.StateEnvironmentVariable,previousState);}
            var method = typeof(SqliteNativeLibraryService).GetMethod("ImportIwadAsync", [typeof(string), typeof(CancellationToken)]);
            Require(method is not null, "An IWAD import must preserve separate versions rather than overwrite an archive with the same source name.");
            async Task<NativeImportResult> Import(string path, CancellationToken token = default) =>
                await (Task<NativeImportResult>)method!.Invoke(library, [path, token])!;
            // The existing identical manual definition is reused without adding a Mod row.
            var definitions = await library.LoadLauncherDefinitionsAsync();
            var detected = await library.DetectIwadVersionAsync(first, "DOOM.WAD");
            await library.SaveIwadAsync(definitions.Iwads.Single() with { Md5 = detected.Md5, FileSize = detected.FileSize });
            var duplicate = await Import(first);
            Require(duplicate.WasSkipped && duplicate.GameFileId == imported.GameFileId, "Duplicate IWAD contents must reuse the original definition.");
            var different = await Import(second);
            Require(different.GameFileId != imported.GameFileId && different.FileName.StartsWith("GameWads\\", StringComparison.OrdinalIgnoreCase), "Another IWAD version needs a separate managed archive and library ID.");
            var bytesBefore = await File.ReadAllBytesAsync(different.DestinationPath);
            var duplicateSecond = await Import(second);
            var bytesAfter = await File.ReadAllBytesAsync(different.DestinationPath);
            Require(duplicateSecond.WasSkipped && bytesBefore.SequenceEqual(bytesAfter), "Repeated IWAD imports must preserve the existing ZIP.");
            Require((await new SqliteLibraryCatalog(locator, new UiLocalization()).LoadAsync()).Entries.Count == 2, "Deduplication must not leave unprotected Mod rows.");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await Import(first, cancellation.Token); throw new InvalidOperationException("Cancelled IWAD import succeeded."); }
            catch (OperationCanceledException) { }

            var multiple = Path.Combine(root, "multiple.zip");
            using (var zip = ZipFile.Open(multiple, ZipArchiveMode.Create))
                foreach (var part in new[] { ("first/DOOM.WAD", (byte)3), ("second/DOOM.WAD", (byte)4) })
                    using (var stream = zip.CreateEntry(part.Item1).Open()) stream.Write(Wad(part.Item2));
            try { await Import(multiple); throw new InvalidOperationException("Ambiguous multi-IWAD ZIP was imported."); }
            catch (InvalidDataException) { }
            Require((await library.LoadLauncherDefinitionsAsync()).Iwads.Count == 2, "A rejected ambiguous archive must not change the library.");
            var disguised = Path.Combine(root, "same-name-patch.zip");
            using (var zip = ZipFile.Open(disguised, ZipArchiveMode.Create))
            {
                var patch = Wad(5); patch[0] = (byte)'P';
                using (var stream = zip.CreateEntry("first/DOOM.WAD").Open()) stream.Write(patch);
                using (var stream = zip.CreateEntry("second/DOOM.WAD").Open()) stream.Write(Wad(6));
            }
            try { await Import(disguised); throw new Exception("Ambiguous patch and base with the same internal filename were imported."); }
            catch (InvalidDataException) { }
            var third = MakeArchive("three", 3);
            var concurrent = await Task.WhenAll(Import(third), Import(third));
            Require(concurrent.Count(item => item.WasSkipped) == 1 && concurrent[0].GameFileId == concurrent[1].GameFileId,
                "Concurrent imports of the same IWAD must produce only one protected library entry.");
            var deathkings = Path.Combine(root, "deathkings.zip");
            using (var zip = ZipFile.Open(deathkings, ZipArchiveMode.Create))
                using (var stream = zip.CreateEntry("HEXDD.WAD").Open()) stream.Write(Wad(9));
            try { await Import(deathkings); throw new Exception("Deathkings without Hexen was imported."); }
            catch (InvalidOperationException) { }
            Require((await library.LoadLauncherDefinitionsAsync()).Iwads.Count == 3
                && (await new SqliteLibraryCatalog(locator, new UiLocalization()).LoadAsync()).Entries.Count == 3
                && Directory.GetFiles(Path.Combine(root, "Data", "GameWads")).Length == 2,
                "Failure after staging and DB insertion must roll back both database rows and the new ZIP.");
            File.Copy(different.DestinationPath, Path.Combine(root, "Data", "GameWads", "same-content-copy.zip"));
            await new FirstSetupService(locator, library).ScanIwadsAsync();
            Require((await new SqliteLibraryCatalog(locator, new UiLocalization()).LoadAsync()).Entries.Count == 3,
                "An IWAD rescan must not create an unprotected Mod row for an identical ZIP copy.");
            File.Copy(multiple, Path.Combine(root, "Data", "GameWads", "ambiguous.zip"));
            File.Copy(disguised, Path.Combine(root, "Data", "GameWads", "ambiguous-patch.zip"));
            var ambiguousScan = await new FirstSetupService(locator, library).ScanIwadsAsync();
            Require(ambiguousScan.Warnings.Count == 2 && (await library.LoadLauncherDefinitionsAsync()).Iwads.Count == 3
                && (await new SqliteLibraryCatalog(locator, new UiLocalization()).LoadAsync()).Entries.Count == 3,
                "The setup scanner must reject ambiguous internal WAD names without replacing definitions or creating unsafe entries.");
            Console.WriteLine("PASS IWAD library version, separate archive names, content deduplication, cancellation and ambiguous archive rejection");

            string MakeArchive(string folder, byte value)
            {
                var directory = Path.Combine(root, "input", folder); Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "doom.zip");
                using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
                using var output = zip.CreateEntry("DOOM.WAD").Open(); output.Write(Wad(value)); return path;
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private static byte[] Wad(byte value)
    {
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
        writer.Write(Encoding.ASCII.GetBytes("IWAD")); writer.Write(1); writer.Write(13);
        writer.Write(value); writer.Write(12); writer.Write(1); writer.Write(Encoding.ASCII.GetBytes("TESTDATA"));
        return memory.ToArray();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Locator(string database) : IDoomLauncherDatabaseLocator { public string FindDatabase() => database; }
}
