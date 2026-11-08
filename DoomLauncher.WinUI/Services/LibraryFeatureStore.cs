using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DoomLauncher.WinUI.Services;

public sealed record PersonalModData(int GameFileId, int? Rating, int? QueuePosition, bool AutoDequeue,
    IReadOnlySet<string> CompletedMaps)
{
    public static PersonalModData Empty(int id) => new(id, null, null, false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}
public sealed record BulkLibraryEdit(int? SourcePortId, bool ChangeSourcePort, int? IwadId, bool ChangeIwad,
    bool? Finished, IReadOnlySet<int> AddCollections, IReadOnlySet<int> RemoveCollections);
public sealed record MetadataSnapshot(string? Title, string? Author, string? Description, string? ReleaseDate,
    double? Rating, string? Map, int MapCount, int? IdGamesId, string? ArchiveDirectory);
public sealed record MetadataChange(long Id, int GameFileId, string AtUtc, string Source,
    MetadataSnapshot Before, MetadataSnapshot After, bool Undone);

public sealed class LibraryFeatureStore(IDoomLauncherDatabaseLocator locator)
{
    public static IReadOnlyList<string> MetadataFields { get; } = Array.AsReadOnly(new[] { "Title", "Author", "Description", "ReleaseDate", "Rating", "Maps" });
    public string DatabasePath => locator.FindDatabase();

    internal static async Task CreateTablesAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS WinUI_PersonalMods (
              GameFileID INTEGER NOT NULL PRIMARY KEY REFERENCES GameFiles(GameFileID) ON DELETE CASCADE,
              PersonalRating INTEGER NULL CHECK(PersonalRating BETWEEN 1 AND 5),
              QueuePosition INTEGER NULL CHECK(QueuePosition>=0), AutoDequeue INTEGER NOT NULL DEFAULT 0,
              UpdatedUtc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS WinUI_MapProgress (
              GameFileID INTEGER NOT NULL REFERENCES GameFiles(GameFileID) ON DELETE CASCADE,
              MapName TEXT NOT NULL COLLATE NOCASE, Finished INTEGER NOT NULL DEFAULT 0,
              ValidationOrigin TEXT NOT NULL DEFAULT '',
              UpdatedUtc TEXT NOT NULL, PRIMARY KEY(GameFileID,MapName));
            CREATE TABLE IF NOT EXISTS WinUI_MetadataLocks (
              GameFileID INTEGER NOT NULL REFERENCES GameFiles(GameFileID) ON DELETE CASCADE,
              Field TEXT NOT NULL, PRIMARY KEY(GameFileID,Field));
            CREATE TABLE IF NOT EXISTS WinUI_MetadataHistory (
              HistoryID INTEGER PRIMARY KEY AUTOINCREMENT,
              GameFileID INTEGER NOT NULL REFERENCES GameFiles(GameFileID) ON DELETE CASCADE,
              AtUtc TEXT NOT NULL, Source TEXT NOT NULL, BeforeJson TEXT NOT NULL, AfterJson TEXT NOT NULL,
              UndoneAt TEXT NULL);
            CREATE INDEX IF NOT EXISTS IX_WinUI_MetadataHistory_Game ON WinUI_MetadataHistory(GameFileID,HistoryID DESC);
            """;
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = "PRAGMA table_info(WinUI_MapProgress);";
        var hasOrigin = false;
        await using (var columns = await command.ExecuteReaderAsync(token))
            while (await columns.ReadAsync(token)) hasOrigin |= columns.GetString(1) == "ValidationOrigin";
        if (!hasOrigin)
        {
            command.CommandText = "ALTER TABLE WinUI_MapProgress ADD COLUMN ValidationOrigin TEXT NOT NULL DEFAULT '';";
            await command.ExecuteNonQueryAsync(token);
        }
    }

    internal async Task<SqliteConnection> OpenAsync(CancellationToken token = default)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, ForeignKeys = true, DefaultTimeout = 5 }.ToString());
        try { await connection.OpenAsync(token); await WinUiDatabaseSchema.EnsureAsync(connection, token); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task<IReadOnlyDictionary<int, PersonalModData>> LoadAsync(CancellationToken token = default)
    {
        await using var db = await OpenAsync(token);
        var progress = new Dictionary<int, HashSet<string>>();
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT GameFileID,MapName FROM WinUI_MapProgress WHERE Finished=1 AND ValidationOrigin='gzdoom-visited-transition-v1';";
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                if (!progress.TryGetValue(reader.GetInt32(0), out var maps)) progress[reader.GetInt32(0)] = maps = new(StringComparer.OrdinalIgnoreCase);
                maps.Add(reader.GetString(1));
            }
        var result = progress.ToDictionary(pair => pair.Key, pair => PersonalModData.Empty(pair.Key) with { CompletedMaps = pair.Value });
        command.CommandText = "SELECT GameFileID,PersonalRating,QueuePosition,AutoDequeue FROM WinUI_PersonalMods;";
        await using var rows = await command.ExecuteReaderAsync(token);
        while (await rows.ReadAsync(token))
        {
            var id = rows.GetInt32(0);
            result[id] = new(id, rows.IsDBNull(1) ? null : rows.GetInt32(1), rows.IsDBNull(2) ? null : rows.GetInt32(2),
                rows.GetInt32(3) != 0, progress.GetValueOrDefault(id) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
        return result;
    }

    public async Task SetRatingAsync(int id, int? rating, CancellationToken token = default)
    {
        if (rating.HasValue && rating is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(rating));
        await using var db = await OpenAsync(token); await using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO WinUI_PersonalMods(GameFileID,PersonalRating,UpdatedUtc) VALUES($id,$rating,$at) ON CONFLICT(GameFileID) DO UPDATE SET PersonalRating=$rating,UpdatedUtc=$at;";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$rating", (object?)rating ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); await command.ExecuteNonQueryAsync(token);
    }

    public async Task SetQueuedAsync(int id, bool queued, bool autoDequeue, CancellationToken token = default)
    {
        await using var db = await OpenAsync(token); await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO WinUI_PersonalMods(GameFileID,QueuePosition,AutoDequeue,UpdatedUtc)
            VALUES($id,CASE WHEN $queued=1 THEN (SELECT COALESCE(MAX(QueuePosition),-1)+1 FROM WinUI_PersonalMods) ELSE NULL END,$auto,$at)
            ON CONFLICT(GameFileID) DO UPDATE SET QueuePosition=CASE WHEN $queued=0 THEN NULL ELSE COALESCE(QueuePosition,(SELECT COALESCE(MAX(QueuePosition),-1)+1 FROM WinUI_PersonalMods)) END,
              AutoDequeue=$auto,UpdatedUtc=$at;
            """;
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$queued", queued ? 1 : 0);
        command.Parameters.AddWithValue("$auto", autoDequeue ? 1 : 0); command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task ReorderQueueAsync(IReadOnlyList<int> ids, CancellationToken token = default)
    {
        if (ids.Distinct().Count() != ids.Count) throw new ArgumentException("Repeated queue entry.");
        await using var db = await OpenAsync(token); await using var tx = (SqliteTransaction)await db.BeginTransactionAsync(token);
        var actual = new HashSet<int>(); await using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT GameFileID FROM WinUI_PersonalMods WHERE QueuePosition IS NOT NULL;";
        await using (var rows = await command.ExecuteReaderAsync(token)) while (await rows.ReadAsync(token)) actual.Add(rows.GetInt32(0));
        if (!actual.SetEquals(ids)) throw new InvalidOperationException("The queue changed; reopen it before reordering.");
        command.CommandText = "UPDATE WinUI_PersonalMods SET QueuePosition=$position,UpdatedUtc=$at WHERE GameFileID=$id;";
        command.Parameters.AddWithValue("$id", 0); command.Parameters.AddWithValue("$position", 0); command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        for (var index = 0; index < ids.Count; index++)
        { command.Parameters["$id"].Value = ids[index]; command.Parameters["$position"].Value = index; await command.ExecuteNonQueryAsync(token); }
        await tx.CommitAsync(token);
    }

    internal async Task RecordValidatedMapsAsync(int id, IReadOnlySet<string> maps, CancellationToken token = default)
    {
        if (maps.Count == 0) return;
        if (maps.Any(map => map.Length is 0 or > 64 || !map.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')))
            throw new ArgumentException("Invalid validated map name.");
        await using var db = await OpenAsync(token); await using var tx = (SqliteTransaction)await db.BeginTransactionAsync(token);
        await using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "INSERT INTO WinUI_MapProgress(GameFileID,MapName,Finished,ValidationOrigin,UpdatedUtc) VALUES($id,$map,1,'gzdoom-visited-transition-v1',$at) ON CONFLICT(GameFileID,MapName) DO UPDATE SET Finished=1,ValidationOrigin=excluded.ValidationOrigin,UpdatedUtc=excluded.UpdatedUtc;";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$map", "");
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        foreach (var map in maps) { command.Parameters["$map"].Value = map.ToUpperInvariant(); await command.ExecuteNonQueryAsync(token); }
        await tx.CommitAsync(token);
    }

    public async Task<IReadOnlySet<string>> LoadLocksAsync(int id, CancellationToken token = default)
    {
        await using var db = await OpenAsync(token); await using var command = db.CreateCommand();
        command.CommandText = "SELECT Field FROM WinUI_MetadataLocks WHERE GameFileID=$id;"; command.Parameters.AddWithValue("$id", id);
        var result = new HashSet<string>(StringComparer.Ordinal); await using var rows = await command.ExecuteReaderAsync(token);
        while (await rows.ReadAsync(token)) result.Add(rows.GetString(0)); return result;
    }

    public async Task SetLocksAsync(int id, IReadOnlySet<string> fields, CancellationToken token = default)
    {
        if (fields.Any(field => !MetadataFields.Contains(field))) throw new ArgumentException("Unknown metadata field.");
        await using var db = await OpenAsync(token); await using var tx = (SqliteTransaction)await db.BeginTransactionAsync(token);
        await using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "DELETE FROM WinUI_MetadataLocks WHERE GameFileID=$id;"; command.Parameters.AddWithValue("$id", id); await command.ExecuteNonQueryAsync(token);
        command.CommandText = "INSERT INTO WinUI_MetadataLocks(GameFileID,Field) VALUES($id,$field);"; command.Parameters.AddWithValue("$field", "");
        foreach (var field in fields) { command.Parameters["$field"].Value = field; await command.ExecuteNonQueryAsync(token); }
        await tx.CommitAsync(token);
    }

    public async Task ApplyBulkEditAsync(IReadOnlySet<int> ids, BulkLibraryEdit edit, CancellationToken token = default)
    {
        if (ids.Count == 0) return;
        if (edit.AddCollections.Overlaps(edit.RemoveCollections)) throw new ArgumentException("A collection cannot be both added and removed.");
        await using var db = await OpenAsync(token); await using var tx = (SqliteTransaction)await db.BeginTransactionAsync(token);
        async Task RequireAsync(string table, string column, int value)
        {
            await using var check = db.CreateCommand(); check.Transaction = tx;
            check.CommandText = $"SELECT 1 FROM {table} WHERE {column}=$id;"; check.Parameters.AddWithValue("$id", value);
            if (await check.ExecuteScalarAsync(token) is null) throw new InvalidOperationException("A selected definition was removed; no changes applied.");
        }
        if (edit.ChangeSourcePort && edit.SourcePortId is { } portId) await RequireAsync("SourcePorts", "SourcePortID", portId);
        if (edit.ChangeIwad && edit.IwadId is { } iwadId) await RequireAsync("IWads", "IWadID", iwadId);
        foreach (var tag in edit.AddCollections.Concat(edit.RemoveCollections)) await RequireAsync("Tags", "TagID", tag);
        foreach (var id in ids)
        {
            await using var command = db.CreateCommand(); command.Transaction = tx;
            command.CommandText = "UPDATE GameFiles SET SourcePortID=CASE WHEN $sp=1 THEN $port ELSE SourcePortID END,IWadID=CASE WHEN $si=1 THEN $iwad ELSE IWadID END,IsSyncNeeded=1 WHERE GameFileID=$id;";
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$sp", edit.ChangeSourcePort ? 1 : 0); command.Parameters.AddWithValue("$si", edit.ChangeIwad ? 1 : 0);
            command.Parameters.AddWithValue("$port", (object?)edit.SourcePortId ?? DBNull.Value); command.Parameters.AddWithValue("$iwad", (object?)edit.IwadId ?? DBNull.Value);
            if (await command.ExecuteNonQueryAsync(token) != 1) throw new InvalidOperationException("An entry was removed; no changes applied.");
            command.Parameters.Clear(); command.Parameters.AddWithValue("$id", id);
            if (edit.Finished.HasValue)
            {
                command.CommandText = "INSERT INTO WinUI_GameState(GameFileID,Finished) VALUES($id,$done) ON CONFLICT(GameFileID) DO UPDATE SET Finished=$done; UPDATE WinUI_PersonalMods SET QueuePosition=NULL,UpdatedUtc=$at WHERE GameFileID=$id AND AutoDequeue=1 AND $done=1;";
                command.Parameters.AddWithValue("$done", edit.Finished.Value ? 1 : 0); command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); await command.ExecuteNonQueryAsync(token);
            }
            command.Parameters.Clear(); command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$tag", 0);
            foreach (var tag in edit.RemoveCollections)
            { command.CommandText = "DELETE FROM TagMapping WHERE FileID=$id AND TagID=$tag;"; command.Parameters["$tag"].Value = tag; await command.ExecuteNonQueryAsync(token); }
            foreach (var tag in edit.AddCollections)
            {
                command.CommandText = "INSERT INTO TagMapping(FileID,TagID) SELECT $id,$tag WHERE EXISTS(SELECT 1 FROM Tags WHERE TagID=$tag) AND NOT EXISTS(SELECT 1 FROM TagMapping WHERE FileID=$id AND TagID=$tag);";
                command.Parameters["$tag"].Value = tag; await command.ExecuteNonQueryAsync(token);
            }
        }
        await tx.CommitAsync(token);
    }

    public static async Task<MetadataSnapshot> CaptureMetadataAsync(SqliteConnection db, SqliteTransaction? tx, int id, CancellationToken token)
    {
        await using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT Title,Author,Description,ReleaseDate,Rating,Map,COALESCE(MapCount,0),m.IdGamesID,m.ArchiveDirectory FROM GameFiles g LEFT JOIN WinUI_IdGamesMetadata m ON m.GameFileID=g.GameFileID WHERE g.GameFileID=$id;";
        command.Parameters.AddWithValue("$id", id); await using var row = await command.ExecuteReaderAsync(token);
        if (!await row.ReadAsync(token)) throw new InvalidOperationException("Library entry no longer exists.");
        string? Text(int n) => row.IsDBNull(n) ? null : Convert.ToString(row.GetValue(n), CultureInfo.InvariantCulture);
        return new(Text(0), Text(1), Text(2), Text(3), row.IsDBNull(4) ? null : row.GetDouble(4), Text(5), row.GetInt32(6), row.IsDBNull(7) ? null : row.GetInt32(7), Text(8));
    }

    public static async Task RecordMetadataAsync(SqliteConnection db, SqliteTransaction? tx, int id, string source, MetadataSnapshot before, CancellationToken token)
    {
        var after = await CaptureMetadataAsync(db, tx, id, token); if (before == after) return;
        await using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "INSERT INTO WinUI_MetadataHistory(GameFileID,AtUtc,Source,BeforeJson,AfterJson) VALUES($id,$at,$source,$before,$after);";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$source", source); command.Parameters.AddWithValue("$before", JsonSerializer.Serialize(before)); command.Parameters.AddWithValue("$after", JsonSerializer.Serialize(after));
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<IReadOnlyList<MetadataChange>> LoadHistoryAsync(int id, CancellationToken token = default)
    {
        await using var db = await OpenAsync(token); await using var command = db.CreateCommand();
        command.CommandText = "SELECT HistoryID,AtUtc,Source,BeforeJson,AfterJson,UndoneAt FROM WinUI_MetadataHistory WHERE GameFileID=$id ORDER BY HistoryID DESC LIMIT 200;";
        command.Parameters.AddWithValue("$id", id); var result = new List<MetadataChange>(); await using var rows = await command.ExecuteReaderAsync(token);
        while (await rows.ReadAsync(token)) result.Add(new(rows.GetInt64(0), id, rows.GetString(1), rows.GetString(2), JsonSerializer.Deserialize<MetadataSnapshot>(rows.GetString(3))!, JsonSerializer.Deserialize<MetadataSnapshot>(rows.GetString(4))!, !rows.IsDBNull(5)));
        return result;
    }

    public async Task UndoMetadataAsync(long historyId, CancellationToken token = default)
    {
        await using var db = await OpenAsync(token); await using var tx = (SqliteTransaction)await db.BeginTransactionAsync(token);
        await using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT GameFileID,BeforeJson,AfterJson FROM WinUI_MetadataHistory WHERE HistoryID=$history AND UndoneAt IS NULL;"; command.Parameters.AddWithValue("$history", historyId);
        int id; MetadataSnapshot before, after;
        await using (var rows = await command.ExecuteReaderAsync(token))
        { if (!await rows.ReadAsync(token)) throw new InvalidOperationException("Change is missing or already undone."); id = rows.GetInt32(0); before = JsonSerializer.Deserialize<MetadataSnapshot>(rows.GetString(1))!; after = JsonSerializer.Deserialize<MetadataSnapshot>(rows.GetString(2))!; }
        if (await CaptureMetadataAsync(db, tx, id, token) != after) throw new InvalidOperationException("Metadata changed since this revision. Undo newer changes first.");
        command.CommandText = "UPDATE GameFiles SET Title=$title,Author=$author,Description=$description,ReleaseDate=$release,Rating=$rating,Map=$map,MapCount=$count,IsSyncNeeded=1 WHERE GameFileID=$id; DELETE FROM WinUI_IdGamesMetadata WHERE GameFileID=$id;";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$title", (object?)before.Title ?? DBNull.Value); command.Parameters.AddWithValue("$author", (object?)before.Author ?? DBNull.Value);
        command.Parameters.AddWithValue("$description", (object?)before.Description ?? DBNull.Value); command.Parameters.AddWithValue("$release", (object?)before.ReleaseDate ?? DBNull.Value);
        command.Parameters.AddWithValue("$rating", (object?)before.Rating ?? DBNull.Value); command.Parameters.AddWithValue("$map", (object?)before.Map ?? DBNull.Value); command.Parameters.AddWithValue("$count", before.MapCount); await command.ExecuteNonQueryAsync(token);
        if (before.IdGamesId.HasValue)
        {
            command.CommandText = "INSERT INTO WinUI_IdGamesMetadata(GameFileID,IdGamesID,ArchiveDirectory,ScrapedAt) VALUES($id,$link,$directory,$at);";
            command.Parameters.AddWithValue("$link", before.IdGamesId.Value); command.Parameters.AddWithValue("$directory", (object?)before.ArchiveDirectory ?? DBNull.Value); command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); await command.ExecuteNonQueryAsync(token);
        }
        command.CommandText = "UPDATE WinUI_MetadataHistory SET UndoneAt=$undone WHERE HistoryID=$history;"; command.Parameters.AddWithValue("$undone", DateTimeOffset.UtcNow.ToString("O")); await command.ExecuteNonQueryAsync(token); await tx.CommitAsync(token);
    }
}
