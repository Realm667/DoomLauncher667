using System.Security.Cryptography;
using System.Text.Json;
using SharpCompress.Archives;

namespace DoomLauncher.WinUI.Services;

internal static class IwadVersionDetector
{
    private const string CatalogFileName = "iwad-hashes.json";
    private static readonly SemaphoreSlim CatalogLock = new(1, 1);
    private static IReadOnlyList<IwadHashEntry>? _catalog;

    public static async Task<IwadVersionDetection> DetectAsync(
        string archivePath,
        string internalFileName,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("Die konfigurierte IWAD-Datei wurde nicht gefunden.", archivePath);

        var requestedName = DatabaseTextSanitizer.SingleLine(internalFileName);
        await using var input = await OpenIwadStreamAsync(
            archivePath,
            requestedName,
            cancellationToken);
        var md5 = Convert.ToHexString(
                await MD5.HashDataAsync(input, cancellationToken))
            .ToLowerInvariant();
        var size = input.CanSeek ? input.Length : 0;
        var catalog = await LoadCatalogAsync(cancellationToken);
        var match = catalog.FirstOrDefault(entry =>
            entry.Md5.Equals(md5, StringComparison.OrdinalIgnoreCase)
            && (entry.Size <= 0 || size <= 0 || entry.Size == size));
        return match is null
            ? new IwadVersionDetection(
                false,
                string.Empty,
                string.Empty,
                md5,
                size,
                string.Empty)
            : new IwadVersionDetection(
                true,
                match.Version,
                match.Edition,
                md5,
                size,
                match.Label);
    }

    public static async Task<IReadOnlyList<IwadArchiveCandidate>> ScanArchiveAsync(
        string archivePath,
        CancellationToken cancellationToken)
    {
        archivePath = Path.GetFullPath(archivePath);
        if (!File.Exists(archivePath))
            return [];

        var catalog = await LoadCatalogAsync(cancellationToken);
        var candidates = new List<IwadArchiveCandidate>();
        if (Path.GetExtension(archivePath).Equals(".wad", StringComparison.OrdinalIgnoreCase))
        {
            await using var stream = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var candidate = await ReadCandidateAsync(
                stream,
                Path.GetFileName(archivePath),
                catalog,
                cancellationToken);
            if (candidate is not null)
                candidates.Add(candidate);
            return candidates;
        }

        using var archive = ArchiveFactory.OpenArchive(archivePath);
        foreach (var entry in archive.Entries.Where(candidate =>
                     !candidate.IsDirectory
                     && Path.GetExtension(candidate.Key ?? string.Empty).Equals(
                         ".wad",
                         StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Size <= 0 || entry.Size > 512L * 1024 * 1024)
                continue;
            await using var source = entry.OpenEntryStream();
            await using var copy = new MemoryStream(
                entry.Size <= int.MaxValue ? (int)entry.Size : 0);
            await source.CopyToAsync(copy, cancellationToken);
            copy.Position = 0;
            var candidate = await ReadCandidateAsync(
                copy,
                Path.GetFileName(entry.Key ?? string.Empty),
                catalog,
                cancellationToken);
            if (candidate is not null)
                candidates.Add(candidate);
        }
        return candidates;
    }

    private static async Task<IwadArchiveCandidate?> ReadCandidateAsync(
        Stream input,
        string fileName,
        IReadOnlyList<IwadHashEntry> catalog,
        CancellationToken cancellationToken)
    {
        if (input.Length < 12
            || fileName.Equals("VOICES.WAD", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var header = new byte[4];
        _ = await input.ReadAsync(header, cancellationToken);
        input.Position = 0;
        var md5 = Convert.ToHexString(
                await MD5.HashDataAsync(input, cancellationToken))
            .ToLowerInvariant();
        var size = input.Length;
        var match = catalog.FirstOrDefault(entry =>
            entry.Md5.Equals(md5, StringComparison.OrdinalIgnoreCase)
            && (entry.Size <= 0 || entry.Size == size));
        var magic = System.Text.Encoding.ASCII.GetString(header);
        if (match is null && !magic.Equals("IWAD", StringComparison.Ordinal))
            return null;

        var suggestedName = match?.Edition;
        if (string.IsNullOrWhiteSpace(suggestedName))
            suggestedName = Path.GetFileNameWithoutExtension(fileName);
        return new IwadArchiveCandidate(
            fileName,
            suggestedName,
            match?.Version ?? string.Empty,
            md5,
            size,
            match?.Label ?? string.Empty);
    }

    private static async Task<Stream> OpenIwadStreamAsync(
        string archivePath,
        string requestedName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Path.GetExtension(archivePath).Equals(".wad", StringComparison.OrdinalIgnoreCase))
            return new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

        var archive = ArchiveFactory.OpenArchive(archivePath);
        var entry = archive.Entries.FirstOrDefault(candidate =>
            !candidate.IsDirectory
            && (string.IsNullOrWhiteSpace(requestedName)
                ? Path.GetExtension(candidate.Key ?? string.Empty).Equals(
                    ".wad",
                    StringComparison.OrdinalIgnoreCase)
                : Path.GetFileName(candidate.Key ?? string.Empty).Equals(
                    Path.GetFileName(requestedName),
                    StringComparison.OrdinalIgnoreCase)));
        if (entry is null)
        {
            archive.Dispose();
            throw new InvalidDataException(
                string.IsNullOrWhiteSpace(requestedName)
                    ? "Das Archiv enthält keine IWAD-Datei."
                    : $"Die IWAD-Datei {requestedName} wurde im Archiv nicht gefunden.");
        }

        await using var source = entry.OpenEntryStream();
        var copy = new MemoryStream(
            entry.Size > 0 && entry.Size <= int.MaxValue ? (int)entry.Size : 0);
        await source.CopyToAsync(copy, cancellationToken);
        archive.Dispose();
        copy.Position = 0;
        return copy;
    }

    private static async Task<IReadOnlyList<IwadHashEntry>> LoadCatalogAsync(
        CancellationToken cancellationToken)
    {
        if (_catalog is not null)
            return _catalog;
        await CatalogLock.WaitAsync(cancellationToken);
        try
        {
            if (_catalog is not null)
                return _catalog;
            var catalogPath = Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                CatalogFileName);
            await using var stream = File.OpenRead(catalogPath);
            _catalog = await JsonSerializer.DeserializeAsync<List<IwadHashEntry>>(
                    stream,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                    },
                    cancellationToken)
                ?? [];
            return _catalog;
        }
        finally
        {
            CatalogLock.Release();
        }
    }

    private sealed record IwadHashEntry(
        string Md5,
        long Size,
        string Version,
        string Edition,
        string Label);
}
