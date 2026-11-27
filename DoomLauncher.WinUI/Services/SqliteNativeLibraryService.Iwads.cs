using System.Globalization;
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using SharpCompress.Archives;

namespace DoomLauncher.WinUI.Services;

public sealed partial class SqliteNativeLibraryService
{
    private static readonly SemaphoreSlim IwadImportGate = new(1, 1);
    // Explicit IWAD import: ordinary Mod imports keep their existing conflict policy.
    public async Task<NativeImportResult> ImportIwadAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        await IwadImportGate.WaitAsync(cancellationToken);
        try { return await ImportIwadCoreAsync(sourcePath, cancellationToken); }
        finally { IwadImportGate.Release(); }
    }

    private async Task<NativeImportResult> ImportIwadCoreAsync(string sourcePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        sourcePath = Path.GetFullPath(sourcePath);
        var candidates = await IwadVersionDetector.ScanArchiveAsync(sourcePath, cancellationToken);
        if (candidates.Count != 1)
            throw new InvalidDataException("Für den IWAD-Import muss das Archiv genau eine Basis-IWAD enthalten. Bitte die IWADs eines Sammelarchivs getrennt verpacken.");
        var candidate = candidates[0];
        var database = databaseLocator.FindDatabase();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var configuration = await LoadConfigurationAsync(connection, cancellationToken);
        var gameRoot = ResolveGameFileDirectory(database, configuration);
        var definitions = await LoadLauncherDefinitionsAsync(cancellationToken);
        foreach (var definition in definitions.Iwads)
        {
            if (definition.Md5.Length > 0 && !definition.Md5.Equals(candidate.Md5, StringComparison.OrdinalIgnoreCase)) continue;
            var existingPath = Path.GetFullPath(Path.IsPathFullyQualified(definition.ArchiveFileName)
                ? definition.ArchiveFileName : Path.Combine(gameRoot, definition.ArchiveFileName));
            if (!File.Exists(existingPath)) continue;
            var actual = await IwadVersionDetector.DetectAsync(existingPath, definition.InternalFileName, cancellationToken);
            if (actual.Md5 != candidate.Md5 || actual.FileSize != candidate.FileSize) continue;
            await using var find = connection.CreateCommand();
            find.CommandText = "SELECT GameFileID FROM IWads WHERE IWadID=$id";
            find.Parameters.AddWithValue("$id", definition.IwadId!.Value);
            var id = Convert.ToInt32(await find.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            return new(id, definition.ArchiveFileName, existingPath, WasSkipped: true);
        }

        var directory = Path.Combine(gameRoot, "GameWads"); Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".iwad-{Guid.NewGuid():N}.partial");
        string? ownedDestination = null; var committed = false;
        try
        {
            if (Path.GetExtension(sourcePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
                await input.CopyToAsync(output, cancellationToken);
            }
            else
            {
                using var zip = ZipFile.Open(temporary, ZipArchiveMode.Create);
                if (Path.GetExtension(sourcePath).Equals(".wad", StringComparison.OrdinalIgnoreCase))
                {
                    await using var input = File.OpenRead(sourcePath);
                    await using var output = zip.CreateEntry(candidate.InternalFileName, CompressionLevel.Fastest).Open();
                    await input.CopyToAsync(output, cancellationToken);
                }
                else
                {
                    using var archive = ArchiveFactory.OpenArchive(sourcePath);
                    foreach (var entry in archive.Entries.Where(item => !item.IsDirectory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await using var input = entry.OpenEntryStream();
                        await using var output = zip.CreateEntry((entry.Key ?? string.Empty).Replace('\\', '/'), CompressionLevel.Fastest).Open();
                        await input.CopyToAsync(output, cancellationToken);
                    }
                }
            }
            // Detect source changes during copying before committing any database row.
            var copied = await IwadVersionDetector.ScanArchiveAsync(temporary, cancellationToken);
            if (copied.Count != 1 || copied[0].Md5 != candidate.Md5 || copied[0].FileSize != candidate.FileSize)
                throw new IOException("Die IWAD-Datei wurde während des Imports geändert.");
            var maps = await MapNameExtractor.ExtractAsync(temporary, cancellationToken);
            var stem = SafeIwadName(Path.GetFileNameWithoutExtension(candidate.InternalFileName));
            var version = SafeIwadName(candidate.Version.Length > 0 ? candidate.Version : "unknown");
            var prefix = $"{stem}_v{version}_{candidate.Md5[..8]}";
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            string reference, destination; var suffix = 0;
            while (true)
            {
                var name = prefix + (suffix == 0 ? "" : "_" + suffix) + ".zip";
                reference = Path.Combine("GameWads", name); destination = Path.Combine(gameRoot, reference);
                await using var exists = connection.CreateCommand(); exists.Transaction = transaction;
                exists.CommandText = "SELECT COUNT(*) FROM GameFiles WHERE FileName=$file COLLATE NOCASE";
                exists.Parameters.AddWithValue("$file", reference);
                if (!File.Exists(destination) && Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0) break;
                suffix++;
            }
            File.Move(temporary, destination, overwrite: false); ownedDestination = destination;
            await using var insert = connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO GameFiles (FileName,Title,Downloaded,Map,MapCount,MinutesPlayed,IsSyncNeeded)
                VALUES ($file,$title,$downloaded,$maps,$count,0,1);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$file", reference);
            insert.Parameters.AddWithValue("$title", candidate.SuggestedName);
            insert.Parameters.AddWithValue("$downloaded", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$maps", maps.Count == 0 ? DBNull.Value : string.Join(", ", maps));
            insert.Parameters.AddWithValue("$count", maps.Count);
            var gameId = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            await using var iwad = connection.CreateCommand(); iwad.Transaction = transaction;
            iwad.CommandText = "INSERT INTO IWads (Name,FileName,GameFileID) VALUES ($name,$internal,$game); SELECT last_insert_rowid();";
            iwad.Parameters.AddWithValue("$name", candidate.SuggestedName);
            iwad.Parameters.AddWithValue("$internal", candidate.InternalFileName);
            iwad.Parameters.AddWithValue("$game", gameId);
            var iwadId = Convert.ToInt32(await iwad.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            if (candidate.InternalFileName.Equals("HEXDD.WAD", StringComparison.OrdinalIgnoreCase)
                && !definitions.Iwads.Any(item => item.InternalFileName.Equals("HEXEN.WAD", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Deathkings benötigt zuerst eine registrierte HEXEN.WAD.");
            await WriteIwadMetadataAsync(connection, transaction, iwadId,
                new(iwadId, candidate.SuggestedName, reference, candidate.InternalFileName, candidate.Version, candidate.Md5, candidate.FileSize, candidate.CatalogLabel), cancellationToken);
            await transaction.CommitAsync(cancellationToken); committed = true;
            // Optional artwork must not turn a committed import into a reported failure.
            try { await TryImportTitlePicAsync(gameId, destination, candidate.InternalFileName, CancellationToken.None); }
            catch (Exception error) { System.Diagnostics.Trace.TraceWarning("IWAD artwork: {0}", error); }
            return new(gameId, reference, destination);
        }
        finally
        {
            if (!committed && ownedDestination is not null) File.Delete(ownedDestination);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string SafeIwadName(string value) => new(value.ToLowerInvariant()
        .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '_').ToArray());
}
