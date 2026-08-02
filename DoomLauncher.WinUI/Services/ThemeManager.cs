using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DoomLauncher.WinUI.Services;

public sealed record LauncherThemeInfo(
    string Id,
    string Name,
    string BaseMode,
    string FilePath);

public static class ThemeManager
{
    private const double MinimumTextContrastRatio = 4.5;
    private const string ThemeDirectoryEnvironmentVariable =
        "DOOMLAUNCHER_THEME_DIRECTORY";
    private const string DatabaseEnvironmentVariable =
        "DOOMLAUNCHER_DATABASE";

    public static IReadOnlyList<LauncherThemeInfo> GetAvailableThemes()
    {
        return LoadDefinitions()
            .OrderBy(theme => theme.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(theme => theme.Id, StringComparer.OrdinalIgnoreCase)
            .Select(theme => new LauncherThemeInfo(
                theme.Id,
                theme.Name,
                theme.BaseMode,
                theme.FilePath))
            .ToArray();
    }

    public static void Apply(FrameworkElement root, string theme)
    {
        var definitions = LoadDefinitions();
        var selected = definitions.FirstOrDefault(definition =>
                definition.Id.Equals(theme, StringComparison.OrdinalIgnoreCase))
            ?? definitions.FirstOrDefault(definition =>
                definition.Id.Equals("Dark", StringComparison.OrdinalIgnoreCase))
            ?? definitions.FirstOrDefault();
        if (selected is null)
        {
            root.RequestedTheme = ElementTheme.Dark;
            return;
        }

        root.RequestedTheme = selected.BaseMode.Equals(
            "Light",
            StringComparison.OrdinalIgnoreCase)
            ? ElementTheme.Light
            : ElementTheme.Dark;
        ApplyPalette(selected.Colors);
        ApplyScopedControlPalette(root.Resources, selected.Colors);
    }

    private static IReadOnlyList<ThemeDefinition> LoadDefinitions()
    {
        EnsureDefaultThemeFiles();
        return Directory
            .EnumerateFiles(
                GetThemeDirectory(),
                "*.xml",
                SearchOption.TopDirectoryOnly)
            .Select(TryLoadDefinition)
            .Where(definition => definition is not null)
            .Cast<ThemeDefinition>()
            .ToArray();
    }

    public static string GetThemeDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(
            ThemeDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(
                    configured.Trim().Trim('"')));
        }

        var database = Environment.GetEnvironmentVariable(
            DatabaseEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(database))
        {
            var databasePath = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(
                    database.Trim().Trim('"')));
            return Path.Combine(
                Path.GetDirectoryName(databasePath)!,
                "Data",
                "Themes");
        }

        var applicationDirectory = AppContext.BaseDirectory
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var portableRoot = Path.GetFileName(applicationDirectory).Equals(
            "WinUI",
            StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(applicationDirectory)!
            : applicationDirectory;
        return Path.Combine(portableRoot, "Data", "Themes");
    }

    private static void EnsureDefaultThemeFiles()
    {
        var destination = GetThemeDirectory();
        Directory.CreateDirectory(destination);
        var source = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Themes");
        if (!Directory.Exists(source))
            return;
        foreach (var file in Directory.EnumerateFiles(
                     source,
                     "*.xml",
                     SearchOption.TopDirectoryOnly))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            if (!File.Exists(target))
                File.Copy(file, target, overwrite: false);
        }
        var readmeSource = Path.Combine(source, "README.md");
        var readmeTarget = Path.Combine(destination, "README.md");
        if (File.Exists(readmeSource) && !File.Exists(readmeTarget))
            File.Copy(readmeSource, readmeTarget, overwrite: false);
    }

    private static ThemeDefinition? TryLoadDefinition(string path)
    {
        try
        {
            return LoadDefinition(path);
        }
        catch (Exception exception)
            when (exception is IOException
                or UnauthorizedAccessException
                or System.Xml.XmlException
                or FormatException
                or InvalidDataException)
        {
            return null;
        }
    }

    private static ThemeDefinition LoadDefinition(string path)
    {
        var root = XDocument.Load(path).Root
            ?? throw new InvalidDataException("The theme XML has no root element.");
        if (!root.Name.LocalName.Equals(
                "DoomLauncherTheme",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The root element must be DoomLauncherTheme.");
        }

        var id = RequiredAttribute(root, "id");
        var name = RequiredAttribute(root, "name");
        var baseMode = RequiredAttribute(root, "baseMode");
        if (baseMode is not ("Dark" or "Light"))
            throw new InvalidDataException("baseMode must be Dark or Light.");
        var colors = root.Element("Colors")
            ?? throw new InvalidDataException("The Colors element is missing.");

        return new ThemeDefinition(
            id,
            name,
            baseMode,
            Path.GetFullPath(path),
            new Palette(
                RequiredColor(colors, "Background"),
                RequiredColor(colors, "Surface"),
                RequiredColor(colors, "ElevatedSurface"),
                RequiredColor(colors, "Sidebar"),
                RequiredColor(colors, "PrimaryText"),
                RequiredColor(colors, "SecondaryText"),
                RequiredColor(colors, "Accent"),
                RequiredColor(colors, "SecondaryAccent"),
                RequiredColor(colors, "ControlAccent"),
                RequiredColor(colors, "Border"),
                RequiredColor(colors, "Success"),
                RequiredColor(colors, "Warning")));
    }

    private static string RequiredAttribute(XElement element, string name)
    {
        var value = element.Attribute(name)?.Value.Trim();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException(
                $"The required attribute '{name}' is missing.")
            : value;
    }

    private static Color RequiredColor(XElement colors, string name)
    {
        var value = colors.Element(name)?.Value.Trim();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException(
                $"The required color '{name}' is missing.")
            : ColorFromHex(value);
    }

    private static void ApplyPalette(Palette palette)
    {
        var accentForeground = ContrastForeground(
            palette.Accent,
            palette.PrimaryText);
        var controlAccentForeground = ContrastForeground(
            palette.ControlAccent,
            palette.PrimaryText);
        SetBrush("DoomPageBackgroundBrush", palette.Background);
        SetBrush("DoomSurfaceBrush", palette.Surface);
        SetBrush("DoomSurfaceElevatedBrush", palette.ElevatedSurface);
        SetBrush("DoomSidebarBrush", palette.Sidebar);
        SetBrush("NavigationViewDefaultPaneBackground", palette.Sidebar);
        SetBrush("NavigationViewExpandedPaneBackground", palette.Sidebar);
        SetBrush("TextFillColorPrimaryBrush", palette.PrimaryText);
        SetBrush("TextFillColorSecondaryBrush", palette.SecondaryText);
        SetBrush("DoomAccentBrush", palette.Accent);
        SetBrush("DoomAccentForegroundBrush", accentForeground);
        SetBrush("DoomAccentSecondaryBrush", palette.SecondaryAccent);
        SetBrush("DoomControlAccentBrush", palette.ControlAccent);
        SetBrush(
            "DoomControlAccentForegroundBrush",
            controlAccentForeground);
        SetBrush("DoomSuccessBrush", palette.Success);
        SetBrush("DoomWarningBrush", palette.Warning);
        SetBrush("DoomStrokeBrush", palette.Border);
        SetBrush("DoomSubtleStrokeBrush", WithAlpha(palette.Border, 32));
        SetBrush("DoomControlAccentLowBrush", WithAlpha(palette.ControlAccent, 51));
        SetBrush("DoomControlAccentMediumBrush", WithAlpha(palette.ControlAccent, 68));
        SetBrush("DoomControlAccentStrongBrush", WithAlpha(palette.ControlAccent, 221));
        SetBrush("DoomSuccessLowBrush", WithAlpha(palette.Success, 36));
        var resources = Application.Current.Resources;
        resources["DoomControlAccentLowColor"] =
            WithAlpha(palette.ControlAccent, 51);
        resources["DoomControlAccentMediumColor"] =
            WithAlpha(palette.ControlAccent, 68);
        resources["DoomSuccessColor"] = palette.Success;

        var selectionHover = Scale(palette.ControlAccent, 0.84);
        var selectionPressed = Scale(palette.ControlAccent, 0.70);
        SetBrush("AccentFillColorDefaultBrush", palette.ControlAccent);
        SetBrush("AccentFillColorSecondaryBrush", selectionHover);
        SetBrush("AccentFillColorTertiaryBrush", selectionPressed);
        SetBrush("AccentTextFillColorPrimaryBrush", palette.ControlAccent);
        SetBrush("TextOnAccentFillColorPrimaryBrush", controlAccentForeground);
        SetBrush(
            "TextOnAccentFillColorSecondaryBrush",
            WithAlpha(controlAccentForeground, 230));
        SetBrush(
            "TextOnAccentFillColorDisabledBrush",
            WithAlpha(controlAccentForeground, 160));
        SetBrush("ToggleButtonBackgroundChecked", palette.ControlAccent);
        SetBrush("ToggleButtonBackgroundCheckedPointerOver", selectionHover);
        SetBrush("ToggleButtonBackgroundCheckedPressed", selectionPressed);
        SetBrush("ToggleButtonForegroundChecked", controlAccentForeground);
        SetBrush(
            "ToggleButtonForegroundCheckedPointerOver",
            ContrastForeground(selectionHover, palette.PrimaryText));
        SetBrush(
            "ToggleButtonForegroundCheckedPressed",
            ContrastForeground(selectionPressed, palette.PrimaryText));
        SetBrush("AppBarToggleButtonForegroundChecked", controlAccentForeground);
        SetBrush(
            "AppBarToggleButtonForegroundCheckedPointerOver",
            ContrastForeground(selectionHover, palette.PrimaryText));
        SetBrush(
            "AppBarToggleButtonForegroundCheckedPressed",
            ContrastForeground(selectionPressed, palette.PrimaryText));
        SetBrush("ToggleSwitchFillOn", palette.ControlAccent);
        SetBrush("ToggleSwitchFillOnPointerOver", selectionHover);
        SetBrush("ToggleSwitchFillOnPressed", selectionPressed);
        SetBrush(
            "NavigationViewSelectionIndicatorForeground",
            palette.ControlAccent);
        SetBrush(
            "NavigationViewItemBackgroundSelected",
            WithAlpha(palette.ControlAccent, 46));
        SetBrush(
            "NavigationViewItemBackgroundSelectedPointerOver",
            WithAlpha(palette.ControlAccent, 64));
        SetBrush(
            "NavigationViewItemBackgroundSelectedPressed",
            WithAlpha(palette.ControlAccent, 82));
        SetBrush(
            "NavigationViewItemForegroundSelected",
            palette.PrimaryText);
        SetBrush(
            "NavigationViewItemForegroundSelectedPointerOver",
            palette.PrimaryText);
        SetBrush(
            "NavigationViewItemForegroundSelectedPressed",
            palette.PrimaryText);
        SetBrush("ListViewItemSelectionIndicatorBrush", palette.ControlAccent);
        SetBrush(
            "ListViewItemSelectionIndicatorPointerOverBrush",
            palette.ControlAccent);
        SetBrush(
            "ListViewItemSelectionIndicatorPressedBrush",
            selectionPressed);
        SetBrush(
            "ListViewItemBackgroundSelected",
            WithAlpha(palette.ControlAccent, 46));
        SetBrush(
            "ListViewItemBackgroundSelectedPointerOver",
            WithAlpha(palette.ControlAccent, 64));
        SetBrush(
            "ListViewItemBackgroundSelectedPressed",
            WithAlpha(palette.ControlAccent, 82));
        SetBrush("ListViewItemForegroundSelected", palette.PrimaryText);
        SetBrush(
            "ListViewItemForegroundSelectedPointerOver",
            palette.PrimaryText);
        SetBrush("SliderThumbBackground", palette.ControlAccent);
        SetBrush("SliderThumbBackgroundPointerOver", selectionHover);
        SetBrush("SliderThumbBackgroundPressed", selectionPressed);
        SetBrush("SliderTrackValueFill", palette.ControlAccent);
        SetBrush("SliderTrackValueFillPointerOver", selectionHover);
        SetBrush("SliderTrackValueFillPressed", selectionPressed);
        SetBrush("AccentButtonBackground", palette.ControlAccent);
        SetBrush("AccentButtonBackgroundPointerOver", selectionHover);
        SetBrush("AccentButtonBackgroundPressed", selectionPressed);
        SetBrush("AccentButtonForeground", controlAccentForeground);
        SetBrush(
            "AccentButtonForegroundPointerOver",
            ContrastForeground(selectionHover, palette.PrimaryText));
        SetBrush(
            "AccentButtonForegroundPressed",
            ContrastForeground(selectionPressed, palette.PrimaryText));
        SetBrush(
            "CheckBoxCheckGlyphForegroundChecked",
            controlAccentForeground);
        SetBrush(
            "CheckBoxCheckGlyphForegroundCheckedPointerOver",
            ContrastForeground(selectionHover, palette.PrimaryText));
        SetBrush(
            "CheckBoxCheckGlyphForegroundCheckedPressed",
            ContrastForeground(selectionPressed, palette.PrimaryText));
        SetBrush(
            "ComboBoxItemSelectedBackgroundThemeBrush",
            palette.ControlAccent);
        SetBrush(
            "ComboBoxItemSelectedPointerOverBackgroundThemeBrush",
            selectionHover);
        SetBrush(
            "ComboBoxItemSelectedForegroundThemeBrush",
            controlAccentForeground);
        SetBrush(
            "ComboBoxItemBackgroundSelected",
            WithAlpha(palette.ControlAccent, 55));
        SetBrush(
            "ComboBoxItemBackgroundSelectedUnfocused",
            WithAlpha(palette.ControlAccent, 40));
        SetBrush(
            "ComboBoxItemBackgroundSelectedPointerOver",
            WithAlpha(palette.ControlAccent, 72));
        SetBrush(
            "ComboBoxItemBackgroundSelectedPressed",
            WithAlpha(palette.ControlAccent, 88));
        SetBrush("ComboBoxItemBorderBrushSelected", palette.ControlAccent);
        SetBrush(
            "ComboBoxItemBorderBrushSelectedPointerOver",
            palette.ControlAccent);
        SetBrush("ComboBoxItemPillFillBrush", palette.ControlAccent);
        SetBrush(
            "AccentFillColorSelectedTextBackgroundBrush",
            palette.ControlAccent);
        SetBrush("TextControlSelectionHighlightColor", palette.ControlAccent);
        SetBrush("TextSelectionHighlightColorThemeBrush", palette.ControlAccent);
        SetBrush("SystemControlHighlightAccentBrush", palette.ControlAccent);
        SetBrush(
            "SystemControlHighlightListAccentLowBrush",
            WithAlpha(palette.ControlAccent, 72));
        SetBrush("ProgressBarIndicatorForeground", palette.ControlAccent);
        SetBrush("ProgressRingForeground", palette.ControlAccent);
        SetBrush("TextControlBorderBrushFocused", palette.ControlAccent);

        resources["SystemAccentColor"] = palette.ControlAccent;
        resources["SystemAccentColorDark1"] = selectionHover;
        resources["SystemAccentColorDark2"] = selectionPressed;
        resources["SystemAccentColorLight1"] = Scale(palette.ControlAccent, 1.16);
        resources["SystemAccentColorLight2"] = Scale(palette.ControlAccent, 1.30);
    }

    private static void SetBrush(string key, Color color)
    {
        SetBrush(Application.Current.Resources, key, color);
    }

    private static void ApplyScopedControlPalette(
        ResourceDictionary resources,
        Palette palette)
    {
        var hover = Scale(palette.ControlAccent, 0.84);
        var pressed = Scale(palette.ControlAccent, 0.70);
        var foreground = ContrastForeground(
            palette.ControlAccent,
            palette.PrimaryText);
        foreach (var key in new[]
                 {
                     "AccentButtonForeground",
                     "ToggleButtonForegroundChecked",
                     "AppBarToggleButtonForegroundChecked",
                     "CheckBoxCheckGlyphForegroundChecked",
                     "TextOnAccentFillColorPrimaryBrush",
                 })
        {
            SetBrush(resources, key, foreground);
        }
        SetBrush(resources, "AccentFillColorDefaultBrush", palette.ControlAccent);
        SetBrush(resources, "AccentFillColorSecondaryBrush", hover);
        SetBrush(resources, "AccentFillColorTertiaryBrush", pressed);
        SetBrush(resources, "NavigationViewSelectionIndicatorForeground",
            palette.ControlAccent);
        SetBrush(resources, "NavigationViewItemBackgroundSelected",
            WithAlpha(palette.ControlAccent, 46));
        SetBrush(resources, "NavigationViewItemBackgroundSelectedPointerOver",
            WithAlpha(palette.ControlAccent, 64));
        SetBrush(resources, "NavigationViewItemBackgroundSelectedPressed",
            WithAlpha(palette.ControlAccent, 82));
        SetBrush(resources, "NavigationViewItemForegroundSelected",
            palette.PrimaryText);
        SetBrush(resources, "NavigationViewItemForegroundSelectedPointerOver",
            palette.PrimaryText);
        SetBrush(resources, "NavigationViewItemForegroundSelectedPressed",
            palette.PrimaryText);
        SetBrush(resources, "ListViewItemBackgroundSelected",
            WithAlpha(palette.ControlAccent, 46));
        SetBrush(resources, "ListViewItemBackgroundSelectedPointerOver",
            WithAlpha(palette.ControlAccent, 64));
        SetBrush(resources, "ListViewItemBackgroundSelectedPressed",
            WithAlpha(palette.ControlAccent, 82));
        SetBrush(resources, "ListViewItemSelectionIndicatorBrush",
            palette.ControlAccent);
        SetBrush(resources, "ComboBoxItemSelectedBackgroundThemeBrush",
            palette.ControlAccent);
        SetBrush(resources, "ComboBoxItemSelectedPointerOverBackgroundThemeBrush",
            hover);
        SetBrush(resources, "ComboBoxItemSelectedForegroundThemeBrush",
            foreground);
        SetBrush(resources, "ComboBoxItemPillFillBrush",
            palette.ControlAccent);
        SetBrush(resources, "AccentFillColorSelectedTextBackgroundBrush",
            palette.ControlAccent);
        SetBrush(resources, "TextControlSelectionHighlightColor",
            palette.ControlAccent);
        SetBrush(resources, "TextSelectionHighlightColorThemeBrush",
            palette.ControlAccent);
    }

    private static void SetBrush(
        ResourceDictionary resources,
        string key,
        Color color)
    {
        if (resources.TryGetValue(key, out var value)
            && value is SolidColorBrush brush)
        {
            try
            {
                brush.Color = color;
                return;
            }
            catch (UnauthorizedAccessException)
            {
                // Some WinUI theme-dictionary brushes are immutable. Replacing
                // the resource keeps custom XML themes compatible with them.
            }
        }
        resources[key] = new SolidColorBrush(color);
    }

    private static Color ColorFromHex(string hex)
    {
        var value = hex.TrimStart('#');
        if (value.Length is not (6 or 8)
            || !value.All(Uri.IsHexDigit))
        {
            throw new FormatException(
                $"'{hex}' is not a valid #RRGGBB or #AARRGGBB color.");
        }
        var offset = value.Length == 8 ? 2 : 0;
        var alpha = value.Length == 8
            ? Convert.ToByte(value[..2], 16)
            : (byte)255;
        return Color.FromArgb(
            alpha,
            Convert.ToByte(value.Substring(offset, 2), 16),
            Convert.ToByte(value.Substring(offset + 2, 2), 16),
            Convert.ToByte(value.Substring(offset + 4, 2), 16));
    }

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Scale(Color color, double factor) =>
        Color.FromArgb(
            color.A,
            checked((byte)Math.Clamp(Math.Round(color.R * factor), 0, 255)),
            checked((byte)Math.Clamp(Math.Round(color.G * factor), 0, 255)),
            checked((byte)Math.Clamp(Math.Round(color.B * factor), 0, 255)));

    private static Color ContrastForeground(
        Color background,
        Color preferred)
    {
        if (ContrastRatio(background, preferred)
            >= MinimumTextContrastRatio)
        {
            return Color.FromArgb(
                255,
                preferred.R,
                preferred.G,
                preferred.B);
        }

        var black = Microsoft.UI.Colors.Black;
        var white = Microsoft.UI.Colors.White;
        return ContrastRatio(background, black)
            >= ContrastRatio(background, white)
            ? black
            : white;
    }

    private static double ContrastRatio(Color first, Color second)
    {
        var firstLuminance = RelativeLuminance(first);
        var secondLuminance = RelativeLuminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05)
            / (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    private static double RelativeLuminance(Color color) =>
        0.2126 * LinearChannel(color.R)
        + 0.7152 * LinearChannel(color.G)
        + 0.0722 * LinearChannel(color.B);

    private static double LinearChannel(byte channel)
    {
        var normalized = channel / 255d;
        return normalized <= 0.04045
            ? normalized / 12.92
            : Math.Pow((normalized + 0.055) / 1.055, 2.4);
    }

    private sealed record ThemeDefinition(
        string Id,
        string Name,
        string BaseMode,
        string FilePath,
        Palette Colors);

    private sealed record Palette(
        Color Background,
        Color Surface,
        Color ElevatedSurface,
        Color Sidebar,
        Color PrimaryText,
        Color SecondaryText,
        Color Accent,
        Color SecondaryAccent,
        Color ControlAccent,
        Color Border,
        Color Success,
        Color Warning);
}
