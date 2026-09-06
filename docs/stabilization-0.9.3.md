# Stabilization beta 0.9.3

This is preparation for 1.0, not a promise that every external engine, archive,
storage device or historical installation has been exhaustively tested.

## Changes by area

1. **Portable updates and data:** schema changes are backed up, transactional and
   revisioned. Migration snapshots include committed SQLite WAL data. File
   replacements are journaled until the operation commits. Release archives no
   longer put theme defaults into the user-owned `Data/Themes` directory.
2. **Import consistency:** local archive inspection supplies exact map counts and
   decoded text to a common import path. Optional artwork is now imported by the
   service, including manual imports. Remote metadata and local map counts are
   saved in one transaction. Generated release dates/provenance retain their
   intentional separate rules.
3. **Launch matching:** matching uses named engine families/executable names and
   natural version ordering. Explicit alternative ordering takes precedence over
   unrelated engines' version numbers. Manual choices are retained; unrecognized
   requirements remain unset.
4. **Generator reliability:** single-level counts, nonempty output and game map
   naming are checked before import. Failed post-processing rolls back generated
   entries/files. Progress is monotonic, cancellation waits for the process to
   exit, and failure logs remain under `Data/Temp/Obsidian`.
5. **UI consistency:** Library and Collections share `GameDetailsPane`, removing
   roughly 290 lines of duplicated XAML. Progress controls share accent defaults.
   Generator choices are translated into English, German, French and Spanish;
   settings are disabled during generation. Blocking progress dialogs cannot be
   dismissed while their operation is still running.
6. **Large libraries:** catalog loading and archive/image processing are moved
   off the UI thread. PNG dimensions are read from the header. Identical fallback
   paths are resolved once per catalog load, avoiding stale cross-load caches.
   Supporting SQLite indexes avoid repeated table scans.
7. **Failure handling:** scan cancellation propagates; unavailable/incompletely
   enumerated Mods directories do not trigger missing-entry cleanup. Cleanup
   operates on a complete inventory and only on managed Mods paths.

## Verification performed on Windows

- Release WinUI build and self-contained publish: successful.
- Modern contract tests, including new schema/file rollback, cancelled migration,
  unavailable directory, map validation, port matching and localization tests.
- Existing integration suite against an isolated copy of a 997-entry library:
  imports, portable packages, metadata, source ports, native launch, session
  statistics, screenshots and legacy migration passed. The source library was
  not rewritten by these tests.
- First-setup and guarded reset contracts passed on temporary fixtures.
- Portable overwrite-update contract passed, including a customized built-in
  theme, custom artwork, database, mods, saves, UI state and installed Obsidian.
- Real Obsidian-Content v21 batch run: one E1M1, extracted artwork, map count,
  release date, Generated tag, IWAD and UZDoom assignment checked.
- Native UI smoke check: executable startup/version, shared Library details,
  generator choices and navigation. This is not an exhaustive DPI/device matrix.
- Synthetic 5,000-entry catalog: about 1.7 seconds before per-load placeholder
  resolution caching, about 50 milliseconds afterward on the test machine.
  This measures catalog loading, not rendering or total application startup.

The default contract suite and first-setup test run in CI and before releases.

## Remaining external limitation

The installed Obsidian-Content v21 executable replaces the requested seed 667
with `calisayas tweeze` during batch initialization. The launcher records the
effective seed and explains the discrepancy; missing seed output is explicitly
unverified. It does not modify or redistribute the user's Obsidian installation.
Exact requested-seed reproducibility is therefore not guaranteed with that build.

Keep a separate backup before upgrading and close the launcher before replacing
program files. Journal rollback covers handled failures/cancellation; it does not
replace backups against storage failures or power loss. This beta does not claim
that every historical beta database has been individually exercised.
