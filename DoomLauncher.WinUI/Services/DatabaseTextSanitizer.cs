using System.Text;

namespace DoomLauncher.WinUI.Services;

internal static class DatabaseTextSanitizer
{
    public static string SingleLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var result = new StringBuilder(value.Length);
        var previousWasSpace = true;
        foreach (var character in value)
        {
            var isSpace = char.IsWhiteSpace(character) || char.IsControl(character);
            if (isSpace)
            {
                if (!previousWasSpace)
                    result.Append(' ');
                previousWasSpace = true;
                continue;
            }

            result.Append(character);
            previousWasSpace = false;
        }
        return result.ToString().Trim();
    }

    public static string Multiline(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        value = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\t', ' ');
        var lines = value
            .Split('\n')
            .Select(SingleLine)
            .ToArray();
        return string.Join(Environment.NewLine, lines).Trim();
    }
}
