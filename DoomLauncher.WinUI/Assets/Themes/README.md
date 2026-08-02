# Doom Launcher 667 themes

The launcher reads every `*.xml` file in this directory when the theme
selection is opened and when a theme is applied. Copy an existing file, give
it a unique `id` and `name`, then change its colors. Restarting the launcher is
not required before the new file appears in Settings.

The root attributes are:

- `id`: stable identifier stored in the user settings; keep it unique.
- `name`: label displayed in Settings.
- `baseMode`: `Dark` or `Light`; selects the matching WinUI control behavior.
- `order`: optional numeric position in the theme list.

All colors accept `#RRGGBB` or `#AARRGGBB`:

- `Background`: main page background.
- `Surface`: cards, lists and dialog surfaces.
- `ElevatedSurface`: selected or elevated surfaces.
- `Sidebar`: navigation pane and Windows title bar.
- `PrimaryText`: primary foreground.
- `SecondaryText`: muted metadata foreground.
- `Accent`: decorative accent such as favorites and highlights.
- `SecondaryAccent`: secondary decorative accent.
- `ControlAccent`: buttons, selection, toggles, sliders and focus.
- `Border`: borders and separators.
- `Success`: finished, success and positive status states.
- `Warning`: warning and attention states.

Hover, pressed, subtle-border and translucent variants are derived
automatically. This keeps custom themes compact and visually consistent.
Text placed on `Accent` or `ControlAccent` is also derived automatically.
The launcher keeps the configured primary text where it reaches the WCAG
contrast threshold of 4.5:1; otherwise it chooses black or white, whichever
has the stronger contrast. Custom themes therefore remain readable without
adding separate button-text colors.
