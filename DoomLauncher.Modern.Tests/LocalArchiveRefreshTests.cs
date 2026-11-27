using System.IO.Compression;
using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;
using DoomLauncher.WinUI.Services;
using Microsoft.Data.Sqlite;

internal static class LocalArchiveRefreshTests
{
    private sealed class Locator(string path) : IDoomLauncherDatabaseLocator { public string FindDatabase() => path; }
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DoomLauncher-local-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await VerifyMetadataIdentityAsync(root);
            var inner = Path.Combine(root, "updated.pk3");
            using (var archive = ZipFile.Open(inner, ZipArchiveMode.Create))
            {
                using (var padding = archive.CreateEntry("padding.bin", CompressionLevel.NoCompression).Open())
                {
                    var block = new byte[1024 * 1024];
                    for (var index = 0; index < 257; index++) padding.Write(block);
                }
                archive.CreateEntry("maps/MAP01.wad");
                archive.CreateEntry("maps/MAP02.wad");
                using var image = new Bitmap(16, 16);
                image.SetPixel(0, 0, Color.Red);
                using var title = archive.CreateEntry("graphics/TITLEPIC.png").Open();
                image.Save(title, ImageFormat.Png);
            }
            var outer = Path.Combine(root, "same name.zip");
            using (var archive = ZipFile.Open(outer, ZipArchiveMode.Create))
                archive.CreateEntryFromFile(inner, "updated.pk3", CompressionLevel.Fastest);
            var maps = await MapNameExtractor.ExtractAsync(outer, default);
            Check(maps.SequenceEqual(new[] { "MAP01", "MAP02" }), "Large single-PK3 ZIP exposes its maps without the 256 MB memory cutoff");
            Check(await TitlePicExtractor.TryExtractPngAsync(outer, default) is { Length: > 0 }, "Single-PK3 ZIP exposes its embedded TITLEPIC");
            var tiny = Path.Combine(root,"tiny.pk3");
            using (var archive=ZipFile.Open(tiny,ZipArchiveMode.Create)) archive.CreateEntry("maps/MAP01.wad");
            var wrapped = tiny;
            for(var index=0;index<3;index++)
            {
                var next=Path.Combine(root,$"layer-{index}.zip");
                using(var archive=ZipFile.Open(next,ZipArchiveMode.Create)) archive.CreateEntryFromFile(wrapped,Path.GetFileName(wrapped));
                wrapped=next;
            }
            var temporaryBefore=Directory.GetFiles(Path.GetTempPath(),"DoomLauncher-payload-*").Order().ToArray();
            try { using var unexpected=await ArchivePayload.OpenAsync(wrapped,default); throw new Exception("Excessive nesting was accepted."); }
            catch(InvalidDataException) { }
            Check(temporaryBefore.SequenceEqual(Directory.GetFiles(Path.GetTempPath(),"DoomLauncher-payload-*").Order()),
                "Nested archive depth is bounded and failed extraction removes temporary payloads");
            await VerifyLocalRefreshAsync(Path.Combine(root, "library"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private static async Task VerifyMetadataIdentityAsync(string root)
    {
        var inner=Path.Combine(root,"Venturous_1.7.2.pk3");
        using(var archive=ZipFile.Open(inner,ZipArchiveMode.Create))
        {
            using(var main=new StreamWriter(archive.CreateEntry("wadinfo2.txt").Open()))
                main.Write("Title : Venturous - In The Footsteps Of The Fallen\nFilename : Venturous 1.7.1.pk3\nAuthor : Pixelfox\nDescription : Main campaign\n");
            using(var addon=new StreamWriter(archive.CreateEntry("wadinfo.txt").Open()))
                addon.Write("Title : Venturous Frozen Enemies\nFilename : VenturousFrozenEnemies.pk3\nAuthor : Other author\nRelease date : February 2025\nDescription : Bundled add-on, not the main game\nGame : Doom II\nAdvanced engine needed : GZDoom\n");
        }
        var outer=Path.Combine(root,"Venturous 1.3.1.zip");
        using(var archive=ZipFile.Open(outer,ZipArchiveMode.Create)) archive.CreateEntryFromFile(inner,Path.GetFileName(inner));
        var metadata=await ArchiveTextMetadataReader.ReadAsync(outer,default);
        Check(metadata.Title=="Venturous - In The Footsteps Of The Fallen" && metadata.Author=="Pixelfox" && metadata.ReleaseDate is null,
            "Primary archive identity outranks a more complete bundled add-on readme");
        metadata=await ArchiveTextMetadataReader.ReadAsync(inner,default,"renamed-local.pk3","Venturous - In The Footsteps Of The Fallen");
        Check(metadata.Title=="Venturous - In The Footsteps Of The Fallen", "Known mod title identifies primary text after a custom archive rename");
        var unrelated=Path.Combine(root,"main-game.pk3");
        using(var archive=ZipFile.Open(unrelated,ZipArchiveMode.Create))
        using(var text=new StreamWriter(archive.CreateEntry("wadinfo.txt").Open()))
            text.Write("Title : Bundled addon\nFilename : different-addon.pk3\nAuthor : Other author\n");
        Check((await ArchiveTextMetadataReader.ReadAsync(unrelated,default)).Quality==0,
            "An unrelated add-on document alone does not become the main mod metadata");

        var renamedZip = Path.Combine(root, "renamed-download.zip");
        using (var archive = ZipFile.Open(renamedZip, ZipArchiveMode.Create))
        {
            using (var wad = archive.CreateEntry("episode.wad").Open())
                wad.Write("PWAD\0\0\0\0\u000c\0\0\0"u8);
            using (var readme = new StreamWriter(archive.CreateEntry("episode.txt").Open()))
                readme.Write("Title : The Main Episode\nFilename : episode.wad\nAuthor : Main author\n");
            using (var addon = new StreamWriter(archive.CreateEntry("addon.txt").Open()))
                addon.Write("Title : Optional add-on\nFilename : addon.wad\nAuthor : Other author\nDescription : Add-on instructions\nGame : Doom II\n");
        }
        metadata = await ArchiveTextMetadataReader.ReadAsync(renamedZip, default);
        Check(metadata.Title == "The Main Episode" && metadata.Author == "Main author",
            "A renamed ZIP retains the metadata for its only bundled playable file");
        using (var archive = ZipFile.Open(renamedZip, ZipArchiveMode.Update))
        using (var wad = archive.CreateEntry("addon.wad").Open())
            wad.Write("PWAD\0\0\0\0\u000c\0\0\0"u8);
        Check((await ArchiveTextMetadataReader.ReadAsync(renamedZip, default)).Quality == 0,
            "A renamed ZIP with several playable files does not guess the main mod metadata");
    }

    private static async Task VerifyLocalRefreshAsync(string root)
    {
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "DoomLauncher.sqlite");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "DoomLauncher.Empty.sqlite"), database);
        var locator = new Locator(database);
        var library = new SqliteNativeLibraryService(locator);
        await new FirstSetupService(locator, library).EnsureManagedLayoutAsync();
        var incoming = Path.Combine(root, "incoming"); Directory.CreateDirectory(incoming);
        var source = Path.Combine(incoming, "unchanged name.zip");
        WriteArchive(source, "Original title", "Old author", 1);
        var imported = await library.ImportAsync(source);
        var store = new LibraryFeatureStore(locator);
        await store.SetRatingAsync(imported.GameFileId, 4);
        await store.SetQueuedAsync(imported.GameFileId, true, false);
        await store.SetLocksAsync(imported.GameFileId, new HashSet<string> { "Title" });
        await using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=database, Pooling=false }.ToString());
        await db.OpenAsync();
        await ExecuteAsync(db, $"UPDATE GameFiles SET MinutesPlayed=42 WHERE GameFileID={imported.GameFileId}; INSERT INTO Tags(Name,HasTab) VALUES('Retained collection',1); INSERT INTO TagMapping(FileID,TagID) VALUES({imported.GameFileId},last_insert_rowid());");
        var protectedBefore = await ProtectedStateAsync(db, imported.GameFileId);
        WriteArchive(imported.DestinationPath, "New title", "New author", 2);
        var bytes = await File.ReadAllBytesAsync(imported.DestinationPath);
        var result = await library.RefreshGameFromLocalArchiveAsync(imported.GameFileId);
        var game = await library.LoadGameAsync(imported.GameFileId);
        Check(result.MapCount == 2 && result.ArtworkImported && game.Title == "Original title" && game.Author == "New author",
            "Same-name replacement refreshes local maps, text and artwork without /idgames, honoring locks");
        var protectedAfter = await ProtectedStateAsync(db, imported.GameFileId);
        var bytesAfter = await File.ReadAllBytesAsync(imported.DestinationPath);
        Check(protectedBefore == protectedAfter && bytes.SequenceEqual(bytesAfter),
            "Local refresh retains the archive, launch columns, playtime, personal ratings, queue, collections and /idgames link");
        var history = await store.LoadHistoryAsync(imported.GameFileId);
        Check(history.Count == 1 && history[0].Source.StartsWith("local-archive:"), "Local metadata changes are recorded in the existing undo history");
        await store.UndoMetadataAsync(history[0].Id);
        Check((await library.LoadGameAsync(imported.GameFileId)).Author == "Old author", "Existing metadata undo restores a local refresh");
        await store.SetLocksAsync(imported.GameFileId, new HashSet<string> { "Title", "Maps" });
        await library.RefreshGameFromLocalArchiveAsync(imported.GameFileId);
        await using var count = db.CreateCommand(); count.CommandText = "SELECT MapCount FROM GameFiles WHERE GameFileID=" + imported.GameFileId;
        Check(Convert.ToInt32(await count.ExecuteScalarAsync()) == 1, "Local refresh respects a locked map list");
        // Archives without readme metadata must not erase existing text.
        File.Delete(imported.DestinationPath);
        using (var archive = ZipFile.Open(imported.DestinationPath, ZipArchiveMode.Create)) archive.CreateEntry("maps/MAP03.wad");
        await store.SetLocksAsync(imported.GameFileId, new HashSet<string>());
        await library.RefreshGameFromLocalArchiveAsync(imported.GameFileId);
        Check((await library.LoadGameAsync(imported.GameFileId)).Author == "New author", "Missing archive text retains existing metadata");
        var beforeFailure = await LibraryFeatureStore.CaptureMetadataAsync(db, null, imported.GameFileId, default);
        await File.WriteAllTextAsync(imported.DestinationPath, "broken ZIP");
        await RejectAsync(() => library.RefreshGameFromLocalArchiveAsync(imported.GameFileId));
        Check(beforeFailure == await LibraryFeatureStore.CaptureMetadataAsync(db, null, imported.GameFileId, default), "Corrupt local archive never overwrites metadata with an empty map list");
        await RejectAsync(() => library.RefreshGameFromLocalArchiveAsync(imported.GameFileId, cancellationToken:new CancellationToken(true)));
        await ExecuteAsync(db, $"INSERT INTO IWads(Name,FileName,GameFileID) VALUES('Protected','DOOM.WAD',{imported.GameFileId});");
        await RejectAsync(() => library.RefreshGameFromLocalArchiveAsync(imported.GameFileId));
        Check(beforeFailure == await LibraryFeatureStore.CaptureMetadataAsync(db, null, imported.GameFileId, default), "Local refresh rejects IWADs before reading or changing metadata");
    }

    private static void WriteArchive(string path, string title, string author, int maps)
    {
        if (File.Exists(path)) File.Delete(path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var text = new StreamWriter(archive.CreateEntry("readme.txt").Open()))
            text.Write($"Title : {title}\nAuthor : {author}\nDescription : Updated local description\n");
        for (var index = 1; index <= maps; index++) archive.CreateEntry($"maps/MAP{index:00}.wad");
        using var image = new Bitmap(16,16); image.SetPixel(0,0,Color.Blue);
        using var output = archive.CreateEntry("graphics/TITLEPIC.png").Open(); image.Save(output,ImageFormat.Png);
    }

    private static async Task ExecuteAsync(SqliteConnection db, string sql)
    { await using var command = db.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }

    private static async Task<string> ProtectedStateAsync(SqliteConnection db, int id)
    {
        var state = new List<object?>();
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT * FROM GameFiles ORDER BY GameFileID";
        var mutable = new HashSet<string> { "Title", "Author", "Description", "ReleaseDate", "Map", "MapCount", "IsSyncNeeded" };
        await using (var row = await command.ExecuteReaderAsync())
        { while(await row.ReadAsync()) for(var n=0;n<row.FieldCount;n++) if(row.GetInt32(row.GetOrdinal("GameFileID")) != id || !mutable.Contains(row.GetName(n))) state.Add(new {Column=row.GetName(n),Value=row.IsDBNull(n)?null:row.GetValue(n)}); }
        foreach(var table in new[] { "TagMapping", "Tags", "WinUI_PersonalMods", "WinUI_GameState", "WinUI_MapProgress", "WinUI_IdGamesMetadata", "SourcePorts", "IWads", "Configuration", "Files" })
        {
            command.CommandText = "SELECT * FROM " + table + (table == "Files" ? $" WHERE GameFileID<>{id} OR FileTypeID NOT IN(4,6)" : "");
            await using var rows = await command.ExecuteReaderAsync();
            while(await rows.ReadAsync()) { var values=new object?[rows.FieldCount]; for(var n=0;n<values.Length;n++)values[n]=rows.IsDBNull(n)?null:rows.GetValue(n);state.Add(values); }
        }
        return JsonSerializer.Serialize(state);
    }

    private static async Task RejectAsync(Func<Task> action)
    { try { await action(); } catch { return; } throw new InvalidOperationException("Unsafe local refresh was accepted."); }

    internal static async Task VerifyRealCopyAsync(string root, int id)
    {
        root = Path.GetFullPath(root);
        if (!File.Exists(Path.Combine(root, ".local-archive-test-instance"))) throw new InvalidOperationException("An explicitly marked isolated copy is required.");
        var locator = new Locator(Path.Combine(root, "DoomLauncher.sqlite"));
        var library = new SqliteNativeLibraryService(locator);
        var path = await library.ResolveManagedGameFileAsync(id);
        if (path is null || !Path.GetFullPath(path).StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The copied archive must resolve inside the isolated root.");
        await using var db = new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=locator.FindDatabase(),Pooling=false}.ToString());
        await db.OpenAsync();
        await using(var guard = db.CreateCommand())
        {
            guard.CommandText = "SELECT FileName FROM Files WHERE GameFileID="+id+" AND FileTypeID IN(4,6)";
            await using var rows = await guard.ExecuteReaderAsync();
            while(await rows.ReadAsync()) if(Path.GetFileName(rows.GetString(0)) != rows.GetString(0)) throw new InvalidOperationException("Copied artwork must use local basenames.");
        }
        var before = await ProtectedStateAsync(db,id);
        var oldMetadata = await LibraryFeatureStore.CaptureMetadataAsync(db,null,id,default);
        string Hash() { using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file)); }
        var oldHash=Hash();
        var result=await library.RefreshGameFromLocalArchiveAsync(id);
        Check(before == await ProtectedStateAsync(db,id) && oldHash == Hash(), "Real copied mod refresh retains protected database rows and the exact archive bytes");
        var metadata=await LibraryFeatureStore.CaptureMetadataAsync(db,null,id,default);
        if(oldMetadata.Title=="Venturous - In The Footsteps Of The Fallen")
            Check(metadata.Title==oldMetadata.Title && metadata.Author=="Pixelfox" && metadata.ReleaseDate==oldMetadata.ReleaseDate
                && metadata.Description is not null && !metadata.Description.Contains("freezing enemies",StringComparison.OrdinalIgnoreCase),
                "Real Venturous refresh preserves main-mod identity instead of selecting bundled Frozen Enemies metadata");
        var report = new { OldMapCount=oldMetadata.MapCount, NewMapCount=result.MapCount, result.ArtworkImported, ProtectedDataRetained=true, metadata.Title, metadata.Author, metadata.ReleaseDate, ArchiveSHA256=oldHash };
        var json=JsonSerializer.Serialize(report);
        await File.WriteAllTextAsync(Path.Combine(root,"local-refresh-report.json"),json);
        Console.WriteLine(json);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
