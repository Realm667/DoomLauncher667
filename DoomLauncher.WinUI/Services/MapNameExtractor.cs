using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using SharpCompress.Archives;

namespace DoomLauncher.WinUI.Services;

internal static partial class MapNameExtractor
{
    private const long MaximumEmbeddedWadSize = 256L * 1024 * 1024;

    public static async Task<IReadOnlyList<string>> ExtractAsync(
        string archivePath,
        CancellationToken cancellationToken)
    {
        archivePath = Path.GetFullPath(archivePath);
        if (!File.Exists(archivePath))
            return [];

        var maps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extension = Path.GetExtension(archivePath);
        if (extension.Equals(".wad", StringComparison.OrdinalIgnoreCase))
        {
            AddWadMaps(
                await File.ReadAllBytesAsync(archivePath, cancellationToken),
                maps);
        }
        else if (extension is ".zip" or ".pk3" or ".pk7"
                 || extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                 || extension.Equals(".pk3", StringComparison.OrdinalIgnoreCase)
                 || extension.Equals(".pk7", StringComparison.OrdinalIgnoreCase))
        {
            await using var stream = File.OpenRead(archivePath);
            await ReadZipAsync(stream, maps, cancellationToken, depth: 0);
        }
        else if (extension.Equals(".7z", StringComparison.OrdinalIgnoreCase)
                 || extension.Equals(".rar", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = entry.Key ?? string.Empty;
                AddMapFromPath(key, maps);
                if (entry.Size <= 0
                    || entry.Size > MaximumEmbeddedWadSize)
                {
                    continue;
                }
                var embeddedExtension = Path.GetExtension(key);
                await using var stream = entry.OpenEntryStream();
                var bytes = await ReadAllBytesAsync(
                    stream,
                    entry.Size,
                    cancellationToken);
                if (embeddedExtension.Equals(".wad", StringComparison.OrdinalIgnoreCase))
                    AddWadMaps(bytes, maps);
                else if (IsZipExtension(embeddedExtension))
                    await ReadZipAsync(
                        new MemoryStream(bytes, writable: false),
                        maps,
                        cancellationToken,
                        depth: 1);
            }
        }

        return maps
            .OrderBy(MapSortKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<string> ParseStored(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        var maps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match range in MapRangeRegex().Matches(value))
            ExpandRange(range.Groups[1].Value, range.Groups[2].Value, maps);
        foreach (Match match in MapNameRegex().Matches(value))
            maps.Add(match.Value.ToUpperInvariant());
        return maps
            .OrderBy(MapSortKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<byte[]> ReadAllBytesAsync(
        Stream source,
        long length,
        CancellationToken cancellationToken)
    {
        await using var destination = new MemoryStream(
            length > 0 && length <= int.MaxValue ? checked((int)length) : 0);
        await source.CopyToAsync(destination, cancellationToken);
        return destination.ToArray();
    }

    private static async Task ReadZipAsync(
        Stream source,
        ISet<string> maps,
        CancellationToken cancellationToken,
        int depth)
    {
        if (depth > 2)
            return;
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddMapFromPath(entry.FullName, maps);
            if (entry.Length <= 0 || entry.Length > MaximumEmbeddedWadSize)
                continue;

            var extension = Path.GetExtension(entry.FullName);
            if (!extension.Equals(".wad", StringComparison.OrdinalIgnoreCase)
                && !IsZipExtension(extension))
            {
                continue;
            }
            await using var entryStream = entry.Open();
            var bytes = await ReadAllBytesAsync(
                entryStream,
                entry.Length,
                cancellationToken);
            if (extension.Equals(".wad", StringComparison.OrdinalIgnoreCase))
                AddWadMaps(bytes, maps);
            else
                await ReadZipAsync(
                    new MemoryStream(bytes, writable: false),
                    maps,
                    cancellationToken,
                    depth + 1);
        }
    }

    private static bool IsZipExtension(string extension) =>
        extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".pk3", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".pk7", StringComparison.OrdinalIgnoreCase);

    private static void AddMapFromPath(string path, ISet<string> maps)
    {
        var parts = path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        var mapsIndex = Array.FindIndex(
            parts,
            part => part.Equals("maps", StringComparison.OrdinalIgnoreCase));
        if (mapsIndex < 0 || mapsIndex + 1 >= parts.Length)
        {
            return;
        }

        var name = Path.GetFileNameWithoutExtension(parts[mapsIndex + 1]);
        if (MapNameRegex().IsMatch(name))
            maps.Add(MapNameRegex().Match(name).Value.ToUpperInvariant());
    }

    private static void AddWadMaps(byte[] wad, ISet<string> maps)
    {
        if (wad.Length < 12)
            return;
        var magic = Encoding.ASCII.GetString(wad, 0, 4);
        if (magic is not ("IWAD" or "PWAD"))
            return;
        var count = BitConverter.ToInt32(wad, 4);
        var directoryOffset = BitConverter.ToInt32(wad, 8);
        if (count < 0
            || directoryOffset < 0
            || (long)directoryOffset + ((long)count * 16) > wad.Length)
        {
            return;
        }

        for (var index = 0; index < count; index++)
        {
            var offset = directoryOffset + (index * 16);
            var name = Encoding.ASCII.GetString(wad, offset + 8, 8).TrimEnd('\0');
            if (MapNameRegex().IsMatch(name)
                && MapNameRegex().Match(name).Value.Length == name.Length)
            {
                maps.Add(name.ToUpperInvariant());
            }
        }
    }

    private static void ExpandRange(string first, string last, ISet<string> maps)
    {
        first = first.ToUpperInvariant();
        last = last.ToUpperInvariant();
        if (first.StartsWith("MAP", StringComparison.Ordinal)
            && last.StartsWith("MAP", StringComparison.Ordinal)
            && int.TryParse(first[3..], out var start)
            && int.TryParse(last[3..], out var end)
            && end >= start
            && end - start <= 999)
        {
            var digits = Math.Max(first.Length, last.Length) - 3;
            for (var map = start; map <= end; map++)
                maps.Add($"MAP{map.ToString($"D{digits}")}");
            return;
        }

        if (first.Length == 4
            && last.Length == 4
            && first[0] == 'E'
            && first[2] == 'M'
            && last[0] == 'E'
            && last[2] == 'M'
            && first[1] == last[1]
            && int.TryParse(first[3].ToString(), out start)
            && int.TryParse(last[3].ToString(), out end)
            && end >= start)
        {
            for (var map = start; map <= end; map++)
                maps.Add($"E{first[1]}M{map}");
        }
    }

    private static string MapSortKey(string map)
    {
        if (map.StartsWith("MAP", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(map[3..], out var numeric))
        {
            return $"1-{numeric:D4}";
        }
        if (map.Length == 4
            && map[0] is 'E' or 'e'
            && map[2] is 'M' or 'm')
        {
            return $"0-{map[1]}-{map[3]}";
        }
        return $"2-{map}";
    }

    [GeneratedRegex(@"(?<![A-Z0-9])(?:MAP\d{1,3}|E\dM\d)(?![A-Z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MapNameRegex();

    [GeneratedRegex(@"(MAP\d{1,3}|E\dM\d)\s*[-–]\s*(MAP\d{1,3}|E\dM\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MapRangeRegex();
}
