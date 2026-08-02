# ADR 002: Legacy launch bridge

## Status

Superseded by ADR 003 in version 0.5 Beta.

## Context

Launching a DoomLauncher library item is not a single process invocation. The
existing pipeline resolves profiles, IWADs and source-port flavors, extracts
archive entries, handles savegames and statistics, updates playtime, and imports
files created during a session. Reimplementing only part of this behavior in the
new shell would create incompatible launch results.

The classic executable already supported the command-line contract
`-LaunchGameFileID <id> -AutoClose`, which is also used by shortcuts created by
the application. The rebuild extends that contract with explicit profile,
source-port, IWAD, edit, and settings arguments.

## Decision

Expose the primary WinUI action through `ILaunchService`. The first adapter,
`LegacyDoomLauncherLaunchService`, starts the classic executable with the
selected `GameFileID` and `AutoClose` flag. The classic application remains the
owner of profile resolution, process lifetime, database updates, and post-launch
processing.

The complete launch contract is:

```text
-LaunchGameFileID <id>
[-LaunchGameProfileID <id> | -LaunchDefaultProfile]
[-LaunchSourcePortID <id>]
[-LaunchIWadID <id>]
-AutoClose
```

Management handoff additionally supports:

```text
-EditGameFileID <id>
-OpenSettings
<file-path-to-import>
```

The launch result exposes an `IGameLaunchSession`. WinUI waits for the classic
launcher process to exit without blocking the UI thread, prevents duplicate
starts while the session is active, and then reloads the read-only catalog. This
reflects the playtime and `LastPlayed` values written by the classic pipeline.

Launcher discovery checks:

1. `DOOMLAUNCHER_LEGACY_EXE`;
2. the selected database directory;
3. the WinUI application and current directories;
4. the conventional `Program Files\Doom Launcher` locations.

Before launch, the adapter verifies that the classic executable will resolve the
same `DoomLauncher.sqlite` as the WinUI catalog. It rejects mismatched portable
and installed locations instead of launching the wrong `GameFileID`.

## Consequences

- WinUI can launch real library items without duplicating the mature pipeline.
- The bridge opens no console window; the classic launcher minimizes itself
  while `AutoClose` is active and closes after the game session.
- The selected library item is restored by `GameFileID` after the catalog
  refresh, so replacing immutable presentation models does not lose selection.
- A classic DoomLauncher installation remains required during this phase.
- Import, edit, and advanced settings open directly at the requested classic
  workflow instead of forcing the user to navigate there manually.
- Errors are presented in the WinUI shell and do not crash the application.
- A future native launch adapter can replace the bridge behind the same
  interface after domain and archive services have been extracted from the
  .NET Framework application.

For a portable development setup:

```powershell
$env:DOOMLAUNCHER_DATABASE = "C:\DoomLauncher\DoomLauncher.sqlite"
$env:DOOMLAUNCHER_LEGACY_EXE = "C:\DoomLauncher\DoomLauncher.exe"
```
