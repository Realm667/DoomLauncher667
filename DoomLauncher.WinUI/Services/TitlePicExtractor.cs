using System.Drawing;
using System.Drawing.Imaging;
using SharpCompress.Archives;

namespace DoomLauncher.WinUI.Services;

internal static class TitlePicExtractor
{
    private static readonly string[] TitleNames = ["TITLEPIC", "TITLE"];
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp"];

    public static async Task<byte[]?> TryExtractPngAsync(
        string archivePath,
        CancellationToken cancellationToken)
    {
        archivePath = Path.GetFullPath(archivePath);
        if (!File.Exists(archivePath))
            return null;

        var extension = Path.GetExtension(archivePath);
        if (extension.Equals(".wad", StringComparison.OrdinalIgnoreCase))
        {
            return TryExtractFromWad(
                await File.ReadAllBytesAsync(archivePath, cancellationToken));
        }
        if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".pk3", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".pk7", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        using var archive = ArchiveFactory.OpenArchive(archivePath);
        var entries = archive.Entries
            .Where(entry => !entry.IsDirectory)
            .ToArray();
        foreach (var titleName in TitleNames)
        {
            var imageEntry = entries.FirstOrDefault(entry =>
                TitleNamesEqual(entry.Key ?? string.Empty, titleName)
                && ImageExtensions.Contains(
                    Path.GetExtension(entry.Key),
                    StringComparer.OrdinalIgnoreCase));
            if (imageEntry is null)
                continue;
            var data = await ReadEntryAsync(imageEntry, cancellationToken);
            var png = ConvertStandardImageToPng(data);
            if (png is not null)
                return png;
        }

        foreach (var wadEntry in entries.Where(entry =>
                     Path.GetExtension(entry.Key ?? string.Empty)
                         .Equals(".wad", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var png = TryExtractFromWad(
                await ReadEntryAsync(wadEntry, cancellationToken));
            if (png is not null)
                return png;
        }
        return null;
    }

    private static async Task<byte[]> ReadEntryAsync(
        IArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        await using var source = entry.OpenEntryStream();
        await using var destination = new MemoryStream(
            entry.Size > 0 && entry.Size <= int.MaxValue
                ? checked((int)entry.Size)
                : 0);
        await source.CopyToAsync(destination, cancellationToken);
        return destination.ToArray();
    }

    private static bool TitleNamesEqual(string path, string expected) =>
        Path.GetFileNameWithoutExtension(path)
            .Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static byte[]? TryExtractFromWad(byte[] wad)
    {
        if (!TryReadWadLumps(wad, out var lumps))
            return null;
        var title = TitleNames
            .Select(name => lumps.FirstOrDefault(lump =>
                lump.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(lump => lump.Data is not null);
        if (title.Data is null)
            return null;

        var direct = ConvertStandardImageToPng(title.Data);
        if (direct is not null)
            return direct;

        var paletteData = lumps.FirstOrDefault(lump =>
            lump.Name.Equals("PLAYPAL", StringComparison.OrdinalIgnoreCase)).Data;
        if (paletteData is null)
        {
            var defaultPalette = Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "DoomPLAYPAL.LMP");
            if (File.Exists(defaultPalette))
                paletteData = File.ReadAllBytes(defaultPalette);
        }
        if (paletteData is null || paletteData.Length < 256 * 3)
            return null;
        using var bitmap = DecodePaletteImage(title.Data, paletteData);
        if (bitmap is null)
            return null;
        return SavePng(bitmap);
    }

    private static byte[]? ConvertStandardImageToPng(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: true);
            using var bitmap = new Bitmap(image);
            return SavePng(bitmap);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static byte[] SavePng(Image image)
    {
        using var output = new MemoryStream();
        image.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    private static Bitmap? DecodePaletteImage(byte[] data, byte[] palette)
    {
        var flatDimensions = data.Length switch
        {
            320 * 200 => (Width: 320, Height: 200),
            560 * 200 => (Width: 560, Height: 200),
            256 * 256 => (Width: 256, Height: 256),
            128 * 128 => (Width: 128, Height: 128),
            64 * 64 => (Width: 64, Height: 64),
            _ => (Width: 0, Height: 0),
        };
        if (flatDimensions.Width > 0)
        {
            var flat = new Bitmap(
                flatDimensions.Width,
                flatDimensions.Height,
                PixelFormat.Format32bppArgb);
            for (var y = 0; y < flat.Height; y++)
            {
                for (var x = 0; x < flat.Width; x++)
                {
                    flat.SetPixel(
                        x,
                        y,
                        PaletteColor(palette, data[(y * flat.Width) + x]));
                }
            }
            return flat;
        }

        if (data.Length < 12)
            return null;
        var width = BitConverter.ToInt16(data, 0);
        var height = BitConverter.ToInt16(data, 2);
        if (width <= 0
            || height <= 0
            || width > 4096
            || height > 4096
            || 8L + (width * 4L) > data.Length)
        {
            return null;
        }
        var patch = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        for (var column = 0; column < width; column++)
        {
            var cursor = BitConverter.ToInt32(data, 8 + (column * 4));
            if (cursor < 0 || cursor >= data.Length)
                continue;
            var previousRow = -1;
            while (cursor < data.Length)
            {
                var rowStart = (int)data[cursor++];
                if (rowStart == 0xFF)
                    break;
                if (cursor + 2 > data.Length)
                    break;
                var count = data[cursor++];
                cursor++;
                if (rowStart <= previousRow)
                    rowStart += previousRow;
                previousRow = rowStart;
                for (var pixel = 0; pixel < count && cursor < data.Length; pixel++)
                {
                    var y = rowStart + pixel;
                    var paletteIndex = data[cursor++];
                    if (y >= 0 && y < height)
                        patch.SetPixel(column, y, PaletteColor(palette, paletteIndex));
                }
                if (cursor < data.Length)
                    cursor++;
            }
        }
        return patch;
    }

    private static Color PaletteColor(byte[] palette, byte index)
    {
        var offset = index * 3;
        return Color.FromArgb(
            255,
            palette[offset],
            palette[offset + 1],
            palette[offset + 2]);
    }

    private static bool TryReadWadLumps(
        byte[] wad,
        out IReadOnlyList<WadLump> lumps)
    {
        lumps = [];
        if (wad.Length < 12)
            return false;
        var magic = System.Text.Encoding.ASCII.GetString(wad, 0, 4);
        if (magic is not ("IWAD" or "PWAD"))
            return false;
        var count = BitConverter.ToInt32(wad, 4);
        var directoryOffset = BitConverter.ToInt32(wad, 8);
        if (count < 0
            || directoryOffset < 0
            || (long)directoryOffset + ((long)count * 16) > wad.Length)
        {
            return false;
        }

        var result = new List<WadLump>(count);
        for (var index = 0; index < count; index++)
        {
            var offset = directoryOffset + (index * 16);
            var dataOffset = BitConverter.ToInt32(wad, offset);
            var dataLength = BitConverter.ToInt32(wad, offset + 4);
            if (dataOffset < 0
                || dataLength < 0
                || (long)dataOffset + dataLength > wad.Length)
            {
                continue;
            }
            var name = System.Text.Encoding.ASCII
                .GetString(wad, offset + 8, 8)
                .TrimEnd('\0');
            result.Add(new WadLump(
                name,
                wad.AsSpan(dataOffset, dataLength).ToArray()));
        }
        lumps = result;
        return true;
    }

    private readonly record struct WadLump(string Name, byte[]? Data);
}
