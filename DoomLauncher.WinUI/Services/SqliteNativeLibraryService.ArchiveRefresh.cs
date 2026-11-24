using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DoomLauncher.WinUI.Services;

public sealed record LocalArchiveRefreshResult(int MapCount, bool ArtworkImported);

public sealed partial class SqliteNativeLibraryService
{
    public async Task<LocalArchiveRefreshResult> RefreshGameFromLocalArchiveAsync(
        int gameFileId, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await GetEnabledMetadataFieldsAsync(connection, gameFileId, null, cancellationToken);
        var path = await ResolveManagedGameFileAsync(gameFileId, cancellationToken);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("The linked mod archive is unavailable.", path);
        progress?.Report(0.1);
        using var payload = await Task.Run(() => ArchivePayload.OpenAsync(path, cancellationToken), cancellationToken);
        progress?.Report(0.3);
        var inspection = await ArchiveInspection.ReadAsync(payload.Path, cancellationToken);
        progress?.Report(0.6);
        int mapCount;
        await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken))
        {
            var enabled = await GetEnabledMetadataFieldsAsync(connection, gameFileId, null, cancellationToken, transaction);
            var before = await LibraryFeatureStore.CaptureMetadataAsync(connection, transaction, gameFileId, cancellationToken);
            string? Text(string field, string value, string? previous) =>
                enabled.Contains(field) && !string.IsNullOrWhiteSpace(value) ? value : previous;
            var after = before with
            {
                Title = Text("Title", inspection.Text.Title, before.Title),
                Author = Text("Author", inspection.Text.Author, before.Author),
                Description = Text("Description", inspection.Text.Description, before.Description),
                ReleaseDate = enabled.Contains("ReleaseDate") && inspection.Text.ReleaseDate.HasValue
                    ? inspection.Text.ReleaseDate.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : before.ReleaseDate,
                Map = enabled.Contains("Maps") ? inspection.Maps.Count == 0 ? null : string.Join(", ", inspection.Maps) : before.Map,
                MapCount = enabled.Contains("Maps") ? inspection.Maps.Count : before.MapCount,
            };
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE GameFiles SET Title=$title,Author=$author,Description=$description,
                    ReleaseDate=$date,Map=$maps,MapCount=$count,IsSyncNeeded=1
                WHERE GameFileID=$id AND NOT EXISTS(SELECT 1 FROM IWads WHERE GameFileID=$id);
                """;
            update.Parameters.AddWithValue("$title", DbValue(after.Title));
            update.Parameters.AddWithValue("$author", DbValue(after.Author));
            update.Parameters.AddWithValue("$description", DbValue(after.Description));
            update.Parameters.AddWithValue("$date", DbValue(after.ReleaseDate));
            update.Parameters.AddWithValue("$maps", DbValue(after.Map));
            update.Parameters.AddWithValue("$count", after.MapCount);
            update.Parameters.AddWithValue("$id", gameFileId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("The library entry is missing or protected as an IWAD.");
            await LibraryFeatureStore.RecordMetadataAsync(connection, transaction, gameFileId,
                "local-archive:" + Path.GetFileName(path), before, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            mapCount = after.MapCount;
        }
        progress?.Report(0.8);
        var artwork = false;
        try { artwork = await TryImportTitlePicAsync(gameFileId, payload.Path, cancellationToken); }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException)
        { System.Diagnostics.Trace.TraceWarning("Optional local artwork: {0}", error); }
        progress?.Report(1);
        return new(mapCount, artwork);
    }

    private static async Task<HashSet<string>> GetEnabledMetadataFieldsAsync(SqliteConnection connection,
        int gameFileId, IReadOnlySet<string>? requested, CancellationToken token, SqliteTransaction? transaction = null)
    {
        await using (var guard = connection.CreateCommand())
        {
            guard.Transaction = transaction;
            guard.CommandText = "SELECT EXISTS(SELECT 1 FROM IWads WHERE GameFileID=$id);";
            guard.Parameters.AddWithValue("$id", gameFileId);
            if (Convert.ToInt32(await guard.ExecuteScalarAsync(token)) != 0)
                throw new InvalidOperationException("IWADs are excluded from mod metadata updates.");
        }
        var enabled = new HashSet<string>(requested ?? LibraryFeatureStore.MetadataFields.ToHashSet(), StringComparer.Ordinal);
        if (enabled.Any(field => !LibraryFeatureStore.MetadataFields.Contains(field))) throw new ArgumentException("Unknown metadata field.");
        await using var locks = connection.CreateCommand();
        locks.Transaction = transaction;
        locks.CommandText = "SELECT Field FROM WinUI_MetadataLocks WHERE GameFileID=$id;";
        locks.Parameters.AddWithValue("$id", gameFileId);
        await using var rows = await locks.ExecuteReaderAsync(token);
        while (await rows.ReadAsync(token)) enabled.Remove(rows.GetString(0));
        return enabled;
    }
}
