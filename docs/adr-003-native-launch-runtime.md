# ADR 003: Native launch runtime

## Status

Accepted for version 0.5 Beta. Supersedes ADR 002.

## Context

The transitional WinUI shell delegated game starts and post-session work to
the classic Windows Forms executable. That prevented a clean standalone
runtime and required two application stacks to remain installed.

## Decision

The WinUI application owns the complete launch session:

1. resolve request overrides, saved game settings, profiles and configured
   defaults;
2. resolve and extract IWAD and mod archives, including ZIP, 7z and RAR;
3. construct source-port arguments for regular, DEH/BEX, map, skill and extra
   parameters;
4. start and monitor the source-port process directly;
5. record LastPlayed and accumulated MinutesPlayed;
6. discover screenshots in configured and GZDoom-family locations and attach
   them to the library entry;
7. remove the temporary per-session extraction directory.

Source ports and IWADs are managed directly through native Settings dialogs.
A separate migration service imports existing installations with an
automatic database backup, portable path rewriting and `PRAGMA integrity_check`.

## Consequences

- The distributed runtime no longer contains or calls `DoomLauncher.exe`.
- The existing database remains compatible and is still the single source of
  truth.
- Process launch and post-session behavior have isolated integration coverage.
- Future launch behavior changes must be implemented in the native service and
  accompanied by regression tests.
