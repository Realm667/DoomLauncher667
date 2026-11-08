using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SharpCompress.Archives;

namespace DoomLauncher.WinUI.Services;

public readonly record struct MapVertex(double X, double Y);
public readonly record struct MapLine(int Start, int End, bool TwoSided);
public sealed record MapDocument(string Name, string Title, string ArchiveEntry, string Format,
    IReadOnlyList<MapVertex> Vertices, IReadOnlyList<MapLine> Lines, int Things, int Sectors,
    int? StandardMonsters, int? StandardItems, int? Secrets)
{
    public string Label => Name == Title ? Name : $"{Name} · {Title}";
}

public static class MapBrowserService
{
    private const int MaximumFileBytes = 256 * 1024 * 1024;
    private const long MaximumTotalBytes = 768L * 1024 * 1024;
    private static readonly HashSet<int> DoomMonsters = [7, 9, 16, 58, 64, 65, 66, 67, 68, 69, 71, 72, 84, 88, 3001, 3002, 3003, 3004, 3005, 3006];
    private static readonly HashSet<int> DoomItems = [5, 6, 13, 17, 38, 39, 40, 82, 83, 2001, 2002, 2003, 2004, 2005, 2006, 2007, 2008, 2010, 2011, 2012, 2013, 2014, 2015, 2018, 2019, 2022, 2023, 2024, 2025, 2026, 2045, 2046, 2047, 2048, 2049];
    private sealed record Lump(string Name, int Offset, int Length);

    public static Task<IReadOnlyList<MapDocument>> ReadAsync(string path, bool doomGame, CancellationToken token = default) => Task.Run(async () =>
    {
        var result = new List<MapDocument>(); var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        async Task Visit(Stream stream, string entry, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth > 4) throw new InvalidDataException("Archive nesting exceeds the map-browser limit.");
            var extension = Path.GetExtension(entry).ToLowerInvariant();
            if (extension == ".wad")
            {
                var bytes = await ReadBoundedAsync(stream, MaximumFileBytes, token); total += bytes.Length;
                if (total > MaximumTotalBytes) throw new InvalidDataException("Map data exceeds the memory safety limit.");
                result.AddRange(ReadWad(bytes, entry, doomGame, token));
                foreach (var lump in ReadDirectory(bytes).Where(lump => lump.Name is "MAPINFO" or "ZMAPINFO" or "UMAPINFO" && lump.Length <= 4 * 1024 * 1024))
                    ReadTitles(Encoding.UTF8.GetString(bytes, lump.Offset, lump.Length), titles);
                return;
            }
            using var archive = ArchiveFactory.OpenArchive(stream);
            foreach (var item in archive.Entries.Where(item => !item.IsDirectory))
            {
                token.ThrowIfCancellationRequested(); var name = item.Key ?? string.Empty; var ext = Path.GetExtension(name).ToLowerInvariant();
                if (Path.GetFileName(name).Equals("MAPINFO", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(name).Equals("ZMAPINFO", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(name).Equals("UMAPINFO", StringComparison.OrdinalIgnoreCase))
                { using var input = item.OpenEntryStream(); ReadTitles(Encoding.UTF8.GetString(await ReadBoundedAsync(input, 4 * 1024 * 1024, token)), titles); }
                if (ext is not (".wad" or ".pk3" or ".pk7" or ".ipk3" or ".pke" or ".zip" or ".7z" or ".rar")) continue;
                if (item.Size > MaximumFileBytes) throw new InvalidDataException("Embedded map archive is too large.");
                using var inputMap = item.OpenEntryStream(); var copy = await ReadBoundedAsync(inputMap, MaximumFileBytes, token);
                using var memory = new MemoryStream(copy, false); await Visit(memory, entry + " / " + name, depth + 1);
            }
        }
        await using var file = File.OpenRead(path); await Visit(file, Path.GetFileName(path), 0);
        return (IReadOnlyList<MapDocument>)result.Select(map => titles.TryGetValue(map.Name, out var title) ? map with { Title = title } : map)
            .OrderBy(map => MapNameExtractor.MapSortKey(map.Name), StringComparer.OrdinalIgnoreCase).ToArray();
    }, token);

    private static async Task<byte[]> ReadBoundedAsync(Stream source, int maximum, CancellationToken token)
    {
        using var target = new MemoryStream(); var buffer = new byte[65536]; int read;
        while ((read = await source.ReadAsync(buffer, token)) != 0)
        { if (target.Length + read > maximum) throw new InvalidDataException("Archive entry exceeds its size limit."); await target.WriteAsync(buffer.AsMemory(0, read), token); }
        return target.ToArray();
    }

    private static IReadOnlyList<Lump> ReadDirectory(byte[] bytes)
    {
        if (bytes.Length < 12 || Encoding.ASCII.GetString(bytes, 0, 4) is not ("IWAD" or "PWAD")) throw new InvalidDataException("Invalid WAD header.");
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)); var directory = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        if (count is < 0 or > 100000 || directory < 0 || directory + count * 16L > bytes.Length) throw new InvalidDataException("Invalid WAD directory.");
        var result = new List<Lump>(count);
        for (var i = 0; i < count; i++)
        {
            var item = bytes.AsSpan(directory + i * 16, 16); var offset = BinaryPrimitives.ReadInt32LittleEndian(item); var length = BinaryPrimitives.ReadInt32LittleEndian(item[4..]);
            if (offset < 0 || length < 0 || (long)offset + length > bytes.Length) throw new InvalidDataException("WAD lump exceeds file bounds.");
            result.Add(new(Encoding.ASCII.GetString(item[8..]).TrimEnd('\0').ToUpperInvariant(), offset, length));
        }
        return result;
    }

    public static IReadOnlyList<MapDocument> ReadWad(byte[] bytes, string entry, bool doomGame, CancellationToken token = default)
    {
        var lumps = ReadDirectory(bytes); var result = new List<MapDocument>();
        var markers = Enumerable.Range(0, Math.Max(0, lumps.Count - 1)).Where(index => lumps[index + 1].Name is "THINGS" or "TEXTMAP").ToArray();
        for (var mapIndex = 0; mapIndex < markers.Length; mapIndex++)
        {
            token.ThrowIfCancellationRequested(); var start = markers[mapIndex]; var end = mapIndex + 1 < markers.Length ? markers[mapIndex + 1] : lumps.Count;
            var parts = lumps.Skip(start + 1).Take(end - start - 1).GroupBy(lump => lump.Name).ToDictionary(group => group.Key, group => group.First());
            var name = lumps[start].Name;
            if (parts.TryGetValue("TEXTMAP", out var text))
            { result.Add(ReadUdmf(Encoding.UTF8.GetString(bytes, text.Offset, text.Length), name, entry, doomGame, token)); continue; }
            var hexen = parts.ContainsKey("BEHAVIOR");
            var vertices = new List<MapVertex>(); var lines = new List<MapLine>(); var types = new List<int>(); var sectors = 0; int? secrets = hexen ? null : 0;
            if (parts.TryGetValue("VERTEXES", out var vertexLump))
            {
                if (vertexLump.Length % 4 != 0) throw new InvalidDataException("Incomplete WAD vertex record.");
                for (var p = vertexLump.Offset; p < vertexLump.Offset + vertexLump.Length; p += 4)
                    vertices.Add(new(BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(p)), BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(p + 2))));
            }
            if (parts.TryGetValue("LINEDEFS", out var lineLump))
            {
                var size = hexen ? 16 : 14; if (lineLump.Length % size != 0) throw new InvalidDataException("Incomplete WAD linedef record.");
                for (var p = lineLump.Offset; p < lineLump.Offset + lineLump.Length; p += size)
                {
                    var a = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p)); var b = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p + 2));
                    if (a >= vertices.Count || b >= vertices.Count) throw new InvalidDataException("Linedef references an invalid vertex.");
                    lines.Add(new(a, b, (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p + 4)) & 4) != 0));
                }
            }
            if (parts.TryGetValue("THINGS", out var thingLump))
            {
                var size = hexen ? 20 : 10; if (thingLump.Length % size != 0) throw new InvalidDataException("Incomplete WAD thing record.");
                for (var p = thingLump.Offset; p < thingLump.Offset + thingLump.Length; p += size)
                    types.Add(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p + (hexen ? 10 : 6))));
            }
            if (parts.TryGetValue("SECTORS", out var sectorLump))
            {
                if (sectorLump.Length % 26 != 0) throw new InvalidDataException("Incomplete WAD sector record."); sectors = sectorLump.Length / 26;
                if (!hexen) for (var p = sectorLump.Offset; p < sectorLump.Offset + sectorLump.Length; p += 26)
                { var special = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p + 22)); if (special == 9 || (special & 128) != 0) secrets++; }
            }
            result.Add(new(name, name, entry, hexen ? "Hexen" : "Doom", vertices, lines, types.Count, sectors,
                doomGame ? types.Count(DoomMonsters.Contains) : null, doomGame ? types.Count(DoomItems.Contains) : null, secrets));
        }
        return result;
    }

    private static void ReadTitles(string text, IDictionary<string, string> titles)
    {
        foreach (Match match in Regex.Matches(text, "\\bmap\\s+(?<slot>[A-Za-z0-9_]+)\\s+\"(?<title>[^\"\\r\\n]{1,256})\"", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
            titles[match.Groups["slot"].Value] = match.Groups["title"].Value;
        foreach (Match match in Regex.Matches(text, "\\bmap\\s+(?<slot>[A-Za-z0-9_]+)\\s*\\{[^}]*?\\blevelname\\s*=\\s*\"(?<title>[^\"\\r\\n]{1,256})\"", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1)))
            titles[match.Groups["slot"].Value] = match.Groups["title"].Value;
    }

    private static MapDocument ReadUdmf(string text, string name, string entry, bool doomGame, CancellationToken token)
    {
        var tokens = Tokenize(text); var vertices = new List<MapVertex>(); var rawLines = new List<(int A, int B, bool Two)>();
        var types = new List<int>(); var sectors = 0; var secrets = 0;
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            token.ThrowIfCancellationRequested(); var kind = tokens[i]; if (tokens[i + 1] != "{") continue;
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); i += 2;
            while (i < tokens.Count && tokens[i] != "}")
            {
                if (i + 3 >= tokens.Count || tokens[i + 1] != "=" || tokens[i + 3] != ";") throw new InvalidDataException("Invalid UDMF assignment.");
                fields[tokens[i]] = tokens[i + 2]; i += 4;
            }
            if (i >= tokens.Count) throw new InvalidDataException("Incomplete UDMF block.");
            double Number(string key, double fallback = 0) => fields.TryGetValue(key, out var value)
                ? double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) && Math.Abs(number) <= 1e9 ? number : throw new InvalidDataException("Invalid UDMF number.") : fallback;
            int Integer(string key, int fallback = 0)
            { var number = Number(key, fallback); if (number != Math.Truncate(number)) throw new InvalidDataException("Invalid UDMF integer."); return (int)number; }
            switch (kind.ToLowerInvariant())
            {
                case "vertex": vertices.Add(new(Number("x"), Number("y"))); break;
                case "linedef": rawLines.Add((Integer("v1", -1), Integer("v2", -1), fields.GetValueOrDefault("twosided") == "true")); break;
                case "thing": types.Add(Integer("type")); break;
                case "sector": sectors++; if (fields.GetValueOrDefault("secret") == "true" || Integer("special") == 9) secrets++; break;
            }
        }
        if (rawLines.Any(line => line.A < 0 || line.B < 0 || line.A >= vertices.Count || line.B >= vertices.Count)) throw new InvalidDataException("Invalid UDMF vertex reference.");
        return new(name, name, entry, "UDMF", vertices, rawLines.Select(line => new MapLine(line.A, line.B, line.Two)).ToArray(), types.Count, sectors,
            doomGame ? types.Count(DoomMonsters.Contains) : null, doomGame ? types.Count(DoomItems.Contains) : null, secrets);
    }

    private static List<string> Tokenize(string text)
    {
        var result = new List<string>();
        for (var i = 0; i < text.Length;)
        {
            if (result.Count > 4000000) throw new InvalidDataException("UDMF complexity exceeds the safety limit.");
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            { var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal); if (close < 0) throw new InvalidDataException("Incomplete UDMF comment."); i = close + 2; continue; }
            if ("{}=;".Contains(text[i])) { result.Add(text[i++].ToString()); continue; }
            if (text[i] == '"')
            {
                var value = new StringBuilder(); i++; var closed = false;
                while (i < text.Length)
                { var c = text[i++]; if (c == '"') { closed = true; break; } if (c == '\\' && i < text.Length) c = text[i++]; value.Append(c); }
                if (!closed) throw new InvalidDataException("Incomplete UDMF string."); result.Add(value.ToString()); continue;
            }
            var start = i; while (i < text.Length && !char.IsWhiteSpace(text[i]) && !"{}=;\"/".Contains(text[i])) i++;
            if (start == i) throw new InvalidDataException("Invalid UDMF token."); result.Add(text[start..i]);
            if (result.Count > 4000000) throw new InvalidDataException("UDMF complexity exceeds the safety limit.");
        }
        return result;
    }

    public static Task<string> RenderPreviewAsync(MapDocument map, string tempDirectory, CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested(); Directory.CreateDirectory(tempDirectory);
        var path = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + ".png");
        using var image = new Bitmap(1400, 1000); using var graphics = Graphics.FromImage(image); graphics.Clear(Color.FromArgb(22, 24, 26));
        if (map.Vertices.Count > 0)
        {
            var minX = map.Vertices.Min(p => p.X); var maxX = map.Vertices.Max(p => p.X); var minY = map.Vertices.Min(p => p.Y); var maxY = map.Vertices.Max(p => p.Y);
            var scale = Math.Min(1340 / Math.Max(1, maxX - minX), 940 / Math.Max(1, maxY - minY));
            using var wall = new Pen(Color.FromArgb(235, 235, 235), 1); using var twoSided = new Pen(Color.FromArgb(55, 135, 135), 1);
            foreach (var line in map.Lines)
            {
                token.ThrowIfCancellationRequested(); var a = map.Vertices[line.Start]; var b = map.Vertices[line.End];
                graphics.DrawLine(line.TwoSided ? twoSided : wall, (float)(30 + (a.X - minX) * scale), (float)(970 - (a.Y - minY) * scale),
                    (float)(30 + (b.X - minX) * scale), (float)(970 - (b.Y - minY) * scale));
            }
        }
        image.Save(path, ImageFormat.Png); return path;
    }, token);
}
