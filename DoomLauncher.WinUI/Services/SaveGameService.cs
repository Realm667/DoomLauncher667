using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DoomLauncher.WinUI.Services;

public sealed record SaveGameMetadata(int Format, string LibraryFile, string GameHash, string EngineName,
    string EngineVersion, string EngineHash, string LaunchFingerprint, string SavedUtc, bool Imported);
public sealed record ManagedSaveContext(string Directory, string SaveRoot, SaveGameMetadata Metadata, IReadOnlyDictionary<string, string> Before);
public sealed record SaveGameEntry(string Path, string Title, string Map, string Engine, DateTime Modified,
    SaveGameMetadata? Metadata, byte[]? Preview)
{
    public string Label => $"{Title} · {Map} · {Modified:g} · {Engine}";
}

public static class SaveGameService
{
    public static bool SupportsManagedSaves(string executable) => new[] { "gzdoom", "uzdoom", "zdoom", "lzdoom", "qzdoom", "vkdoom", "zandronum" }
        .Any(name => Path.GetFileNameWithoutExtension(executable).Equals(name, StringComparison.OrdinalIgnoreCase));

    public static async Task<string> HashAsync(string path, CancellationToken token = default)
    { await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true); return Convert.ToHexString(await SHA256.HashDataAsync(input, token)).ToLowerInvariant(); }

    public static async Task<ManagedSaveContext> PrepareAsync(string root, string libraryFile, string archivePath,
        string enginePath, string engineName, string iwadPath, IReadOnlyList<string> launchFiles, string extraParameters,
        CancellationToken token)
    {
        var gameHash = await HashAsync(archivePath, token); var engineHash = await HashAsync(enginePath, token);
        var stack = new List<string> { engineHash, await HashAsync(iwadPath, token), extraParameters };
        foreach (var file in launchFiles) stack.Add(await HashAsync(file, token));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', stack)))).ToLowerInvariant();
        var saveRoot = Path.Combine(root, "Data", "SaveGames");
        var directory = Path.Combine(saveRoot, gameHash, engineHash[..16]);
        Directory.CreateDirectory(directory);
        await BackupAsync(root, directory, token);
        var before = Snapshot(directory);
        var metadata = new SaveGameMetadata(1, libraryFile.Replace('\\', '/'), gameHash, engineName,
            FileVersionInfo.GetVersionInfo(enginePath).FileVersion ?? "", engineHash, fingerprint, DateTimeOffset.UtcNow.ToString("O"), false);
        return new(directory, saveRoot, metadata, before);
    }

    private static Dictionary<string, string> Snapshot(string directory) => Directory.EnumerateFiles(directory, "*.zds", new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint })
        .ToDictionary(path => path, path => { var file = new FileInfo(path); return $"{file.Length}:{file.LastWriteTimeUtc.Ticks}"; }, StringComparer.OrdinalIgnoreCase);

    public static async Task RecordSessionAsync(ManagedSaveContext context, CancellationToken token = default)
    {
        foreach (var (path, stamp) in Snapshot(context.Directory))
            if (!context.Before.TryGetValue(path, out var previous) || previous != stamp)
                await WriteSidecarAsync(path, context.Metadata with { SavedUtc = File.GetLastWriteTimeUtc(path).ToString("O") }, token);
    }

    public static async Task ValidateResumeAsync(ManagedSaveContext context, string savePath, bool allowUnverified, CancellationToken token)
    {
        savePath = Path.GetFullPath(savePath);
        if (!File.Exists(savePath) || Path.GetExtension(savePath).ToLowerInvariant() != ".zds") throw new FileNotFoundException("Save game is unavailable.", savePath);
        var parent = context.SaveRoot;
        if (!savePath.StartsWith(Path.GetFullPath(parent).TrimEnd('\\') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Import this save into the managed save-game folder first.");
        var metadata = await ReadSidecarAsync(savePath, token);
        if (metadata is not null && !metadata.LibraryFile.Equals(context.Metadata.LibraryFile, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The save belongs to a different library entry.");
        if (!allowUnverified && (metadata is null || metadata.Imported || metadata.LaunchFingerprint != context.Metadata.LaunchFingerprint))
            throw new InvalidOperationException("Save compatibility is unverified. Review the save and confirm the engine/mod versions before loading.");
        _ = await ReadEntryAsync(savePath, token); // reject malformed or unsupported save containers before starting an engine.
    }

    public static async Task<IReadOnlyList<SaveGameEntry>> LoadAsync(string root, string libraryFile, string gameHash, CancellationToken token = default)
    {
        var directory = Path.Combine(root, "Data", "SaveGames"); if (!Directory.Exists(directory)) return [];
        var result = new List<SaveGameEntry>(); var count = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.zds", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            token.ThrowIfCancellationRequested(); if (++count > 20000) throw new IOException("Too many managed saves; archive older save directories first.");
            var metadata = await ReadSidecarAsync(path, token);
            if (metadata?.LibraryFile.Equals(libraryFile.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) != true
                && !Path.GetDirectoryName(path)!.Equals(Path.Combine(directory, gameHash), StringComparison.OrdinalIgnoreCase)) continue;
            try { result.Add(await ReadEntryAsync(path, token)); }
            catch (InvalidDataException) { result.Add(new(path, Path.GetFileName(path), "unsupported", "", File.GetLastWriteTime(path), metadata, null)); }
        }
        return result.OrderByDescending(item => item.Modified).ToArray();
    }

    public static async Task<SaveGameEntry> ReadEntryAsync(string path, CancellationToken token = default)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > 20000) throw new InvalidDataException("Save container contains too many entries.");
        var info = archive.Entries.FirstOrDefault(entry => entry.Name.Equals("info.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Only ZIP-based ZDoom save containers can be resumed here.");
        if (info.Length > 2 * 1024 * 1024) throw new InvalidDataException("Save header is too large.");
        await using var stream = info.Open();
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(await ReadBoundedAsync(stream, 2 * 1024 * 1024, token)); }
        catch (JsonException error) { throw new InvalidDataException("Invalid save-game header.", error); }
        using var json = parsed;
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid save-game header.");
        string Read(params string[] names)
        {
            foreach (var name in names) foreach (var pair in json.RootElement.EnumerateObject())
                if (pair.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && pair.Value.ValueKind == JsonValueKind.String) return pair.Value.GetString() ?? "";
            return "";
        }
        byte[]? png = null; var preview = archive.Entries.FirstOrDefault(entry => entry.Name.Equals("savepic.png", StringComparison.OrdinalIgnoreCase));
        if (preview is { Length: > 24 and < 4 * 1024 * 1024 })
        {
            using var input = preview.Open(); var bytes = await ReadBoundedAsync(input, 4 * 1024 * 1024, token);
            if (bytes.Length >= 24 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 })
                && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)) <= 8192
                && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20)) <= 8192) png = bytes;
        }
        var title = Read("Title", "Description"); var map = Read("Current Map", "Map", "Map Name"); var engine = Read("Engine", "Software");
        var metadata = await ReadSidecarAsync(path, token);
        return new(path, title.Length > 0 ? title : Path.GetFileName(path), map, engine.Length > 0 ? engine : metadata?.EngineName ?? "?", File.GetLastWriteTime(path), metadata, png);
    }

    public static async Task<string> ImportAsync(string root, string source, string libraryFile, string gameHash, CancellationToken token = default)
    {
        _ = await ReadEntryAsync(source, token);
        var directory = Path.Combine(root, "Data", "SaveGames", gameHash); Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(source));
        using var journal = new FileChangeJournal(); journal.Track(target); journal.Track(target + ".dl667.json");
        await using (var input = File.OpenRead(source)) await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            await input.CopyToAsync(output, token);
        await WriteSidecarAsync(target, new(1, libraryFile.Replace('\\', '/'), gameHash, "Unknown", "", "", "", DateTimeOffset.UtcNow.ToString("O"), true), token);
        journal.Commit();
        return target;
    }

    public static async Task<string?> BackupAsync(string root, string directory, CancellationToken token = default)
    {
        if (!Directory.EnumerateFiles(directory, "*.zds").Any()) return null;
        var backup = Path.Combine(root, "Data", "SaveGameBackups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup); var copied = new List<string>(); var hashes = new Dictionary<string, string>();
        foreach (var file in Directory.EnumerateFiles(directory).Where(path => path.EndsWith(".zds", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".zds.dl667.json", StringComparison.OrdinalIgnoreCase)))
        {
            token.ThrowIfCancellationRequested(); var name = Path.GetFileName(file);
            await using var input = File.OpenRead(file); await using var output = new FileStream(Path.Combine(backup, name), FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
            await input.CopyToAsync(output, token); copied.Add(name);
            await output.FlushAsync(token); hashes[name] = await HashAsync(file, token);
        }
        await File.WriteAllTextAsync(Path.Combine(backup, "restore.json"), JsonSerializer.Serialize(new { Target = Path.GetRelativePath(root, directory).Replace('\\', '/'), Files = copied, Hashes = hashes }), token);
        return backup;
    }

    public static async Task RestoreBackupAsync(string root, string backup, CancellationToken token = default)
    {
        if (new FileInfo(Path.Combine(backup, "restore.json")).Length > 4 * 1024 * 1024) throw new InvalidDataException("Save backup manifest is too large.");
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(backup, "restore.json"), token));
        var relative = json.RootElement.GetProperty("Target").GetString() ?? "";
        if (!relative.StartsWith("Data/SaveGames/", StringComparison.Ordinal) || relative.Split('/').Any(part => part is "" or "." or "..") || relative.Contains('\\') || relative.Contains(':')) throw new InvalidDataException("Unsafe save backup target.");
        var target = Path.GetFullPath(Path.Combine(root, relative));
        for (var ancestor = target; !string.IsNullOrEmpty(ancestor); ancestor = Path.GetDirectoryName(ancestor))
            if (Directory.Exists(ancestor) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked restore targets are not supported.");
        var files = new List<string>();
        foreach (var file in json.RootElement.GetProperty("Files").EnumerateArray())
        {
            var name = file.GetString() ?? "";
            if (name.Length == 0 || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !name.EndsWith(".zds", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".zds.dl667.json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe save backup entry.");
            if (!json.RootElement.GetProperty("Hashes").TryGetProperty(name, out var hash) || await HashAsync(Path.Combine(backup, name), token) != hash.GetString()) throw new InvalidDataException("Save backup checksum mismatch.");
            files.Add(name);
        }
        if (files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count) throw new InvalidDataException("Repeated backup file.");
        Directory.CreateDirectory(target);
        await BackupAsync(root, target, token);
        using var journal = new FileChangeJournal();
        foreach (var name in files)
        {
            token.ThrowIfCancellationRequested(); var destination = Path.Combine(target, name); journal.Track(destination);
            if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked restore files are not supported.");
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.Copy(Path.Combine(backup, name), temporary); File.Move(temporary, destination, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        journal.Commit();
    }

    public static async Task<SaveGameMetadata?> ReadSidecarAsync(string save, CancellationToken token = default)
    {
        var path = save + ".dl667.json"; if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Save metadata is too large.");
        try
        {
            var metadata = JsonSerializer.Deserialize<SaveGameMetadata>(await File.ReadAllTextAsync(path, token));
            return metadata is not null && metadata.Format == 1 && !string.IsNullOrWhiteSpace(metadata.LibraryFile)
                && metadata.LibraryFile.Length <= 1000 && metadata.GameHash is { Length: 64 } && metadata.GameHash.All(Uri.IsHexDigit)
                && metadata.EngineName is { Length: <= 200 } && metadata.EngineVersion is { Length: <= 200 }
                && metadata.EngineHash is { Length: 0 or 64 } && metadata.LaunchFingerprint is { Length: 0 or 64 }
                ? metadata : null;
        }
        catch (JsonException) { return null; }
    }
    private static async Task WriteSidecarAsync(string save, SaveGameMetadata metadata, CancellationToken token)
    {
        var temporary = save + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(metadata), token); File.Move(temporary, save + ".dl667.json", true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static async Task<byte[]> ReadBoundedAsync(Stream input, int maximum, CancellationToken token)
    {
        using var output = new MemoryStream(); var buffer = new byte[32768]; int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        { if (output.Length + count > maximum) throw new InvalidDataException("Save header/preview exceeds its safety limit."); await output.WriteAsync(buffer.AsMemory(0, count), token); }
        return output.ToArray();
    }
}
