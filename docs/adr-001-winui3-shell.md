# ADR 001: WinUI 3 presentation shell

## Status

Accepted and implemented as the active modernization path. Its transitional
classic-client boundary is superseded by ADR 003.

## Context

The existing application is a .NET Framework 4.8 Windows Forms program. Its
domain behavior, SQLite data access, archive handling, metadata synchronization,
and launch pipeline are valuable, but the main user interface mixes presentation
and behavior in large forms.

The modernization must keep the current application usable while a new
presentation layer is developed and validated.

## Decision

Create `DoomLauncher.WinUI` as a separate, packaged WinUI 3 application on
.NET 10. The first vertical slice covers:

1. browse a game and mod library;
2. search the visible library;
3. select an item;
4. inspect the important launch context;
5. launch through the existing production pipeline;
6. filter recent, downloaded, favorite, and tagged entries;
7. hand complex management workflows to the classic client during migration.

The shell reads the existing library through an `ILibraryCatalog` boundary. Its
first production adapter uses `Microsoft.Data.Sqlite` in read-only mode and maps
the stable `GameFiles`, `IWads`, `SourcePorts`, `Files`, and `Configuration`
tables without referencing Windows Forms controls. Database discovery checks an
explicit `DOOMLAUNCHER_DATABASE` environment variable, the installed application
data directory, and portable application locations.

## UI principles

- Library content is primary; configuration is progressive disclosure.
- A selected item exposes one obvious primary play action.
- Grid and detail views remain usable with keyboard, mouse, scaling, and narrow
  windows.
- WinUI theme resources and semantic design tokens are preferred over hard-coded
  per-control styling.
- Existing Doom artwork provides identity while layout and interaction follow
  current Windows conventions.

## Consequences

- Windows 10 version 1809 or later is required.
- The legacy Windows Forms executable remains available during migration.
- The WinUI shell does not mutate the legacy database. The legacy application
  remains the owner of imports, metadata edits, configuration, migrations, and
  game-session statistics during the transition.
- WinUI-only favorites are stored atomically in
  `%LocalAppData%\DoomLauncher.WinUI\library-state.json`.
- Launching is exposed through a UI-independent `ILaunchService`. Its first
  adapter is the transitional legacy launch bridge described in ADR 002.
- Both MSIX and unpackaged, self-contained x64 publishing are supported.

## Build prerequisites

- .NET 10 SDK
- WinUI C# templates
- Windows App SDK 1.8
- Developer Mode for command-line launch, or Visual Studio with Windows
  application development tools

Build the prototype:

```powershell
dotnet restore DoomLauncher.WinUI/DoomLauncher.WinUI.csproj
dotnet build DoomLauncher.WinUI/DoomLauncher.WinUI.csproj -c Debug -p:Platform=x64
```

To point a development build at a portable or test database:

```powershell
$env:DOOMLAUNCHER_DATABASE = "C:\Path\To\DoomLauncher.sqlite"
```

The adapter opens this database with `Mode=ReadOnly`; it never creates,
migrates, or modifies the legacy database.

For isolated favorite-state testing:

```powershell
$env:DOOMLAUNCHER_USER_STATE = "C:\Temp\DoomLauncher-WinUI-state.json"
```
