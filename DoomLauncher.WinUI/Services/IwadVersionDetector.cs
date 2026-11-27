using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpCompress.Archives;

namespace DoomLauncher.WinUI.Services;

// Hashes identify a release. UZDoom's ordered lump rules identify an edition,
// but never establish a file version or promise engine compatibility.
internal static class IwadVersionDetector
{
    private const long MaximumSize = 512L * 1024 * 1024;
    private const string LabelPrefix = "IWAD-ID:";
    private static readonly SemaphoreSlim CatalogLock = new(1, 1);
    private static IReadOnlyList<IwadHashEntry>? _catalog;
    private static IdentityCatalog? _identities;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    internal static async Task EnsureLoadedAsync(CancellationToken token = default)
    {
        if (_catalog is not null) return;
        await CatalogLock.WaitAsync(token);
        try
        {
            if (_catalog is not null) return;
            var directory = Path.Combine(AppContext.BaseDirectory, "Assets");
            var hashes = JsonSerializer.Deserialize<List<IwadHashEntry>>(
                await File.ReadAllTextAsync(Path.Combine(directory, "iwad-hashes.json"), token), JsonOptions) ?? [];
            _identities = JsonSerializer.Deserialize<IdentityCatalog>(
                await File.ReadAllTextAsync(Path.Combine(directory, "iwad-identities.json"), token), JsonOptions)
                ?? throw new InvalidDataException("Missing IWAD identities.");
            _catalog = hashes;
        }
        finally { CatalogLock.Release(); }
    }

    internal static string StoredEdition(string md5, string label)
        => ReadLabel(label)?.Edition ?? (md5.Length == 0 ? "" : _catalog?.FirstOrDefault(item =>
            item.Md5.Length > 0 && item.Md5.Equals(md5, StringComparison.OrdinalIgnoreCase))?.Edition ?? "");

    internal static string StoredVersion(string md5, string storedVersion)
        => md5.Length == 0 ? storedVersion : _catalog?.FirstOrDefault(item => item.Md5.Length > 0
            && item.Md5.Equals(md5, StringComparison.OrdinalIgnoreCase))?.Version ?? storedVersion;

    internal static string RequiredBase(string fileName, string md5, string label)
    {
        var required = ReadLabel(label)?.Required ?? _catalog?.FirstOrDefault(item =>
            item.Md5.Length > 0 && item.Md5.Equals(md5, StringComparison.OrdinalIgnoreCase))?.Required ?? "";
        if (required.Length > 0) return required;
        return Path.GetFileName(fileName).ToUpperInvariant() switch
        {
            "HEXDD.WAD" => "Hexen: Beyond Heretic",
            "SVE.WAD" => "Strife: Quest for the Sigil",
            _ => "",
        };
    }

    internal static bool IsBase(string fileName, string md5, string label, string required)
    {
        var edition = StoredEdition(md5, label);
        // Demo IWADs cannot satisfy the full-game dependency.
        return required switch
        {
            "Hexen: Beyond Heretic" => (edition.StartsWith("Hexen", StringComparison.OrdinalIgnoreCase)
                && !new[] { "Demo", "Shareware", "Beta", "Deathkings" }.Any(part => edition.Contains(part, StringComparison.OrdinalIgnoreCase)))
                || (edition.Length == 0 && Path.GetFileName(fileName).Equals("HEXEN.WAD", StringComparison.OrdinalIgnoreCase)),
            "Strife: Quest for the Sigil" => edition == required
                || (edition.Length == 0 && Path.GetFileName(fileName).Equals("STRIFE1.WAD", StringComparison.OrdinalIgnoreCase)),
            _ => edition.Equals(required, StringComparison.OrdinalIgnoreCase),
        };
    }

    internal static string IdentitySuffix(string title, string edition, string version)
    {
        static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        var identity = edition.Length == 0 || Normalize(title) == Normalize(edition) ? "" : edition;
        return string.Join(" ", new[] { identity, version }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    internal static (string Edition, string Version)? IdentifyHash(string md5, string sha1, long size)
    {
        var entry = FindHash(md5, sha1, size);
        return entry is null ? null : (entry.Edition, entry.Version);
    }

    private static IwadHashEntry? FindHash(string md5, string sha1, long size) => _catalog!.FirstOrDefault(entry =>
        (entry.Size <= 0 || entry.Size == size)
        && (entry.Sha1.Length > 0 ? entry.Sha1.Equals(sha1, StringComparison.OrdinalIgnoreCase)
            : entry.Md5.Length > 0 && entry.Md5.Equals(md5, StringComparison.OrdinalIgnoreCase)));

    public static async Task<IwadVersionDetection> DetectAsync(string archivePath, string internalFileName, CancellationToken token)
    {
        await EnsureLoadedAsync(token);
        if (!File.Exists(archivePath)) throw new FileNotFoundException("Die konfigurierte IWAD-Datei wurde nicht gefunden.", archivePath);
        var requested = Path.GetFileName(DatabaseTextSanitizer.SingleLine(internalFileName));
        await using var stream = await OpenIwadStreamAsync(archivePath, requested, token);
        var fileName = requested.Length > 0 ? requested : Path.GetFileName(archivePath);
        var info = await InspectAsync(stream, fileName, token);
        return new(info.Known, info.Version, info.Edition, info.Md5, stream.Length, info.Label);
    }

    internal static bool IsPayload(string path) => Path.GetExtension(path).ToLowerInvariant() is ".wad" or ".pk3" or ".ipk3";

    public static async Task<IReadOnlyList<IwadArchiveCandidate>> ScanArchiveAsync(string archivePath, CancellationToken token)
    {
        archivePath = Path.GetFullPath(archivePath);
        if (!File.Exists(archivePath)) return [];
        await EnsureLoadedAsync(token);
        if (IsPayload(archivePath))
        {
            await using var input = File.OpenRead(archivePath);
            if (input.Length > MaximumSize) throw new InvalidDataException("IWAD exceeds the supported size.");
            var candidate = await ReadCandidateAsync(input, Path.GetFileName(archivePath), token);
            return candidate is null ? [] : [candidate];
        }
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        var entries = archive.Entries.Where(entry => !entry.IsDirectory && IsPayload(entry.Key ?? "")).ToArray();
        var ambiguous = entries.GroupBy(entry => Path.GetFileName(entry.Key ?? ""), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<IwadArchiveCandidate>();
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Size <= 0 || entry.Size > MaximumSize) continue;
            await using var source = entry.OpenEntryStream();
            await using var copy = await CopyBoundedAsync(source, entry.Size, token);
            var candidate = await ReadCandidateAsync(copy, Path.GetFileName(entry.Key ?? ""), token);
            if (candidate is null) continue;
            if (ambiguous.Contains(candidate.InternalFileName))
                throw new InvalidDataException($"Der IWAD-Dateiname {candidate.InternalFileName} kommt mehrfach im Archiv vor. Bitte getrennte Archive verwenden.");
            result.Add(candidate);
        }
        return result;
    }

    private static async Task<IwadArchiveCandidate?> ReadCandidateAsync(Stream input, string fileName, CancellationToken token)
    {
        if (input.Length < 12 || fileName.Equals("VOICES.WAD", StringComparison.OrdinalIgnoreCase)) return null;
        var info = await InspectAsync(input, fileName, token);
        return info.IsIwad ? new(fileName, info.Edition.Length > 0 ? info.Edition : Path.GetFileNameWithoutExtension(fileName),
            info.Version, info.Md5, input.Length, info.Label) : null;
    }

    private static async Task<Inspection> InspectAsync(Stream input, string fileName, CancellationToken token)
    {
        input.Position = 0;
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[81920]; int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        { md5.AppendData(buffer, 0, read); sha1.AppendData(buffer, 0, read); }
        var digest = Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
        var sha = Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant();
        var match = FindHash(digest, sha, input.Length);
        if (match is not null)
            return new(true, true, match.Edition, match.Version, digest, MakeLabel(match.Edition, match.Required, match.Label));
        input.Position = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var iwadHeader = false; string customName = "";
        if (Path.GetExtension(fileName).Equals(".wad", StringComparison.OrdinalIgnoreCase))
        {
            var header = new byte[12];
            if (input.Length < header.Length) return new(false, false, "", "", digest, "");
            await input.ReadExactlyAsync(header, token);
            var magic = Encoding.ASCII.GetString(header, 0, 4);
            iwadHeader = magic == "IWAD";
            var count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            var offset = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
            if ((iwadHeader || magic == "PWAD") && count is >= 0 and <= 1_000_000
                && offset >= 12 && offset + 16L * count <= input.Length)
            {
                input.Position = offset;
                var directory = new byte[count * 16]; await input.ReadExactlyAsync(directory, token);
                for (var index = 0; index < count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var entryOffset = BinaryPrimitives.ReadInt32LittleEndian(directory.AsSpan(index * 16));
                    var length = BinaryPrimitives.ReadInt32LittleEndian(directory.AsSpan(index * 16 + 4));
                    if (entryOffset < 0 || length < 0 || entryOffset + (long)length > input.Length)
                        throw new InvalidDataException("Invalid IWAD lump directory.");
                    names.Add(Encoding.ASCII.GetString(directory, index * 16 + 8, 8).TrimEnd('\0'));
                }
            }
        }
        else
        {
            using var zip = new System.IO.Compression.ZipArchive(input, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count > 100_000) throw new InvalidDataException("Too many IWAD package entries.");
            foreach (var entry in zip.Entries)
                if (entry.Name.Length > 0) names.Add(Path.GetFileNameWithoutExtension(entry.Name));
            var metadata = zip.Entries.FirstOrDefault(entry => entry.FullName.Equals("IWADINFO", StringComparison.OrdinalIgnoreCase));
            if (metadata is not null && metadata.Length <= 65536)
            {
                using var reader = new StreamReader(metadata.Open());
                var text = (await reader.ReadToEndAsync(token));
                // IWADINFO belongs to the package; only its name is used, never a guessed release version.
                var block = Regex.Match(text, @"\bIWad\s*\{([^{}]*)\}", RegexOptions.IgnoreCase);
                if (block.Success && Regex.IsMatch(block.Groups[1].Value, @"\bGame\s*=", RegexOptions.IgnoreCase))
                    customName = DatabaseTextSanitizer.SingleLine(Regex.Match(block.Groups[1].Value,
                        "\\bName\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase).Groups[1].Value);
            }
        }
        var rule = _identities!.Rules.FirstOrDefault(rule => rule.MustContain.Length > 0
            && rule.MustContain.All(names.Contains)
            // One-map patches are not standalone games. Specific game markers may identify PWAD IWADs.
            && (iwadHeader || rule.MustContain.Length > 1));
        var edition = rule?.Edition ?? customName;
        return new(iwadHeader || rule is not null || customName.Length > 0, false, edition, "", digest,
            edition.Length == 0 ? "" : MakeLabel(edition, rule?.Required ?? "", _identities.Source));
    }

    private static string MakeLabel(string edition, string required, string source)
        => LabelPrefix + JsonSerializer.Serialize(new IdentityLabel(edition, required, source));
    private static IdentityLabel? ReadLabel(string label)
    {
        if (!label.StartsWith(LabelPrefix, StringComparison.Ordinal)) return null;
        try { return JsonSerializer.Deserialize<IdentityLabel>(label[LabelPrefix.Length..], JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static async Task<Stream> CopyBoundedAsync(Stream source, long size, CancellationToken token)
    {
        if (size < 0 || size > MaximumSize) throw new InvalidDataException("IWAD exceeds the supported size.");
        Stream copy = size <= 16L * 1024 * 1024 ? new MemoryStream((int)size)
            : new FileStream(Path.Combine(Path.GetTempPath(), "DL667-iwad-" + Guid.NewGuid().ToString("N") + ".tmp"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            var buffer = new byte[81920]; int read;
            while ((read = await source.ReadAsync(buffer, token)) > 0)
            {
                if (copy.Length + read > size) throw new InvalidDataException("IWAD exceeds its declared size.");
                await copy.WriteAsync(buffer.AsMemory(0, read), token);
            }
            if (copy.Length != size) throw new InvalidDataException("Incomplete IWAD archive entry.");
            copy.Position = 0; return copy;
        }
        catch { copy.Dispose(); throw; }
    }

    private static async Task<Stream> OpenIwadStreamAsync(string path, string requested, CancellationToken token)
    {
        if (IsPayload(path)) return File.OpenRead(path);
        using var archive = ArchiveFactory.OpenArchive(path);
        var entries = archive.Entries.Where(entry => !entry.IsDirectory && (requested.Length == 0
            ? IsPayload(entry.Key ?? "") : Path.GetFileName(entry.Key ?? "").Equals(requested, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (entries.Length != 1) throw new InvalidDataException("Das Archiv muss genau eine eindeutig benannte IWAD-Datei enthalten.");
        await using var source = entries[0].OpenEntryStream();
        return await CopyBoundedAsync(source, entries[0].Size, token);
    }

    private sealed record IwadHashEntry(string Md5, long Size, string Version, string Edition, string Label,
        string Sha1 = "", string Format = "wad", string Required = "");
    private sealed record IdentityCatalog(string Source, string[] Names, IdentityRule[] Rules);
    private sealed record IdentityRule(string Edition, string[] Names, string[] MustContain, string Required);
    private sealed record IdentityLabel(string Edition, string Required, string Source);
    private sealed record Inspection(bool IsIwad, bool Known, string Edition, string Version, string Md5, string Label);
}
