using System.Globalization;
using System.Text;
using SharpCompress.Archives;

namespace DoomLauncher.WinUI.Services;

internal sealed record ArchiveTextMetadata(
    string Title,
    string Author,
    string Description,
    DateTime? ReleaseDate)
{
    public static ArchiveTextMetadata Empty { get; } =
        new(string.Empty, string.Empty, string.Empty, null);

    public int Quality =>
        (string.IsNullOrWhiteSpace(Title) ? 0 : 4)
        + (string.IsNullOrWhiteSpace(Author) ? 0 : 2)
        + (string.IsNullOrWhiteSpace(Description) ? 0 : 1)
        + (ReleaseDate.HasValue ? 1 : 0);
}

internal static class ArchiveTextMetadataReader
{
    private const long MaximumTextSize = 2L * 1024 * 1024;
    private static readonly string[] ArchiveExtensions =
        [".zip", ".pk3", ".pk7", ".7z", ".rar"];
    private static readonly string[] KnownLabels =
    [
        "title", "author", "authors", "description", "date finished",
        "release date", "date", "filename", "game", "advanced engine needed",
        "primary purpose", "additional credits to", "email address",
        "misc. author info", "base", "build time", "editors used",
        "known bugs", "may not run with", "tested with",
    ];

    public static async Task<ArchiveTextMetadata> ReadAsync(
        string archivePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(archivePath)
            || !ArchiveExtensions.Contains(
                Path.GetExtension(archivePath),
                StringComparer.OrdinalIgnoreCase))
        {
            return ArchiveTextMetadata.Empty;
        }

        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            var candidates = new List<ArchiveTextMetadata>();
            foreach (var entry in archive.Entries.Where(entry =>
                         !entry.IsDirectory
                         && entry.Size > 0
                         && entry.Size <= MaximumTextSize
                         && IsMetadataText(entry.Key)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var stream = entry.OpenEntryStream();
                using var reader = new StreamReader(
                    stream,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 4096,
                    leaveOpen: false);
                var text = await reader.ReadToEndAsync(cancellationToken);
                var metadata = Parse(text);
                if (metadata.Quality > 0)
                    candidates.Add(metadata);
            }
            return candidates
                .OrderByDescending(candidate => candidate.Quality)
                .FirstOrDefault()
                ?? ArchiveTextMetadata.Empty;
        }
        catch (Exception exception) when (
            exception is IOException
            or InvalidDataException
            or NotSupportedException)
        {
            return ArchiveTextMetadata.Empty;
        }
    }

    internal static ArchiveTextMetadata Parse(string text)
    {
        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var title = FindSingleLine(lines, "title");
        var author = FindSingleLine(lines, "author", "authors");
        var description = FindDescription(lines);
        var dateText = FindSingleLine(
            lines,
            "date finished",
            "release date",
            "date");
        return new ArchiveTextMetadata(
            DatabaseTextSanitizer.SingleLine(title),
            DatabaseTextSanitizer.SingleLine(author),
            DatabaseTextSanitizer.Multiline(description),
            ParseDate(dateText));
    }

    private static bool IsMetadataText(string? key)
    {
        var fileName = Path.GetFileName(key ?? string.Empty);
        return Path.GetExtension(fileName).Equals(
                   ".txt",
                   StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("WADINFO", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("GAMEINFO", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindSingleLine(
        IReadOnlyList<string> lines,
        params string[] labels)
    {
        foreach (var line in lines)
        {
            if (!TrySplitField(line, out var label, out var value)
                || !labels.Contains(label, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return string.Empty;
    }

    private static string FindDescription(IReadOnlyList<string> lines)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            if (!TrySplitField(lines[index], out var label, out var value)
                || !label.Equals("description", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var result = new List<string>();
            if (!string.IsNullOrWhiteSpace(value))
                result.Add(value.Trim());
            for (index++; index < lines.Count; index++)
            {
                var line = lines[index];
                if (line.Trim().StartsWith("===", StringComparison.Ordinal)
                    || (TrySplitField(line, out var nextLabel, out _)
                        && KnownLabels.Contains(
                            nextLabel,
                            StringComparer.OrdinalIgnoreCase)))
                {
                    break;
                }
                result.Add(line.TrimEnd());
            }
            return string.Join(Environment.NewLine, result).Trim();
        }
        return string.Empty;
    }

    private static bool TrySplitField(
        string line,
        out string label,
        out string value)
    {
        var separator = line.IndexOf(':');
        if (separator <= 0)
        {
            label = string.Empty;
            value = string.Empty;
            return false;
        }
        label = line[..separator].Trim();
        value = line[(separator + 1)..].Trim();
        return label.Length > 0;
    }

    private static DateTime? ParseDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value
            .Replace("st", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("nd", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("rd", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("th", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
        return DateTime.TryParse(
            normalized,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out var result)
            ? result
            : null;
    }
}
