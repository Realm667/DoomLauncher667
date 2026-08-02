using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SharpCompress.Archives;

namespace DoomLauncher.WinUI.Services;

internal sealed record ArchiveTextMetadata(
    string Title,
    string Author,
    string Description,
    DateTime? ReleaseDate,
    string Game,
    string SourcePort)
{
    public static ArchiveTextMetadata Empty { get; } =
        new(string.Empty, string.Empty, string.Empty, null, string.Empty, string.Empty);

    public int Quality =>
        (string.IsNullOrWhiteSpace(Title) ? 0 : 4)
        + (string.IsNullOrWhiteSpace(Author) ? 0 : 2)
        + (string.IsNullOrWhiteSpace(Description) ? 0 : 1)
        + (ReleaseDate.HasValue ? 1 : 0)
        + (string.IsNullOrWhiteSpace(Game) ? 0 : 1)
        + (string.IsNullOrWhiteSpace(SourcePort) ? 0 : 1);
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
                using var memory = new MemoryStream((int)entry.Size);
                await stream.CopyToAsync(memory, cancellationToken);
                var text = DecodeText(memory.GetBuffer().AsSpan(0, (int)memory.Length));
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
            or NotSupportedException
            or SharpCompress.Common.ArchiveOperationException)
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
        var game = FindSingleLine(lines, "game", "iwad");
        var sourcePort = string.Join(
            " | ",
            FindValues(
                lines,
                "advanced engine needed",
                "tested with",
                "may not run with")
                .Distinct(StringComparer.OrdinalIgnoreCase));
        return new ArchiveTextMetadata(
            DatabaseTextSanitizer.SingleLine(title),
            DatabaseTextSanitizer.SingleLine(author),
            DatabaseTextSanitizer.Multiline(description),
            ParseDate(dateText),
            DatabaseTextSanitizer.SingleLine(game),
            DatabaseTextSanitizer.SingleLine(sourcePort));
    }

    internal static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            return Encoding.UTF8.GetString(bytes[3..]);
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE }))
            return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.StartsWith(new byte[] { 0xFE, 0xFF }))
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);

        try
        {
            return new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Most classic /idgames TXT files predate Unicode and use the
            // Windows ANSI code page. Falling back instead of accepting UTF-8
            // replacement characters preserves names such as Jägermörder.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
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

    private static IEnumerable<string> FindValues(
        IReadOnlyList<string> lines,
        params string[] labels)
    {
        foreach (var line in lines)
        {
            if (TrySplitField(line, out var label, out var value)
                && labels.Contains(label, StringComparer.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }
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

internal sealed record InferredLaunchDefinitions(
    int? SourcePortId,
    int? IwadId,
    string SourcePortName,
    string IwadName);

internal static partial class LaunchDefinitionMatcher
{
    public static InferredLaunchDefinitions Infer(
        ArchiveTextMetadata metadata,
        LauncherDefinitionsData definitions)
    {
        var iwadKey = DetectIwadKey(metadata.Game);
        var iwad = iwadKey is null
            ? null
            : definitions.Iwads
                .Where(item => item.IwadId.HasValue && MatchesIwad(item, iwadKey))
                .OrderByDescending(item => item.Version, NaturalVersionComparer.Instance)
                .FirstOrDefault();

        var sourceText = Normalize(metadata.SourcePort);
        var sourcePort = sourceText.Length == 0
            ? null
            : definitions.SourcePorts
                .Where(item => item.SourcePortId.HasValue)
                .Select(item => new
                {
                    Item = item,
                    Score = SourcePortScore(sourceText, Normalize(item.Name)),
                })
                .Where(candidate => candidate.Score > 0)
                .OrderByDescending(candidate => candidate.Score)
                .ThenByDescending(
                    candidate => candidate.Item.Version,
                    NaturalVersionComparer.Instance)
                .Select(candidate => candidate.Item)
                .FirstOrDefault();

        return new InferredLaunchDefinitions(
            sourcePort?.SourcePortId,
            iwad?.IwadId,
            sourcePort?.Name ?? string.Empty,
            iwad?.Name ?? string.Empty);
    }

    private static string? DetectIwadKey(string game)
    {
        var value = Normalize(game);
        if (value.Length == 0)
            return null;
        if (Contains(value, "plutonia"))
            return "plutonia";
        if (Contains(value, "tnt evolution") || Contains(value, "tnt"))
            return "tnt";
        if (Contains(value, "doom ii") || Contains(value, "doom 2") || Contains(value, "doom2"))
            return "doom2";
        if (Contains(value, "ultimate doom") || value == "doom" || Contains(value, "doom 1"))
            return "doom";
        if (Contains(value, "heretic"))
            return "heretic";
        if (Contains(value, "hexen"))
            return "hexen";
        if (Contains(value, "strife"))
            return "strife1";
        return null;
    }

    private static bool MatchesIwad(NativeIwadDefinition definition, string key)
    {
        var values = new[]
        {
            Normalize(Path.GetFileNameWithoutExtension(definition.InternalFileName)),
            Normalize(Path.GetFileNameWithoutExtension(definition.ArchiveFileName)),
            Normalize(definition.Name),
            Normalize(definition.CatalogLabel),
        };
        return key switch
        {
            "doom" => values.Any(value => value == "doom" || Contains(value, "ultimate doom")),
            "doom2" => values.Any(value => value == "doom2" || Contains(value, "doom 2")),
            "strife1" => values.Any(value => value == "strife1" || Contains(value, "strife")),
            _ => values.Any(value => value == key || Contains(value, key)),
        };
    }

    private static int SourcePortScore(string sourceText, string definitionName)
    {
        if (definitionName.Length < 3)
            return 0;
        if (sourceText == definitionName)
            return 1000 + definitionName.Length;
        return Contains(sourceText, definitionName)
            ? 500 + definitionName.Length
            : 0;
    }

    private static bool Contains(string text, string phrase) =>
        (" " + text + " ").Contains(
            " " + phrase + " ",
            StringComparison.Ordinal);

    private static string Normalize(string? value) =>
        WhitespaceRegex().Replace(
            NonAlphaNumericRegex().Replace(
                (value ?? string.Empty).ToLowerInvariant(),
                " "),
            " ").Trim();

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphaNumericRegex();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    private sealed partial class NaturalVersionComparer : IComparer<string>
    {
        public static NaturalVersionComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            var leftParts = VersionParts(left);
            var rightParts = VersionParts(right);
            for (var index = 0; index < Math.Max(leftParts.Length, rightParts.Length); index++)
            {
                var leftValue = index < leftParts.Length ? leftParts[index] : 0;
                var rightValue = index < rightParts.Length ? rightParts[index] : 0;
                var result = leftValue.CompareTo(rightValue);
                if (result != 0)
                    return result;
            }
            return StringComparer.OrdinalIgnoreCase.Compare(left, right);
        }

        private static int[] VersionParts(string? value) =>
            NumberRegex().Matches(value ?? string.Empty)
                .Cast<Match>()
                .Select(match => int.TryParse(match.Value, out var number) ? number : 0)
                .ToArray();

        [GeneratedRegex("\\d+", RegexOptions.CultureInvariant)]
        private static partial Regex NumberRegex();
    }
}
