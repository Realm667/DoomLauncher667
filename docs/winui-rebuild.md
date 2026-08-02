# Doom Launcher 667 WinUI 3

## Architecture

Version 0.8.4 is a self-contained WinUI 3 client. It uses the established
`DoomLauncher.sqlite` schema as its source of truth but does not require or
start the classic .NET Framework executable.

The normal main-window size is stored in the portable user-state JSON and
restored on the next launch. A `--debug` launch always starts at 1440 × 900
and leaves the saved normal size unchanged. Package import and export are
grouped under the single **Manage package** menu in the shared header.

| Capability | Owner |
| --- | --- |
| Library, Home, Discover, Latest, collections, search and filters | WinUI 3 |
| Managed import and `/idgames` download | WinUI 3 |
| Metadata, settings and finished/favorite state | WinUI 3 + SQLite |
| Source-port, capability and IWAD definitions | WinUI 3 + SQLite |
| Archive resolution and process launch | Native launch service |
| Last played, playtime and screenshot capture/import | Native session service |
| Existing-installation migration, backup and integrity check | Migration service |

The migration assistant is offered when no database exists and remains
available from the header. It copies the existing database and managed content;
the source installation is never modified.

## Source-port capabilities

Each source-port definition stores its executable version, screenshot support,
capture directories, image formats, statistics adapter, save-game directories
and save-game formats. Selecting an executable through Browse reads its product
or file version automatically; the value can still be edited or cleared.

Screenshot support can use the known-directory scanner, explicit directories
or be disabled. Explicit screenshot and save-game directories are optional,
manually monitored additions to the automatically recognized engine locations.
Statistics are adapter-based; the ZDoom-compatible `.zds`/`globals.json`
adapter explicitly reports unsupported ports instead of implying universal
telemetry support.

IWAD definitions store an editable detected version in the WinUI companion
schema. The bundled `Assets/iwad-hashes.json` catalog matches MD5 plus file size
for well-known Doom, Heretic, Hexen, Strife and Chex IWAD releases. Matching
works for direct WADs and WADs inside supported archives. Unknown hashes are
shown transparently and can still receive a manually entered version.

## Debug mode

The Debug navigation item is absent during normal starts. Launch with
`DoomLauncher.WinUI.exe --debug` or use
`Start-DoomLauncher-WinUI-Debug.cmd` in a portable distribution to expose
runtime paths, database diagnostics and backup-first repair, source-port
capability inspection and the visual/audio achievement-notification test.

## Runtime configuration

Portable and test installations can override the default locations:

```powershell
$env:DOOMLAUNCHER_DATABASE = "C:\DoomLauncher667\DoomLauncher.sqlite"
$env:DOOMLAUNCHER_USER_STATE = "C:\DoomLauncher667\Data\UserState\state.json"
$env:DOOMLAUNCHER_DIAGNOSTIC_LOG = "C:\DoomLauncher667\Data\UserState\crash.log"
```

Without `DOOMLAUNCHER_DATABASE`, the migrated database is stored below
`%LocalAppData%\DoomLauncher667`. Unhandled application exceptions are written
below `%LocalAppData%\DoomLauncher.WinUI\Logs` unless the diagnostic path is
overridden.

## Build and publish

Prerequisites:

- Visual Studio 2022 with Windows application development tools;
- .NET 10 SDK;
- Windows 10 1809 or newer;
- Windows App SDK 1.8.

```powershell
dotnet restore DoomLauncher.WinUI/DoomLauncher.WinUI.csproj
dotnet build DoomLauncher.WinUI/DoomLauncher.WinUI.csproj `
  -c Release -p:Platform=x64
dotnet publish DoomLauncher.WinUI/DoomLauncher.WinUI.csproj `
  -c Release -p:Platform=x64 `
  -p:PublishProfile=win-x64-unpackaged
```

Run the isolated regression suite against a real library:

```powershell
dotnet run --project DoomLauncher.Modern.Tests/DoomLauncher.Modern.Tests.csproj `
  -c Release -- `
  "C:\DoomLauncher667\DoomLauncher.sqlite" 980
```

The count is optional. Every mutating test runs against a temporary database
copy and covers import sanitization, settings, launcher definitions, native
process launch, playtime, screenshot import and migration.

## Verification checklist

1. Start with a real database and verify entry count, artwork and navigation.
2. Check the transparent splash, title, visible version and Realm667 colors.
3. Verify grid/list switching and aligned horizontal list scrolling.
4. Create or edit a source port and IWAD in Settings, save without closing,
   and then close the dialog explicitly.
5. Launch a test entry, exit it and verify Last Played and playtime.
6. Create a GZDoom, UZDoom or VKDoom screenshot during play and verify that it
   appears in the detail slider.
7. Import local and `/idgames` metadata containing repeated tabs and verify
   that the stored text contains a single space at each run.
8. Run migration into an empty test directory and verify its automatic backup
   and SQLite integrity result.
