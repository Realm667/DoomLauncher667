using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DoomLauncher.Modern.Core.Launch;
using DoomLauncher.WinUI.Models;
using DoomLauncher.WinUI.Services;
using Microsoft.Data.Sqlite;

internal static class ExpansionTests
{
    private sealed class Locator(string path) : IDoomLauncherDatabaseLocator { public string FindDatabase() => path; }
    private sealed class State : IUserLibraryStateStore
    {
        public UserLibraryState Value = UserLibraryState.Empty;
        public Task<UserLibraryState> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);
        public Task SaveAsync(UserLibraryState value, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Value = value; return Task.CompletedTask; }
    }
    private sealed record Fixture(string Root, SqliteNativeLibraryService Library, LibraryFeatureStore Store, State State, NativeImportResult Mod);
    private static void Check(bool condition, string name)
    { if (!condition) throw new InvalidOperationException(name); Console.WriteLine("PASS " + name); }
    private static async Task Reject(Func<Task> action, string name)
    { try { await action(); } catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or SqliteException or JsonException) { Check(true, name); return; } throw new InvalidOperationException("Accepted invalid input: " + name); }
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DoomLauncher expansion " + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var a = await Create(Path.Combine(root, "PC-A")); var b = await Create(Path.Combine(root, "PC-B"));
            await Personal(a); await Metadata(a); await Maps(root); await Saves(a); await Network(root); await NativeLaunch(a);
            await Synchronization(a, b, Path.Combine(root, "Shared"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    public static async Task PrepareUiAsync(string root)
    {
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new InvalidOperationException("UI fixture must be empty.");
        var fixture = await Create(root); await new FirstSetupService(new Locator(fixture.Store.DatabasePath), fixture.Library).CompleteWizardAsync();
        await fixture.Library.UpdateGameAsync((await fixture.Library.LoadGameAsync(fixture.Mod.GameFileId)) with { Title = "RC6 Feature Test", Author = "Isolated test fixture" });
        await fixture.Store.SetQueuedAsync(fixture.Mod.GameFileId, true, true);
        await fixture.Library.CreateCollectionAsync("Test collection");
        await File.WriteAllTextAsync(Path.Combine(root, ".ui-test-instance"), "isolated-rc6-fixture");
    }
    public static async Task VerifyUpgradeCopyAsync(string source, string destination)
    {
        if (File.Exists(destination)) throw new InvalidOperationException("Upgrade test destination must not exist.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        await using (var original = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        await using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString()))
        { await original.OpenAsync(); await copy.OpenAsync(); original.BackupDatabase(copy); }
        await using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString()); await db.OpenAsync();
        var tables = new[] { "GameFiles", "Files", "SourcePorts", "IWads", "Tags", "TagMapping", "Stats", "Configuration", "WinUI_GameState", "WinUI_IdGamesMetadata", "WinUI_IwadMetadata", "WinUI_SourcePortCapabilities" };
        async Task<string> SnapshotTable(string table)
        {
            await using var command = db.CreateCommand(); command.CommandText = $"SELECT * FROM [{table}] ORDER BY rowid;";
            var rows = new List<object[]>(); await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) { var values = new object[reader.FieldCount]; reader.GetValues(values); rows.Add(values.Select(value => value is DBNull ? null! : value).ToArray()); }
            return JsonSerializer.Serialize(rows);
        }
        var before = new Dictionary<string, string>(); foreach (var table in tables) before[table] = await SnapshotTable(table);
        await WinUiDatabaseSchema.EnsureAsync(db, default);
        foreach (var table in tables) Check(await SnapshotTable(table) == before[table], "RC5 → RC6 migration preserves existing " + table + " values (isolated database copy)");
        await using var check = db.CreateCommand(); check.CommandText = "PRAGMA quick_check;"; Check(Convert.ToString(await check.ExecuteScalarAsync()) == "ok", "Upgraded database passes SQLite integrity check");
    }
    private static async Task<Fixture> Create(string root)
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "DoomLauncher.sqlite");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "DoomLauncher.Empty.sqlite"), path);
        var locator = new Locator(path); var library = new SqliteNativeLibraryService(locator); await new FirstSetupService(locator, library).EnsureManagedLayoutAsync();
        var source = Path.Combine(root, "input.zip"); using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
        { using var writer = new StreamWriter(zip.CreateEntry("map.wad").Open()); writer.BaseStream.Write(Wad(("MAP01", []), ("TEXTMAP", Encoding.UTF8.GetBytes("namespace=\"zdoom\"; vertex { x=0; y=0; } vertex { x=128; y=128; } linedef { v1=0; v2=1; } thing { type=3001; } sector { special=9; }")), ("ENDMAP", []))); }
        var mod = await library.ImportAsync(source); return new(root, library, new(locator), new(), mod);
    }
    private static async Task<MetadataSnapshot> Snapshot(Fixture fixture)
    { await using var db = await fixture.Store.OpenAsync(); return await LibraryFeatureStore.CaptureMetadataAsync(db, null, fixture.Mod.GameFileId, default); }
    private static async Task Personal(Fixture a)
    {
        var id = a.Mod.GameFileId; await a.Store.SetRatingAsync(id, 5);
        Check((await a.Store.LoadAsync())[id].Rating == 5 && (await Snapshot(a)).Rating is null or 0, "Personal stars are separate from community rating");
        await Reject(() => a.Store.SetRatingAsync(id, 0), "Personal rating rejects zero"); await Reject(() => a.Store.SetRatingAsync(id, 6), "Personal rating rejects six");
        await a.Store.SetRatingAsync(id, null); Check((await a.Store.LoadAsync())[id].Rating is null, "Personal rating can be cleared");
        await a.Store.SetQueuedAsync(id, true, true); await a.Store.SetQueuedAsync(id, true, true);
        Check((await a.Store.LoadAsync())[id].QueuePosition == 0, "Re-adding queued mod preserves its position");
        await a.Store.ReorderQueueAsync([id]); await Reject(() => a.Store.ReorderQueueAsync([]), "Stale queue reorder is rejected");
        await a.Library.SetGameFinishedAsync(id, true); Check((await a.Store.LoadAsync())[id].QueuePosition is null, "Finishing a mod optionally removes it from Play later");
        await a.Library.SetGameFinishedAsync(id, false); await a.Store.SetMapFinishedAsync(id, "map01", true);
        Check((await a.Store.LoadAsync())[id].CompletedMaps.Contains("MAP01"), "Per-map progress is case-insensitive and persistent");
        await a.Store.SetMapFinishedAsync(id, "MAP01", false); Check((await a.Store.LoadAsync())[id].CompletedMaps.Count == 0, "Map progress can be undone");
        var before = await a.Library.LoadGameAsync(id);
        await Reject(() => a.Store.ApplyBulkEditAsync(new HashSet<int> { id, int.MaxValue }, new(null, false, null, false, true, new HashSet<int>(), new HashSet<int>())), "Bulk edit is all-or-nothing when an entry disappears");
        await using (var db = await a.Store.OpenAsync()) { await using var command = db.CreateCommand(); command.CommandText = "SELECT Finished FROM WinUI_GameState WHERE GameFileID=$id"; command.Parameters.AddWithValue("$id", id); Check(Convert.ToInt32(await command.ExecuteScalarAsync()) == 0, "Failed bulk edit rolls back earlier finished changes"); }
        await Reject(() => a.Store.ApplyBulkEditAsync(new HashSet<int> { id }, new(int.MaxValue, true, null, false, null, new HashSet<int>(), new HashSet<int>())), "Bulk edit rejects a removed source port definition");
        await a.Library.CreateCollectionAsync("Expansion collection"); var tag = (await a.Library.LoadGameCollectionsAsync(id)).Collections.First(item => item.Name == "Expansion collection");
        await a.Store.ApplyBulkEditAsync(new HashSet<int> { id }, new(null, true, null, true, true, new HashSet<int> { tag.TagId }, new HashSet<int>()));
        Check((await a.Library.LoadGameCollectionsAsync(id)).SelectedTagIds.Contains(tag.TagId), "Bulk edit assigns collections atomically");
        await a.Library.SetGameFinishedAsync(id, false);
    }
    private static async Task Metadata(Fixture a)
    {
        var id = a.Mod.GameFileId; var initial = await a.Library.LoadGameAsync(id);
        await a.Library.UpdateGameAsync(initial with { Title = "Protected title", Author = "Protected author" });
        await a.Store.SetLocksAsync(id, new HashSet<string> { "Title" });
        var item = new IdGamesItem { Id = 667, Title = "Remote title", Author = "Remote author", Description = "Remote description", FileName = "input.zip", Directory = "levels/doom2/", Rating = 4.5, ReleaseDate = new DateTime(2001, 2, 3) };
        await a.Library.UpdateGameFromIdGamesAsync(id, item, default, new HashSet<string> { "Title", "Author", "Maps" });
        var snapshot = await Snapshot(a);
        Check(snapshot.Title == "Protected title" && snapshot.Author == "Remote author" && snapshot.MapCount == 1, "Metadata preview applies selected fields and honors persistent locks");
        Check(snapshot.Description != "Remote description" && snapshot.Rating is null or 0, "Unselected metadata fields are retained");
        var change = (await a.Store.LoadHistoryAsync(id)).First();
        await a.Store.UndoMetadataAsync(change.Id);
        Check((await Snapshot(a)).Author == "Protected author" && (await Snapshot(a)).IdGamesId is null, "Metadata undo restores values and the previous idgames link");
        await Reject(() => a.Store.UndoMetadataAsync(change.Id), "Metadata undo cannot run twice");
        await a.Library.UpdateGameFromIdGamesAsync(id, item); var stale = (await a.Store.LoadHistoryAsync(id)).First();
        await a.Library.UpdateGameAsync((await a.Library.LoadGameAsync(id)) with { Description = "Newer edit" });
        await Reject(() => a.Store.UndoMetadataAsync(stale.Id), "Metadata undo never overwrites a newer edit");
        await a.Store.SetLocksAsync(id, new HashSet<string>());
    }
    private static byte[] Wad(params (string Name, byte[] Bytes)[] lumps)
    {
        using var data = new MemoryStream(); using var output = new BinaryWriter(data); output.Write("PWAD"u8); output.Write(lumps.Length); output.Write(0);
        var offsets = new List<int>(); foreach (var lump in lumps) { offsets.Add((int)data.Position); output.Write(lump.Bytes); }
        var directory = (int)data.Position;
        for (var i = 0; i < lumps.Length; i++) { output.Write(offsets[i]); output.Write(lumps[i].Bytes.Length); output.Write(Encoding.ASCII.GetBytes(lumps[i].Name.PadRight(8, '\0')[..8])); }
        data.Position = 8; output.Write(directory); return data.ToArray();
    }
    private static async Task Maps(string root)
    {
        var vertices = new byte[8]; BinaryPrimitives.WriteInt16LittleEndian(vertices.AsSpan(4), 128); BinaryPrimitives.WriteInt16LittleEndian(vertices.AsSpan(6), 128);
        var lines = new byte[14]; BinaryPrimitives.WriteUInt16LittleEndian(lines.AsSpan(2), 1);
        var things = new byte[20]; BinaryPrimitives.WriteUInt16LittleEndian(things.AsSpan(6), 3001); BinaryPrimitives.WriteUInt16LittleEndian(things.AsSpan(16), 2001);
        var sectors = new byte[26]; BinaryPrimitives.WriteUInt16LittleEndian(sectors.AsSpan(22), 9);
        var path = Path.Combine(root, "classic.wad"); await File.WriteAllBytesAsync(path, Wad(("MAP01", []), ("THINGS", things), ("LINEDEFS", lines), ("VERTEXES", vertices), ("SECTORS", sectors), ("MAPINFO", Encoding.UTF8.GetBytes("map MAP01 \"Readable title\" { }"))));
        var map = (await MapBrowserService.ReadAsync(path, true)).Single();
        Check(map.Name == "MAP01" && map.Title == "Readable title" && map.Vertices.Count == 2 && map.Lines.Count == 1 && map.StandardMonsters == 1 && map.StandardItems == 1 && map.Secrets == 1, "Classic map browser extracts geometry, titles and conservative counts");
        Check((await MapBrowserService.ReadAsync(path, false)).Single().StandardMonsters is null, "Non-Doom actor counts are not guessed");
        var image = await MapBrowserService.RenderPreviewAsync(map, Path.Combine(root, "Previews")); Check(new FileInfo(image).Length > 100, "Automap preview renders a PNG");
        var udmf = "namespace=\"zdoom\"; /* test */ vertex { x=0; y=0; } vertex { x=64.5; y=128; } linedef { v1=0; v2=1; twosided=true; } thing { type=3001; } sector { secret=true; }";
        await File.WriteAllBytesAsync(path, Wad(("E1M1", []), ("TEXTMAP", Encoding.UTF8.GetBytes(udmf)), ("ENDMAP", [])));
        map = (await MapBrowserService.ReadAsync(path, true)).Single(); Check(map.Format == "UDMF" && map.Vertices[1].X == 64.5 && map.Lines[0].TwoSided && map.Secrets == 1, "UDMF geometry and secret flags are supported");
        await File.WriteAllBytesAsync(path, Wad(("E1M1", []), ("TEXTMAP", Encoding.UTF8.GetBytes(udmf.Replace("v2=1", "v2=1.5"))), ("ENDMAP", [])));
        await Reject(() => MapBrowserService.ReadAsync(path, true), "UDMF rejects fractional vertex indices");
        await File.WriteAllBytesAsync(path, "PWAD\xff"u8.ToArray()); await Reject(() => MapBrowserService.ReadAsync(path, true), "Malformed WAD geometry is rejected");
    }
    private static void Save(string path, string title)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create); using var output = new StreamWriter(archive.CreateEntry("info.json").Open());
        output.Write(JsonSerializer.Serialize(new Dictionary<string, string> { ["Title"] = title, ["Current Map"] = "MAP01", ["Engine"] = "GZDoom" }));
    }
    private static async Task Saves(Fixture a)
    {
        var engine = Path.Combine(a.Root, "gzdoom.exe"); var iwad = Path.Combine(a.Root, "own.wad"); await File.WriteAllTextAsync(engine, "fixture engine"); await File.WriteAllTextAsync(iwad, "fixture base game");
        var context = await SaveGameService.PrepareAsync(a.Root, a.Mod.FileName, a.Mod.DestinationPath, engine, "GZDoom", iwad, [a.Mod.DestinationPath], "", default);
        var path = Path.Combine(context.Directory, "save0.zds"); Save(path, "The test save"); await SaveGameService.RecordSessionAsync(context);
        var saves = await SaveGameService.LoadAsync(a.Root, a.Mod.FileName, context.Metadata.GameHash);
        Check(saves.Count == 1 && saves[0].Title == "The test save" && saves[0].Map == "MAP01" && saves[0].Metadata?.EngineHash == context.Metadata.EngineHash, "Save browser reads titles, map and recorded engine fingerprint");
        await SaveGameService.ValidateResumeAsync(context, path, false, default);
        await Reject(() => SaveGameService.ValidateResumeAsync(context with { Metadata = context.Metadata with { LaunchFingerprint = "changed" } }, path, false, default), "Resume rejects changed launch environment without confirmation");
        var backup = await SaveGameService.BackupAsync(a.Root, context.Directory); File.Delete(path); Save(path, "Changed save");
        await SaveGameService.RestoreBackupAsync(a.Root, backup!); Check((await SaveGameService.ReadEntryAsync(path)).Title == "The test save", "Save backup restores verified content and preserves pre-overwrite snapshot");
        await File.AppendAllTextAsync(Path.Combine(backup!, "save0.zds"), "damaged"); await Reject(() => SaveGameService.RestoreBackupAsync(a.Root, backup!), "Corrupt save backup is rejected before replacing files");
        var outside = Path.Combine(a.Root, "old.zds"); Save(outside, "Imported save");
        await Reject(() => SaveGameService.ValidateResumeAsync(context, outside, true, default), "Unmanaged save must be imported explicitly");
        var imported = await SaveGameService.ImportAsync(a.Root, outside, a.Mod.FileName, context.Metadata.GameHash);
        Check(File.Exists(outside) && (await SaveGameService.ReadSidecarAsync(imported))?.Imported == true, "Save import retains original and marks unknown compatibility");
    }
    private static byte[] ServerPayload(uint nonce)
    {
        using var buffer = new MemoryStream(); using var output = new BinaryWriter(buffer); void Text(string text) { output.Write(Encoding.UTF8.GetBytes(text)); output.Write((byte)0); }
        output.Write(nonce); Text("3.2"); output.Write(1u | 8 | 16 | 32 | 64 | 128 | 512 | 1024 | 2048 | 0x80000 | 0x80000000);
        Text("Test server"); Text("MAP01"); output.Write((byte)8); output.Write((byte)4); output.Write((byte)1); Text("test.wad"); output.Write(new byte[] { 0, 0, 0 }); Text("doom2.wad"); output.Write((byte)0); output.Write((byte)0); output.Write((byte)2);
        output.Write(5u); output.Write((byte)1); Text(new string('a', 32)); Text("Cooperative"); return buffer.ToArray();
    }
    private sealed class Starter : IProcessStarter
    {
        public System.Diagnostics.ProcessStartInfo? Info;
        public IGameLaunchSession Start(System.Diagnostics.ProcessStartInfo info) { Info = info; return new Session(); }
        private sealed class Session : IGameLaunchSession { public int ProcessId => 667; public Task WaitForExitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask; }
    }
    private static async Task NativeLaunch(Fixture a)
    {
        var iwadPath = Path.Combine(a.Root, "base.wad"); await File.WriteAllBytesAsync(iwadPath, Wad(("TITLEPIC", [])));
        var iwad = await a.Library.ImportAsync(iwadPath);
        await a.Library.SaveIwadAsync(new(null, "Fixture base", iwad.FileName, "DOOM2.WAD"));
        var portDirectory = Path.Combine(a.Root, "Data", "Sourceports", "gzdoom"); Directory.CreateDirectory(portDirectory);
        await File.WriteAllTextAsync(Path.Combine(portDirectory, "gzdoom.exe"), "fixture; never executed");
        await a.Library.SaveSourcePortAsync(new(null, "Fixture GZDoom", portDirectory, "gzdoom.exe", ".wad,.pk3", "-file", "+exec unwanted.cfg", ScreenshotSupport: "None"));
        var definitions = await a.Library.LoadLauncherDefinitionsAsync(); var sp = definitions.SourcePorts.Single(); var iw = definitions.Iwads.Single();
        var starter = new Starter(); var launcher = new NativeGameLaunchService(new Locator(a.Store.DatabasePath), starter);
        var launched = await launcher.LaunchAsync(new(a.Mod.GameFileId, "Fixture", SourcePortId: sp.SourcePortId, IwadId: iw.IwadId, Map: "MAP01"));
        Check(starter.Info!.ArgumentList.Contains("-savedir") && starter.Info.ArgumentList.Contains("-warp"), "Native launch applies managed saves and explicit map selection");
        await launched.Session.WaitForExitAsync();
        launched = await launcher.LaunchAsync(new(iwad.GameFileId, "Network fixture", SourcePortId: sp.SourcePortId, IwadId: iw.IwadId,
            Multiplayer: new("join", "127.0.0.1", Mods: [new(a.Mod.GameFileId, "map.wad")])));
        Check(starter.Info!.ArgumentList.Contains("-join") && !starter.Info.ArgumentList.Contains("+exec") && starter.Info.ArgumentList.Any(value => value.EndsWith("map.wad")), "Multiplayer uses ordered explicit archive entries and excludes saved extra arguments");
        await launched.Session.WaitForExitAsync();
    }
    private static byte[] Reply(byte[] payload)
    { var data = new byte[payload.Length + 4]; BinaryPrimitives.WriteUInt32LittleEndian(data, 5660023); payload.CopyTo(data, 4); return ZandronumHuffman.Encode(data); }
    private static byte[] Segment(byte[] payload, int number, int count, int offset, int size)
    { using var stream = new MemoryStream(); using var output = new BinaryWriter(stream); output.Write(5660032u); output.Write((byte)number); output.Write((byte)count); output.Write((ushort)offset); output.Write((ushort)size); output.Write((ushort)payload.Length); output.Write(payload, offset, size); return ZandronumHuffman.Encode(stream.ToArray()); }
    private static async Task Network(string root)
    {
        var payload = ServerPayload(667); var endpoint = new IPEndPoint(IPAddress.Loopback, 10666);
        var server = MultiplayerService.ParseServerPayload(payload, endpoint, 667, 15);
        Check(server.Name == "Test server" && server.Players == 2 && server.MaximumPlayers == 4 && server.Files.Single().Md5 == new string('a', 32) && server.GameMode == "Cooperative", "Zandronum protocol parses players, IWAD, required files and hashes");
        var browserServers = new[] {
            server with { Name = "Alpha", Players = 2, MaximumPlayers = 8, Ping = 20 },
            server with { Name = "Beta", Endpoint = new(IPAddress.Loopback, 10667), Players = 10, MaximumPlayers = 10, Ping = 100 },
            server with { Name = "Empty", Endpoint = new(IPAddress.Loopback, 10668), Players = 0, Ping = 5 } };
        var sorted = MultiplayerBrowserQuery.Apply(browserServers, "", false, false, ServerSortColumn.Players, false);
        Check(sorted.Select(item => item.Players).SequenceEqual(new[] { 0, 2, 10 }), "Browser player sorting is numeric");
        sorted = MultiplayerBrowserQuery.Apply(browserServers, "", false, false, ServerSortColumn.Ping, false);
        Check(sorted.Select(item => item.Ping).SequenceEqual(new long[] { 5, 20, 100 }), "Browser ping sorting is numeric");
        Check(MultiplayerBrowserQuery.Apply(browserServers, "ALPHA map01 doom2", false, false, ServerSortColumn.Name, false).Single().Name == "Alpha",
            "Server search matches all terms across independent columns without case sensitivity");
        Check(MultiplayerBrowserQuery.Apply(browserServers, "", true, true, ServerSortColumn.Name, false).Single().Name == "Alpha",
            "Server filters exclude empty and full servers independently");
        Check(MultiplayerBrowserQuery.Apply(browserServers, "missing", false, false, ServerSortColumn.Name, false).Count == 0,
            "Server search returns an explicit empty result");
        await Reject(() => Task.Run(() => MultiplayerService.ParseServerPayload(payload, endpoint, 1, 0)), "Stale server response is rejected");
        var assembler = new MultiplayerService.ServerReplyAssembler(); var half = payload.Length / 2;
        Check(assembler.Add(Segment(payload, 1, 2, half, payload.Length - half)) is null && assembler.Add(Segment(payload, 0, 2, 0, half))!.SequenceEqual(payload), "Segmented server packets reassemble out of order");
        assembler = new(); assembler.Add(Segment(payload, 0, 2, 0, half)); var overlap = assembler;
        await Reject(() => Task.Run(() => overlap.Add(Segment(payload, 1, 2, half - 1, payload.Length - half + 1))), "Overlapping server segments are rejected");
        Check(ZandronumHuffman.Decode(ZandronumHuffman.Encode(payload)).SequenceEqual(payload), "Legacy uncompressed query wire format round-trips");
        // Canonical tree: the leftmost leaf is 128, four zero bits per symbol.
        Check(ZandronumHuffman.Decode(new byte[] { 0, 0 }).SequenceEqual(new byte[] { 128, 128 }), "Native Huffman decoder traverses the canonical protocol tree");
        using (var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            var address = (IPEndPoint)udp.Client.LocalEndPoint!;
            var response = Task.Run(async () => { var request = await udp.ReceiveAsync(); var query = ZandronumHuffman.Decode(request.Buffer); var nonce = BinaryPrimitives.ReadUInt32LittleEndian(query.AsSpan(8)); await udp.SendAsync(Reply(ServerPayload(nonce)), request.RemoteEndPoint); });
            var queried = await MultiplayerService.QueryServerAsync(address, default); await response; Check(queried.Name == "Test server", "UDP query adapter interoperates with local protocol fixture");
        }
        var launch = MultiplayerService.BuildLaunchArguments("zandronum.exe", new("host", null)); Check(launch.Contains("+sv_updatemaster") && launch.Contains("0"), "LAN host is not advertised on the internet");
        Check(MultiplayerService.BuildLaunchArguments("gzdoom.exe", new("join", "127.0.0.1")).Contains("-join"), "ZDoom-family direct connection uses the native join adapter");
        await Reject(() => MultiplayerService.ResolveAddressAsync("127.0.0.1 +exec exploit.cfg"), "Server addresses cannot inject engine arguments");
        var wad = Path.Combine(root, "download.wad"); await File.WriteAllBytesAsync(wad, Wad(("MAP01", []))); var md5 = Convert.ToHexString(MD5.HashData(await File.ReadAllBytesAsync(wad))).ToLowerInvariant();
        Check(await MultiplayerDownloadService.VerifyAndPrepareAsync(wad, new("test.wad", md5), default) == wad, "Server mod download verifies supplied checksum");
        await Reject(() => MultiplayerDownloadService.VerifyAndPrepareAsync(wad, new("test.wad", new string('0', 32)), default), "Mismatched server download is rejected");
        await Reject(() => MultiplayerDownloadService.VerifyAndPrepareAsync(wad, new("doom2.wad", md5), default), "IWAD downloads are prohibited");
        var zip = Path.Combine(root, "wrapped.zip"); using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) archive.CreateEntryFromFile(wad, "test.wad");
        Check((await MultiplayerDownloadService.VerifyAndPrepareAsync(zip, new("test.wad", md5), default)).EndsWith(".zip"), "Required WAD inside a downloaded archive is verified");
    }
    private static async Task Synchronization(Fixture a, Fixture b, string folder)
    {
        var one = new PersonalSyncService(a.Store, a.State); var two = new PersonalSyncService(b.Store, b.State);
        await a.Store.SetRatingAsync(a.Mod.GameFileId, 4); await a.Store.SetQueuedAsync(a.Mod.GameFileId, true, false); await a.Store.SetMapFinishedAsync(a.Mod.GameFileId, "MAP01", true);
        a.State.Value = a.State.Value with { FavoriteGameFileIds = new HashSet<int> { a.Mod.GameFileId }, Language = "de-DE", ListDensity = "UltraCompact" };
        var first = await one.PreviewAsync(a.Root, folder, true, true, true, null, default); await one.ApplyAsync(first, null, default);
        var incoming = await two.PreviewAsync(b.Root, folder, true, true, true, null, default);
        foreach (var change in incoming.Differences) if (change.Resolution == SyncResolution.Unresolved) change.Resolution = SyncResolution.Remote;
        await two.ApplyAsync(incoming, null, default);
        var personal = (await b.Store.LoadAsync())[b.Mod.GameFileId];
        Check(personal.Rating == 4 && personal.QueuePosition.HasValue && personal.CompletedMaps.Contains("MAP01") && b.State.Value.FavoriteGameFileIds.Contains(b.Mod.GameFileId), "Sync transfers personal data using content identity rather than database IDs");
        Check(b.State.Value.Language == "de-DE" && b.State.Value.ListDensity == "UltraCompact", "Optional appearance settings synchronize without machine-specific paths");
        Check((await SaveGameService.LoadAsync(b.Root, b.Mod.FileName, await SaveGameService.HashAsync(b.Mod.DestinationPath))).Count >= 1, "Optional managed saves synchronize through verified immutable objects");
        Check((await two.PreviewAsync(b.Root, folder, true, true, false, null, default)).Differences.Count == 0, "Repeated synchronization is idempotent including save metadata");
        Check(!Directory.EnumerateFiles(folder, "*.sqlite", SearchOption.AllDirectories).Any(), "Synchronization never copies an active SQLite database");
        await a.Store.SetRatingAsync(a.Mod.GameFileId, 5); await b.Store.SetRatingAsync(b.Mod.GameFileId, 2);
        await one.ApplyAsync(await one.PreviewAsync(a.Root, folder, false, false, false, null, default), null, default);
        var conflict = await two.PreviewAsync(b.Root, folder, false, false, false, null, default);
        Check(conflict.Differences.Any(change => change.Field == "rating" && change.Conflict), "Concurrent changes create explicit per-field conflicts");
        await Reject(() => two.ApplyAsync(conflict, null, default), "Unresolved sync conflicts block application");
        foreach (var change in conflict.Differences) change.Resolution = SyncResolution.Remote;
        await b.Store.SetQueuedAsync(b.Mod.GameFileId, false, false); await Reject(() => two.ApplyAsync(conflict, null, default), "Sync rejects a stale local preview");
        var fresh = await two.PreviewAsync(b.Root, folder, false, false, false, null, default); foreach (var change in fresh.Differences) if (change.Conflict) change.Resolution = SyncResolution.Remote;
        await two.ApplyAsync(fresh, null, default); Check((await b.Store.LoadAsync())[b.Mod.GameFileId].Rating == 5, "Conflict resolution applies the explicitly selected peer value");
        Check(Directory.EnumerateFiles(Path.Combine(folder, "DoomLauncher667-Sync", "Versions"), "*.json").Any(), "Sync retains previous manifest revisions");
        await Reject(() => one.PreviewAsync(a.Root, a.Root, false, false, false, null, default), "Sync target cannot overlap the installation");
        var manifest = Path.Combine(folder, "DoomLauncher667-Sync", "state.json"); await File.WriteAllTextAsync(manifest, "{\"Format\":999}");
        await Reject(() => one.PreviewAsync(a.Root, folder, false, false, false, null, default), "Unsupported sync formats never reset the local library");
    }
}
