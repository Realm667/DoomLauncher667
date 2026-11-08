using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DoomLauncher.WinUI.Services;

public sealed record BackupFile(string Path, long Length, string Sha256);
public sealed record InstallationBackupManifest(int Format, string LauncherVersion, DateTimeOffset CreatedUtc,
    bool IncludesMods, IReadOnlyList<BackupFile> Files, string SourceRoot = "");

public sealed class InstallationBackupService
{
    private const string ManifestName = "dl667-backup.json";

    public async Task CreateAsync(string root, string destination, bool includeMods,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        root = Path.GetFullPath(root);
        destination = Path.GetFullPath(destination);
        if (IsWithin(destination, root)) throw new InvalidOperationException("Save the backup outside the installation folder.");
        var files = EnumerateFiles(root, includeMods).ToArray();
        foreach (var required in new[] { "DoomLauncher.sqlite", "DoomLauncher667.exe", "WinUI/DoomLauncher.WinUI.exe" })
            if (!files.Any(file => Relative(root, file).Equals(required, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Installation file missing: {required}");
        var staging = Path.Combine(Path.GetTempPath(), "DoomLauncher-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var temporaryArchive = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            var snapshot = Path.Combine(staging, "DoomLauncher.sqlite");
            await using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(root, "DoomLauncher.sqlite"), Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString()))
            await using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshot, Pooling = false }.ToString()))
            {
                await source.OpenAsync(cancellationToken); await target.OpenAsync(cancellationToken);
                source.BackupDatabase(target);
            }
            var records = new List<BackupFile>();
            var total = Math.Max(1, files.Sum(file => Relative(root, file).Equals("DoomLauncher.sqlite", StringComparison.OrdinalIgnoreCase)
                ? new FileInfo(snapshot).Length : new FileInfo(file).Length));
            long copied = 0;
            using (var archive = ZipFile.Open(temporaryArchive, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Relative(root, file);
                    var actual = relative.Equals("DoomLauncher.sqlite", StringComparison.OrdinalIgnoreCase) ? snapshot : file;
                    var before = new FileInfo(actual);
                    var length = before.Length; var modified = before.LastWriteTimeUtc;
                    var entry = archive.CreateEntry(relative, CompressionLevel.Fastest);
                    await using var input = new FileStream(actual, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                    await using var output = entry.Open();
                    var digest = await TransferAsync(input, output, cancellationToken, count => progress?.Report(80d * (copied + count) / total));
                    before.Refresh();
                    if (before.Length != length || before.LastWriteTimeUtc != modified)
                        throw new IOException($"File changed during backup: {relative}. Please retry when the installation is idle.");
                    records.Add(new(relative, length, digest));
                    copied += length;
                }
                await using var manifest = archive.CreateEntry(ManifestName).Open();
                await JsonSerializer.SerializeAsync(manifest,
                    new InstallationBackupManifest(1, AppVersion.Current, DateTimeOffset.UtcNow, includeMods, records, root), cancellationToken: cancellationToken);
            }
            using (var archive = ZipFile.OpenRead(temporaryArchive))
                await VerifyAsync(archive, await ReadManifestAsync(archive, cancellationToken),
                    new ForwardProgress(value => progress?.Report(80 + value * .2)), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryArchive, destination, overwrite: true);
            progress?.Report(100);
        }
        finally
        {
            try { if (File.Exists(temporaryArchive)) File.Delete(temporaryArchive); } catch (IOException) { }
            Directory.Delete(staging, recursive: true);
        }
    }

    public async Task<InstallationBackupManifest> RestoreAsync(string archivePath, string destination,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        destination = Path.GetFullPath(destination);
        if (!Directory.Exists(destination) || Directory.EnumerateFileSystemEntries(destination).Any())
            throw new InvalidOperationException("Choose an existing empty folder for restoration.");
        if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The restore folder must not be a symbolic link.");
        var created = new List<string>();
        var createdDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var manifest = await ReadManifestAsync(archive, cancellationToken);
            ValidateEntries(archive, manifest);
            var total = Math.Max(1, manifest.Files.Sum(file => file.Length));
            long restored = 0;
            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = SafeTarget(destination, file.Path);
                var parent = Path.GetDirectoryName(target)!;
                var missing = new Stack<string>();
                while (!Directory.Exists(parent)) { missing.Push(parent); parent = Path.GetDirectoryName(parent)!; }
                while (missing.TryPop(out var directory)) { Directory.CreateDirectory(directory); createdDirectories.Add(directory); }
                await using var input = archive.GetEntry(file.Path)!.Open();
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
                created.Add(target);
                var hash = await TransferAsync(input, output, cancellationToken, count => progress?.Report(98d * (restored + count) / total));
                if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase) || output.Length != file.Length)
                    throw new InvalidDataException($"Backup checksum mismatch: {file.Path}");
                restored += file.Length;
            }
            await RebasePathsAsync(destination, manifest.SourceRoot, cancellationToken);
            await using var database = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(destination, "DoomLauncher.sqlite"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await database.OpenAsync(cancellationToken);
            await using var check = database.CreateCommand(); check.CommandText = "PRAGMA quick_check;";
            if (!string.Equals(Convert.ToString(await check.ExecuteScalarAsync(cancellationToken)), "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The restored database did not pass its integrity check.");
            progress?.Report(100);
            return manifest;
        }
        catch
        {
            // Only files this operation created in the initially empty target can be removed.
            foreach (var file in created.AsEnumerable().Reverse()) if (File.Exists(file)) File.Delete(file);
            foreach (var folder in createdDirectories.OrderByDescending(path => path.Length))
                if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            throw;
        }
    }

    private static async Task<InstallationBackupManifest> ReadManifestAsync(ZipArchive archive, CancellationToken token)
    {
        var entry = archive.GetEntry(ManifestName) ?? throw new InvalidDataException("Not a Doom Launcher 667 installation backup.");
        if (entry.Length > 32 * 1024 * 1024) throw new InvalidDataException("Backup manifest is too large.");
        await using var input = entry.Open();
        return await JsonSerializer.DeserializeAsync<InstallationBackupManifest>(input, cancellationToken: token)
            ?? throw new InvalidDataException("Backup manifest is empty.");
    }

    private static void ValidateEntries(ZipArchive archive, InstallationBackupManifest manifest)
    {
        if (manifest.Format != 1 || manifest.Files is null) throw new InvalidDataException("Unsupported backup format.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (!names.Add(file.Path) || file.Length < 0 || file.Sha256?.Length != 64)
                throw new InvalidDataException("Invalid or repeated backup file.");
            SafeTarget(Path.GetTempPath(), file.Path);
            var entry = archive.GetEntry(file.Path) ?? throw new InvalidDataException($"Missing backup file: {file.Path}");
            if (entry.Length != file.Length) throw new InvalidDataException($"Backup length mismatch: {file.Path}");
        }
        foreach (var entry in archive.Entries)
            if (!entry.FullName.Equals(ManifestName, StringComparison.Ordinal) && !names.Contains(entry.FullName))
                throw new InvalidDataException("Unlisted entry in backup.");
        if (archive.Entries.Count != names.Count + 1) throw new InvalidDataException("Repeated ZIP entry in backup.");
        foreach (var required in new[] { "DoomLauncher.sqlite", "DoomLauncher667.exe", "WinUI/DoomLauncher.WinUI.exe" })
            if (!names.Contains(required)) throw new InvalidDataException($"Incomplete installation backup: {required}");
    }

    private static async Task VerifyAsync(ZipArchive archive, InstallationBackupManifest manifest, IProgress<double>? progress, CancellationToken token)
    {
        ValidateEntries(archive, manifest);
        var total = Math.Max(1, manifest.Files.Sum(file => file.Length)); long verified = 0;
        foreach (var file in manifest.Files)
        {
            await using var stream = archive.GetEntry(file.Path)!.Open();
            var digest = await TransferAsync(stream, Stream.Null, token);
            if (!digest.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Backup verification failed: {file.Path}");
            verified += file.Length; progress?.Report(100d * verified / total);
        }
    }

    private static async Task<string> TransferAsync(Stream input, Stream output, CancellationToken token, Action<long>? progress = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[131072]; int read; long copied = 0;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), token);
            copied += read; progress?.Invoke(copied);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static IEnumerable<string> EnumerateFiles(string root, bool includeMods)
    {
        foreach (var item in new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            var relative = Relative(root, item.FullName);
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException($"Linked files cannot be backed up: {relative}");
            if (item is DirectoryInfo)
            {
                if (item.Name.Equals("Backups", StringComparison.OrdinalIgnoreCase) || item.Name.Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsWithin(item.FullName, Path.Combine(root, "Data", "Temp")) || (!includeMods && IsWithin(item.FullName, Path.Combine(root, "Data", "Mods")))) continue;
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException($"Copy linked folders into the installation before backing up: {relative}");
                foreach (var nested in EnumerateNested(item.FullName, root)) yield return nested;
            }
            else if (ShouldInclude(item.FullName)) yield return item.FullName;
        }
        // Nested traversal handles exclusions relative to the original root.
        IEnumerable<string> EnumerateNested(string directory, string originalRoot)
        {
            foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (IsWithin(item.FullName, Path.Combine(originalRoot, "Data", "Temp")) || (!includeMods && IsWithin(item.FullName, Path.Combine(originalRoot, "Data", "Mods")))) continue;
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException($"Linked files cannot be backed up: {Relative(originalRoot, item.FullName)}");
                if (item is DirectoryInfo) { foreach (var nested in EnumerateNested(item.FullName, originalRoot)) yield return nested; }
                else if (ShouldInclude(item.FullName)) yield return item.FullName;
            }
        }
    }

    private static bool ShouldInclude(string file) => Path.GetFileName(file) is not ("installation.lock" or ManifestName)
        && !file.EndsWith(".sqlite-wal", StringComparison.OrdinalIgnoreCase)
        && !file.EndsWith(".sqlite-shm", StringComparison.OrdinalIgnoreCase)
        && !file.EndsWith(".sqlite-journal", StringComparison.OrdinalIgnoreCase);
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private sealed class ForwardProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private static async Task RebasePathsAsync(string destination, string sourceRoot, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot)) return;
        string? Rebase(string value)
        {
            if (!Path.IsPathRooted(value)) return null;
            try { return IsWithin(value, sourceRoot) ? Path.Combine(destination, Path.GetRelativePath(sourceRoot, value)) : null; }
            catch (ArgumentException) { return null; }
        }
        await using var database = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(destination, "DoomLauncher.sqlite"), Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await database.OpenAsync(token);
        await using var transaction = (SqliteTransaction)await database.BeginTransactionAsync(token);
        foreach (var (table, column) in new[] { ("Configuration", "Value"), ("SourcePorts", "Directory"), ("GameFiles", "FileName"), ("Files", "FileName"), ("IWads", "FileName") })
        {
            await using var exists = database.CreateCommand(); exists.Transaction = transaction;
            exists.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$column;";
            exists.Parameters.AddWithValue("$column", column);
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(token)) == 0) continue;
            var changes = new List<(long, string)>();
            await using (var query = database.CreateCommand())
            {
                query.Transaction = transaction; query.CommandText = $"SELECT rowid, [{column}] FROM [{table}] WHERE [{column}] IS NOT NULL;";
                await using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                    if (Rebase(Convert.ToString(reader.GetValue(1)) ?? string.Empty) is { } value) changes.Add((reader.GetInt64(0), value));
            }
            foreach (var (id, value) in changes)
            {
                await using var update = database.CreateCommand(); update.Transaction = transaction;
                update.CommandText = $"UPDATE [{table}] SET [{column}]=$value WHERE rowid=$id;";
                update.Parameters.AddWithValue("$value", value); update.Parameters.AddWithValue("$id", id);
                await update.ExecuteNonQueryAsync(token);
            }
        }
        await transaction.CommitAsync(token);
        var stateRoot = Path.Combine(destination, "Data", "UserState");
        if (!Directory.Exists(stateRoot)) return;
        foreach (var file in Directory.EnumerateFiles(stateRoot).Where(path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".json.bak", StringComparison.OrdinalIgnoreCase)))
        {
            System.Text.Json.Nodes.JsonNode? node;
            try { node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file, token)); }
            catch (JsonException) { continue; }
            var changed = false;
            void Rewrite(System.Text.Json.Nodes.JsonNode? current)
            {
                if (current is System.Text.Json.Nodes.JsonObject obj)
                    foreach (var key in obj.Select(pair => pair.Key).ToArray())
                    {
                        if (obj[key] is System.Text.Json.Nodes.JsonValue val && val.TryGetValue<string>(out var text) && Rebase(text) is { } value)
                        { obj[key] = value; changed = true; }
                        else Rewrite(obj[key]);
                    }
                else if (current is System.Text.Json.Nodes.JsonArray array)
                    for (var index = 0; index < array.Count; index++)
                    {
                        if (array[index] is System.Text.Json.Nodes.JsonValue val && val.TryGetValue<string>(out var text) && Rebase(text) is { } value)
                        { array[index] = value; changed = true; }
                        else Rewrite(array[index]);
                    }
            }
            Rewrite(node);
            if (changed) await File.WriteAllTextAsync(file, node!.ToJsonString(), token);
        }
    }
    private static bool IsWithin(string path, string root) => Path.GetFullPath(path).TrimEnd('\\', '/').Equals(Path.GetFullPath(root).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
        || Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string SafeTarget(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':') || relative.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ')))
            throw new InvalidDataException("Unsafe path in backup.");
        foreach (var part in relative.Split('/'))
        {
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 || stem is "CON" or "PRN" or "AUX" or "NUL"
                || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9'))
                throw new InvalidDataException("Reserved filename in backup.");
        }
        var target = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsWithin(target, root) || Path.IsPathRooted(relative)) throw new InvalidDataException("Backup path escapes its target.");
        return target;
    }
}
