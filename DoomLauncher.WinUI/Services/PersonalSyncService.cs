using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DoomLauncher.WinUI.Services;

public enum SyncResolution { Unresolved, Local, Remote }
public sealed class SyncDifference(string contentId, string title, string field, string? local, string? remote, bool conflict, SyncResolution resolution)
{
    public string ContentId { get; } = contentId;
    public string Title { get; } = title;
    public string Field { get; } = field;
    public string? Local { get; } = local;
    public string? Remote { get; } = remote;
    public bool Conflict { get; } = conflict;
    public SyncResolution Resolution { get; set; } = resolution;
    public string Label => $"{Title} · {Field}";
}
public sealed record SyncSave(string RelativePath, string Sha256, long Length, SaveGameMetadata? Metadata);
public sealed record SyncDocument(int Format, string Revision, string UpdatedUtc, Dictionary<string, Dictionary<string, string?>> Entries);
public sealed record SyncLocalMod(int Id, string FileName, string Title, string ContentId);
public sealed record PersonalSyncPlan(string Root, string Folder, string Target, string? RemoteDigest,
    SyncDocument Remote, Dictionary<string, Dictionary<string, string?>> Local,
    IReadOnlyList<SyncLocalMod> Mods, IReadOnlyList<SyncDifference> Differences,
    IReadOnlyDictionary<string, string> LocalSaveFiles, bool IncludeSettings, bool IncludeSaves,
    int MissingLocalMods, UserLibraryState UserState);

public sealed class PersonalSyncService(LibraryFeatureStore store, IUserLibraryStateStore stateStore)
{
    private const long MaximumManifestBytes = 64L * 1024 * 1024;
    private static readonly HashSet<string> SettingFields = ["theme", "language", "visibleColumns", "collectionColumns", "listDensity", "accordionDensity", "libraryFilters"];
    private static readonly HashSet<string> ModFields = ["rating", "favorite", "finished", "queue", "autoDequeue", "collections", "locks"];
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
    private static T Read<T>(string? value, T fallback) => value is null ? fallback : JsonSerializer.Deserialize<T>(value)!;
    private static bool Hash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static string Key(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string? Digest(string path)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > MaximumManifestBytes) throw new InvalidDataException("Sync manifest is too large.");
        using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input));
    }
    private static bool EqualFields(Dictionary<string, Dictionary<string, string?>> left, Dictionary<string, Dictionary<string, string?>> right) =>
        left.Count == right.Count && left.All(entry => right.TryGetValue(entry.Key, out var fields) && entry.Value.Count == fields.Count && entry.Value.All(field => fields.TryGetValue(field.Key, out var value) && value == field.Value));

    public async Task<PersonalSyncPlan> PreviewAsync(string root, string folder, bool settings, bool saves,
        bool verifyArchives, IProgress<string>? progress, CancellationToken token, string? previousSnapshot = null)
    {
        root = Path.GetFullPath(root); folder = Path.Combine(Path.GetFullPath(folder), "DoomLauncher667-Sync");
        if (Within(folder, root) || Within(root, folder)) throw new InvalidOperationException("Choose a sync folder outside this installation.");
        RejectLinks(folder); var target = Key(folder.ToUpperInvariant()); var manifest = Path.Combine(folder, "state.json");
        RejectLinks(manifest);
        if (File.Exists(manifest) && new FileInfo(manifest).Length > MaximumManifestBytes) throw new InvalidDataException("Sync manifest is too large.");
        var digest = Digest(manifest); var remote = File.Exists(manifest) ? await LoadDocumentAsync(manifest, token)
            : new SyncDocument(1, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.ToString("O"), []);
        if (previousSnapshot is not null) remote = await LoadDocumentAsync(previousSnapshot, token);
        var baseline = await LoadBaselineAsync(target, token);
        if (digest is null && baseline.Count > 0) throw new IOException("The previous sync manifest disappeared. Restore it or choose a different target; it will not be silently recreated.");
        var user = await stateStore.LoadAsync(token);
        var local = await BuildLocalAsync(root, settings, saves, verifyArchives, progress, user, token);
        var changes = new List<SyncDifference>();
        foreach (var (content, fields) in local.Fields)
        {
            var remoteFields = remote.Entries.GetValueOrDefault(content) ?? [];
            var beforeFields = baseline.GetValueOrDefault(content) ?? [];
            foreach (var field in fields.Keys.Concat(remoteFields.Keys).Concat(beforeFields.Keys).Distinct().Order())
            {
                if (content == "@settings" && !settings || field.StartsWith("save/") && !saves) continue;
                var before = beforeFields.GetValueOrDefault(field) ?? Default(field);
                var own = fields.GetValueOrDefault(field) ?? (field.StartsWith("save/") ? before : Default(field));
                var other = remoteFields.GetValueOrDefault(field) ?? Default(field);
                if (own == other) continue;
                var conflict = own != before && other != before;
                changes.Add(new(content, content == "@settings" ? "Settings" : local.Mods.First(mod => mod.ContentId == content).Title,
                    field, own, other, conflict, conflict ? SyncResolution.Unresolved : own == before ? SyncResolution.Remote : SyncResolution.Local));
            }
        }
        return new(root, folder, target, digest, remote, local.Fields, local.Mods, changes, local.SaveFiles, settings, saves,
            remote.Entries.Keys.Count(id => id != "@settings" && local.Mods.All(mod => mod.ContentId != id)), user);
    }

    private sealed record LocalState(Dictionary<string, Dictionary<string, string?>> Fields, IReadOnlyList<SyncLocalMod> Mods, Dictionary<string, string> SaveFiles);
    private async Task<LocalState> BuildLocalAsync(string root, bool settings, bool saves, bool verify,
        IProgress<string>? progress, UserLibraryState user, CancellationToken token)
    {
        await using var db = await store.OpenAsync(token); await using var command = db.CreateCommand();
        command.CommandText = "SELECT Value FROM Configuration WHERE Name='GameFileDirectory';";
        var gameRoot = Path.GetFullPath(Path.Combine(root, Convert.ToString(await command.ExecuteScalarAsync(token)) ?? "Data"));
        command.CommandText = "SELECT g.GameFileID,g.FileName,COALESCE(g.Title,g.FileName),COALESCE(s.Finished,0) FROM GameFiles g LEFT JOIN WinUI_GameState s ON s.GameFileID=g.GameFileID ORDER BY g.GameFileID;";
        var games = new List<(int Id, string File, string Title, bool Finished)>();
        await using (var rows = await command.ExecuteReaderAsync(token)) while (await rows.ReadAsync(token)) games.Add((rows.GetInt32(0), rows.GetString(1), rows.GetString(2), rows.GetInt32(3) != 0));
        var personal = await store.LoadAsync(token); var fields = new Dictionary<string, Dictionary<string, string?>>();
        var mods = new List<SyncLocalMod>(); var saveFiles = new Dictionary<string, string>();
        var locksById = new Dictionary<int, List<string>>();
        command.CommandText = "SELECT GameFileID,Field FROM WinUI_MetadataLocks ORDER BY Field;";
        await using (var rows = await command.ExecuteReaderAsync(token)) while (await rows.ReadAsync(token))
        { if (!locksById.TryGetValue(rows.GetInt32(0), out var values)) locksById[rows.GetInt32(0)] = values = []; values.Add(rows.GetString(1)); }
        var allSaves = new List<(string Path, SaveGameMetadata? Metadata)>(); var managedRoot = Path.Combine(root, "Data", "SaveGames");
        if (saves && Directory.Exists(managedRoot))
            foreach (var path in Directory.EnumerateFiles(managedRoot, "*.zds", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                token.ThrowIfCancellationRequested(); if (allSaves.Count >= 20000) throw new IOException("Too many managed saves.");
                allSaves.Add((path, await SaveGameService.ReadSidecarAsync(path, token)));
            }
        for (var index = 0; index < games.Count; index++)
        {
            token.ThrowIfCancellationRequested(); var game = games[index]; var path = Path.Combine(gameRoot, game.File);
            progress?.Report($"{index + 1} / {games.Count} · {game.Title}"); if (!File.Exists(path)) continue;
            var info = new FileInfo(path); var length = info.Length; var modified = info.LastWriteTimeUtc.Ticks;
            command.Parameters.Clear(); command.CommandText = "SELECT Sha256 FROM WinUI_ContentHashes WHERE FileName=$file AND Length=$length AND ModifiedTicks=$modified;";
            command.Parameters.AddWithValue("$file", game.File); command.Parameters.AddWithValue("$length", length); command.Parameters.AddWithValue("$modified", modified);
            var hash = !verify ? await command.ExecuteScalarAsync(token) as string : null;
            if (hash is null)
            {
                hash = await SaveGameService.HashAsync(path, token); info.Refresh();
                if (!info.Exists || info.Length != length || info.LastWriteTimeUtc.Ticks != modified) throw new IOException("An archive changed during synchronization; retry when idle.");
                command.CommandText = "INSERT INTO WinUI_ContentHashes(FileName,Length,ModifiedTicks,Sha256) VALUES($file,$length,$modified,$hash) ON CONFLICT(FileName) DO UPDATE SET Length=$length,ModifiedTicks=$modified,Sha256=$hash;";
                command.Parameters.AddWithValue("$hash", hash); await command.ExecuteNonQueryAsync(token);
            }
            if (fields.ContainsKey(hash)) throw new InvalidOperationException("Byte-identical duplicate library entries must be resolved before synchronization: " + game.Title);
            mods.Add(new(game.Id, game.File, game.Title, hash)); var data = personal.GetValueOrDefault(game.Id) ?? PersonalModData.Empty(game.Id);
            var own = new Dictionary<string, string?> { ["rating"] = Json(data.Rating), ["favorite"] = Json(user.FavoriteGameFileIds.Contains(game.Id)), ["finished"] = Json(game.Finished), ["queue"] = Json(data.QueuePosition), ["autoDequeue"] = Json(data.AutoDequeue) };
            command.Parameters.Clear(); command.Parameters.AddWithValue("$id", game.Id);
            command.CommandText = "SELECT Name FROM Tags t JOIN TagMapping m ON m.TagID=t.TagID WHERE m.FileID=$id ORDER BY Name COLLATE NOCASE;";
            var tags = new List<string>(); await using (var rows = await command.ExecuteReaderAsync(token)) while (await rows.ReadAsync(token)) tags.Add(rows.GetString(0));
            own["collections"] = Json(tags.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray());
            own["locks"] = Json(locksById.GetValueOrDefault(game.Id)?.ToArray() ?? []);
            command.CommandText = "SELECT MapName,Finished FROM WinUI_MapProgress WHERE GameFileID=$id;";
            await using (var rows = await command.ExecuteReaderAsync(token)) while (await rows.ReadAsync(token)) own["map/" + rows.GetString(0).ToUpperInvariant()] = Json(rows.GetInt32(1) != 0);
            if (saves)
            {
                foreach (var save in allSaves.Where(save => save.Metadata?.LibraryFile.Equals(game.File.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) == true
                    || Path.GetDirectoryName(save.Path)!.Equals(Path.Combine(managedRoot, hash), StringComparison.OrdinalIgnoreCase)))
                {
                    var relative = save.Metadata?.SyncPath ?? Path.GetRelativePath(Path.Combine(root, "Data", "SaveGames"), save.Path).Replace('\\', '/');
                    if (!relative.StartsWith(hash + "/", StringComparison.OrdinalIgnoreCase)) relative = hash + "/Legacy-" + relative;
                    ValidateSavePath(relative, hash); var blob = await SaveGameService.HashAsync(save.Path, token);
                    var metadata = save.Metadata is null ? null : save.Metadata with { LibraryFile = "", SyncPath = relative };
                    var descriptor = new SyncSave(relative, blob, new FileInfo(save.Path).Length, metadata);
                    own["save/" + Key(relative.ToLowerInvariant())] = Json(descriptor); saveFiles[blob] = save.Path;
                }
            }
            fields[hash] = own;
        }
        if (settings) fields["@settings"] = new() { ["theme"] = Json(user.Theme), ["language"] = Json(user.Language), ["visibleColumns"] = Json(user.VisibleColumns),
            ["collectionColumns"] = Json(user.CollectionVisibleColumns), ["listDensity"] = Json(user.ListDensity), ["accordionDensity"] = Json(user.AccordionDensity), ["libraryFilters"] = Json(user.LibraryFilterTags.Order().ToArray()) };
        return new(fields, mods, saveFiles);
    }

    public async Task ApplyAsync(PersonalSyncPlan plan, IProgress<string>? progress, CancellationToken token)
    {
        if (plan.Differences.Any(change => change.Resolution == SyncResolution.Unresolved)) throw new InvalidOperationException("Resolve every conflict before synchronization.");
        Directory.CreateDirectory(plan.Folder); RejectLinks(plan.Folder);
        RejectLinks(Path.Combine(plan.Folder, "sync.lock")); RejectLinks(Path.Combine(plan.Folder, "state.json"));
        await using var syncLock = new FileStream(Path.Combine(plan.Folder, "sync.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var manifest = Path.Combine(plan.Folder, "state.json"); if (Digest(manifest) != plan.RemoteDigest) throw new IOException("Remote sync data changed. Refresh the preview before applying.");
        var user = await stateStore.LoadAsync(token);
        var current = await BuildLocalAsync(plan.Root, plan.IncludeSettings, plan.IncludeSaves, false, progress, user, token);
        if (!EqualFields(current.Fields, plan.Local)) throw new IOException("Local library data changed. Refresh the preview before applying.");
        var merged = plan.Remote.Entries.ToDictionary(pair => pair.Key, pair => new Dictionary<string, string?>(pair.Value));
        var baseline = await LoadBaselineAsync(plan.Target, token);
        foreach (var (id, own) in plan.Local)
        {
            if (!merged.TryGetValue(id, out var fields)) merged[id] = fields = [];
            var prior = baseline.GetValueOrDefault(id) ?? [];
            foreach (var key in own.Keys.Concat(fields.Keys).Concat(prior.Keys).Distinct().ToArray())
            {
                if (key.StartsWith("save/") && !plan.IncludeSaves) continue;
                var difference = plan.Differences.FirstOrDefault(change => change.ContentId == id && change.Field == key);
                var selected = difference is null ? own.GetValueOrDefault(key) ?? prior.GetValueOrDefault(key) ?? fields.GetValueOrDefault(key)
                    : difference.Resolution == SyncResolution.Local ? difference.Local : difference.Remote;
                if (selected is not null) fields[key] = selected;
            }
        }
        var next = new SyncDocument(1, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.ToString("O"), merged); ValidateDocument(next);
        using var journal = new FileChangeJournal();
        if (plan.IncludeSaves)
            foreach (var mod in plan.Mods)
                foreach (var pair in merged[mod.ContentId].Where(pair => pair.Key.StartsWith("save/") && pair.Value is not null))
                {
                    var save = Read<SyncSave>(pair.Value, null!)!; ValidateSavePath(save.RelativePath, mod.ContentId);
                    var remoteBlob = Path.Combine(plan.Folder, "Objects", save.Sha256);
                    if (!File.Exists(remoteBlob))
                    {
                        if (!plan.LocalSaveFiles.TryGetValue(save.Sha256, out var source)) throw new FileNotFoundException("A synchronized save object is missing.", remoteBlob);
                        await CopyVerifiedAsync(source, remoteBlob, save.Sha256, save.Length, token);
                    }
                    else if (new FileInfo(remoteBlob).Length != save.Length || await SaveGameService.HashAsync(remoteBlob, token) != save.Sha256) throw new InvalidDataException("A remote save object is damaged.");
                    var target = Path.Combine(plan.Root, "Data", "SaveGames", save.RelativePath.Replace('/', Path.DirectorySeparatorChar)); RejectLinks(target);
                    if (!File.Exists(target) || await SaveGameService.HashAsync(target, token) != save.Sha256)
                    {
                        var parent = Path.GetDirectoryName(target)!; Directory.CreateDirectory(parent); await SaveGameService.BackupAsync(plan.Root, parent, token);
                        journal.Track(target); journal.Track(target + ".dl667.json");
                        await CopyVerifiedAsync(remoteBlob, target, save.Sha256, save.Length, token);
                        var metadata = save.Metadata is null ? new SaveGameMetadata(1, mod.FileName.Replace('\\', '/'), mod.ContentId, "Unknown", "", "", "", DateTimeOffset.UtcNow.ToString("O"), true, save.RelativePath)
                            : save.Metadata with { LibraryFile = mod.FileName.Replace('\\', '/'), SyncPath = save.RelativePath };
                        await File.WriteAllTextAsync(target + ".dl667.json", Json(metadata), token);
                    }
                }
        var favorites = user.FavoriteGameFileIds.ToHashSet(); var changedUser = user;
        await using (var db = await store.OpenAsync(token))
        await using (var tx = (SqliteTransaction)await db.BeginTransactionAsync(token))
        {
            foreach (var mod in plan.Mods)
            {
                var fields = merged[mod.ContentId]; var rating = Read<int?>(fields.GetValueOrDefault("rating"), null); var queue = Read<int?>(fields.GetValueOrDefault("queue"), null);
                var favorite = Read(fields.GetValueOrDefault("favorite"), false); if (favorite) favorites.Add(mod.Id); else favorites.Remove(mod.Id);
                await using var command = db.CreateCommand(); command.Transaction = tx;
                command.CommandText = "INSERT INTO WinUI_PersonalMods(GameFileID,PersonalRating,QueuePosition,AutoDequeue,UpdatedUtc) VALUES($id,$rating,$queue,$auto,$at) ON CONFLICT(GameFileID) DO UPDATE SET PersonalRating=$rating,QueuePosition=$queue,AutoDequeue=$auto,UpdatedUtc=$at; INSERT INTO WinUI_GameState(GameFileID,Finished) VALUES($id,$done) ON CONFLICT(GameFileID) DO UPDATE SET Finished=$done;";
                command.Parameters.AddWithValue("$id", mod.Id); command.Parameters.AddWithValue("$rating", (object?)rating ?? DBNull.Value); command.Parameters.AddWithValue("$queue", (object?)queue ?? DBNull.Value);
                command.Parameters.AddWithValue("$auto", Read(fields.GetValueOrDefault("autoDequeue"), false) ? 1 : 0); command.Parameters.AddWithValue("$done", Read(fields.GetValueOrDefault("finished"), false) ? 1 : 0); command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); await command.ExecuteNonQueryAsync(token);
                command.Parameters.Clear(); command.Parameters.AddWithValue("$id", mod.Id); command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                foreach (var field in fields.Where(pair => pair.Key.StartsWith("map/")))
                {
                    command.CommandText = "INSERT INTO WinUI_MapProgress(GameFileID,MapName,Finished,UpdatedUtc) VALUES($id,$map,$done,$at) ON CONFLICT(GameFileID,MapName) DO UPDATE SET Finished=$done,UpdatedUtc=$at;";
                    command.Parameters.AddWithValue("$map", field.Key[4..]); command.Parameters.AddWithValue("$done", Read(field.Value, false) ? 1 : 0); await command.ExecuteNonQueryAsync(token); command.Parameters.RemoveAt("$map"); command.Parameters.RemoveAt("$done");
                }
                command.CommandText = "DELETE FROM WinUI_MetadataLocks WHERE GameFileID=$id; DELETE FROM TagMapping WHERE FileID=$id;"; await command.ExecuteNonQueryAsync(token);
                command.Parameters.AddWithValue("$field", "");
                foreach (var field in Read(fields.GetValueOrDefault("locks"), Array.Empty<string>()))
                { command.CommandText = "INSERT INTO WinUI_MetadataLocks(GameFileID,Field) VALUES($id,$field);"; command.Parameters["$field"].Value = field; await command.ExecuteNonQueryAsync(token); }
                command.Parameters.RemoveAt("$field"); command.Parameters.AddWithValue("$name", "");
                foreach (var tag in Read(fields.GetValueOrDefault("collections"), Array.Empty<string>()))
                {
                    command.CommandText = "INSERT INTO Tags(Name,HasTab) SELECT $name,0 WHERE NOT EXISTS(SELECT 1 FROM Tags WHERE Name=$name COLLATE NOCASE); INSERT INTO TagMapping(FileID,TagID) SELECT $id,TagID FROM Tags WHERE Name=$name COLLATE NOCASE AND NOT EXISTS(SELECT 1 FROM TagMapping WHERE FileID=$id AND TagID=Tags.TagID);";
                    command.Parameters["$name"].Value = tag; await command.ExecuteNonQueryAsync(token);
                }
            }
            changedUser = user with { FavoriteGameFileIds = favorites };
            if (plan.IncludeSettings && merged.TryGetValue("@settings", out var settings)) changedUser = ApplySettings(changedUser, settings);
            try { await stateStore.SaveAsync(changedUser, token); await tx.CommitAsync(token); }
            catch { await stateStore.SaveAsync(user, CancellationToken.None); throw; }
        }
        journal.Commit();
        // Local state is now a valid merged revision. A failed network publish is retried from
        // the previous baseline; it never rolls back newer local user data or resets the peer.
        try
        {
            await PublishAsync(plan.Folder, next, token);
            await using var db = await store.OpenAsync(token); await using var tx = (SqliteTransaction)await db.BeginTransactionAsync(token);
            foreach (var (id, own) in plan.Local)
            {
                var previous = baseline.GetValueOrDefault(id) ?? [];
                foreach (var field in merged[id]) if (!field.Key.StartsWith("save/") || plan.IncludeSaves) previous[field.Key] = field.Value;
                await using var command = db.CreateCommand(); command.Transaction = tx;
                command.CommandText = "INSERT INTO WinUI_SyncBaseline(Target,ContentID,Payload) VALUES($target,$id,$payload) ON CONFLICT(Target,ContentID) DO UPDATE SET Payload=$payload;";
                command.Parameters.AddWithValue("$target", plan.Target); command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$payload", Json(previous)); await command.ExecuteNonQueryAsync(token);
            }
            await tx.CommitAsync(token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
        { throw new IOException("Local changes were applied safely, but publishing/baseline confirmation failed. Retry synchronization; do not replace either database.", error); }
    }

    private async Task<Dictionary<string, Dictionary<string, string?>>> LoadBaselineAsync(string target, CancellationToken token)
    {
        await using var db = await store.OpenAsync(token); await using var command = db.CreateCommand(); command.CommandText = "SELECT ContentID,Payload FROM WinUI_SyncBaseline WHERE Target=$target;";
        command.Parameters.AddWithValue("$target", target); var result = new Dictionary<string, Dictionary<string, string?>>(); await using var rows = await command.ExecuteReaderAsync(token);
        while (await rows.ReadAsync(token)) result[rows.GetString(0)] = JsonSerializer.Deserialize<Dictionary<string, string?>>(rows.GetString(1))!; return result;
    }
    private static UserLibraryState ApplySettings(UserLibraryState user, IReadOnlyDictionary<string, string?> settings) => user with
    {
        Theme = Read(settings.GetValueOrDefault("theme"), user.Theme), Language = Read(settings.GetValueOrDefault("language"), user.Language),
        VisibleColumns = Read(settings.GetValueOrDefault("visibleColumns"), user.VisibleColumns), CollectionVisibleColumns = Read(settings.GetValueOrDefault("collectionColumns"), user.CollectionVisibleColumns),
        ListDensity = Read(settings.GetValueOrDefault("listDensity"), user.ListDensity), AccordionDensity = Read(settings.GetValueOrDefault("accordionDensity"), user.AccordionDensity), LibraryFilterTags = Read(settings.GetValueOrDefault("libraryFilters"), user.LibraryFilterTags)
    };
    private static string? Default(string field) => field switch
    { "rating" or "queue" => "null", "collections" or "locks" => "[]", "favorite" or "finished" or "autoDequeue" => "false", _ => field.StartsWith("map/") ? "false" : null };

    public static async Task<SyncDocument> LoadDocumentAsync(string path, CancellationToken token)
    {
        if (new FileInfo(path).Length > MaximumManifestBytes) throw new InvalidDataException("Sync manifest is too large.");
        var document = JsonSerializer.Deserialize<SyncDocument>(await File.ReadAllTextAsync(path, token)) ?? throw new InvalidDataException("Empty sync manifest."); ValidateDocument(document); return document;
    }
    private static void ValidateDocument(SyncDocument document)
    {
        if (document.Format != 1 || !Guid.TryParse(document.Revision, out _) || document.Entries is null || document.Entries.Count > 50000) throw new InvalidDataException("Unsupported sync manifest.");
        foreach (var (id, fields) in document.Entries)
        {
            if (id != "@settings" && !Hash(id) || fields is null || fields.Count > 50000) throw new InvalidDataException("Invalid sync entry.");
            foreach (var (key, value) in fields)
            {
                if (value is null) continue;
                if (value.Length > 2 * 1024 * 1024) throw new InvalidDataException("Sync field is too large.");
                using var parsed = JsonDocument.Parse(value);
                if (id == "@settings")
                {
                    if (!SettingFields.Contains(key)) throw new InvalidDataException("Unknown synchronized setting.");
                    if (key is "theme" or "language" or "listDensity" or "accordionDensity")
                    {
                        var text = Read(value, "");
                        if (text.Length is 0 or > 200 || text.Any(char.IsControl)
                            || key == "language" && text is not ("en-US" or "de-DE" or "fr-FR" or "es-ES")
                            || key is "listDensity" or "accordionDensity" && text is not ("Compact" or "Normal" or "UltraCompact")) throw new InvalidDataException("Invalid synchronized setting.");
                    }
                    else
                    {
                        var values = Read(value, Array.Empty<string>());
                        if (values.Length > 1000 || values.Any(text => text is null || text.Length > 200 || text.Any(char.IsControl))) throw new InvalidDataException("Invalid synchronized settings list.");
                    }
                    continue;
                }
                if (key.StartsWith("save/"))
                {
                    var save = Read<SyncSave>(value, null!)!;
                    if (save is null || !Hash(save.Sha256) || save.Length is < 1 or > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Invalid synchronized save."); ValidateSavePath(save.RelativePath, id); continue;
                }
                if (key.StartsWith("map/"))
                { if (key.Length is <= 4 or > 68 || key[4..].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.'))) throw new InvalidDataException("Invalid synchronized map."); _ = Read(value, false); continue; }
                if (!ModFields.Contains(key)) throw new InvalidDataException("Unknown synchronized mod field.");
                switch (key)
                {
                    case "rating": if (Read<int?>(value, null) is { } rating && rating is < 1 or > 5) throw new InvalidDataException("Invalid personal rating."); break;
                    case "queue": if (Read<int?>(value, null) is < 0) throw new InvalidDataException("Invalid queue position."); break;
                    case "favorite": case "finished": case "autoDequeue": _ = Read(value, false); break;
                    case "locks": if (Read(value, Array.Empty<string>()).Any(field => !LibraryFeatureStore.MetadataFields.Contains(field))) throw new InvalidDataException("Unknown metadata lock."); break;
                    case "collections": if (Read(value, Array.Empty<string>()).Any(name => name.Length is 0 or > 200 || name.Any(char.IsControl))) throw new InvalidDataException("Invalid collection name."); break;
                }
            }
        }
    }
    private static void ValidateSavePath(string path, string id)
    {
        if (path.Length > 500 || !path.StartsWith(id + "/", StringComparison.OrdinalIgnoreCase) || path.Contains('\\') || path.Contains(':') || !path.EndsWith(".zds", StringComparison.OrdinalIgnoreCase)
            || path.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || System.Text.RegularExpressions.Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))) throw new InvalidDataException("Unsafe synchronized save path.");
    }
    private static bool Within(string path, string root) => Path.GetFullPath(path).TrimEnd('\\', '/').Equals(Path.GetFullPath(root).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
        || Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked synchronization paths are not supported.");
    }
    private static async Task CopyVerifiedAsync(string source, string destination, string hash, long length, CancellationToken token)
    {
        RejectLinks(destination); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var input = File.OpenRead(source)) await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true)) await input.CopyToAsync(output, token);
            if (new FileInfo(temporary).Length != length || await SaveGameService.HashAsync(temporary, token) != hash) throw new InvalidDataException("Synchronized file checksum mismatch.");
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static async Task PublishAsync(string folder, SyncDocument document, CancellationToken token)
    {
        var path = Path.Combine(folder, "state.json"); var versions = Path.Combine(folder, "Versions"); RejectLinks(path); RejectLinks(versions); Directory.CreateDirectory(versions);
        if (File.Exists(path)) File.Copy(path, Path.Combine(versions, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json"));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        { await File.WriteAllTextAsync(temporary, Json(document), token); await using (var flush = new FileStream(temporary, FileMode.Open, FileAccess.Write, FileShare.None)) flush.Flush(true); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
