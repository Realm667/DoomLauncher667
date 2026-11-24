# Local archive refresh verification — 24 November 2026

## Reproduced case

An existing Venturous entry retained its old outer ZIP filename while the ZIP
was replaced with a single `Venturous_1.7.2.pk3` payload (712,709,150 bytes).
The database still stored eight maps and had no linked /idgames ID.

Two independent problems prevented a refresh:

- The existing action required an /idgames match before inspecting local data.
- Map extraction skipped embedded archives above its 256 MiB memory limit, and
  TITLEPIC extraction did not descend into a single-PK3 ZIP container.

The synthetic regression was first run against the original readers and failed
with `Large single-PK3 ZIP exposes its maps without the 256 MB memory cutoff`.
The fixture wraps a PK3 larger than 256 MiB, avoiding a large checked-in asset.

## Changes

- Unlinked mods first offer **Read local archive**, **Find /idgames entry**, and
  **Cancel**. The local action needs no network lookup. A failed lookup or no
  matches also offers the local path; the existing /idgames workflow remains.
- Single-archive wrappers are spooled to uniquely named temporary files with a
  4 GiB payload limit and at most two wrapper layers. Readers retain their
  existing embedded-WAD memory limits. Temporary payloads are removed on success,
  cancellation and failure. Archive decoding failures use the existing
  `InvalidDataException` contract so batch import still reports corrupt inputs.
- Local refresh updates only available text metadata and the detected map list,
  honoring the existing metadata locks. Missing readme values preserve current
  values. Existing metadata undo history is used; no /idgames link is invented.
- Existing TITLEPIC import updates artwork. A missing TITLEPIC retains the
  current artwork. The mod archive is neither downloaded nor replaced.
- IWAD checks and metadata-field validation are shared by local and /idgames
  refreshes. No database schema, launch argument, save or tracking changes.

## Verification

- `./Build-Project.ps1 -Offline -Test` passes, with no build errors or warnings.
  New checks cover large wrappers, artwork, depth limits and cleanup, a replaced
  ZIP with the same name, locked fields, absent readme values, undo, corrupt input,
  cancellation, IWAD protection, and preservation of personal/launch data.
- 197 native UI checks pass. They include cancelling the new dialog and invoking
  its real primary button against a replaced fixture ZIP: maps update, archive
  bytes and selection remain unchanged, and Library input is restored.
- The narrow German dialog was captured from the live WinUI control, checked for
  viewport bounds, and visually inspected. Its three actions fit without clipped
  labels. Existing language, viewport, theme and display-area checks still pass.
- A consistent SQLite backup and the real updated ZIP were copied into an
  explicitly marked isolated D: directory. Its configured game directory was
  rebased before calling any mutation service. Refresh changes **8 → 29 maps**
  and imports a new TITLEPIC. The archive SHA-256 remains unchanged. Protected
  columns of the target entry, other entries, launch definitions, personal ratings,
  queue, collection membership, progress, /idgames links and non-artwork file
  associations remain unchanged.

Final local evidence (excluded from Git):

- `outputs/venturous-refresh-20261124/regression-before.log`
- `outputs/venturous-refresh-20261124/build-tests.log`
- `outputs/venturous-refresh-20261124/native-ui.log`
- `outputs/venturous-refresh-20261124/real-copy.log` and
  `real-copy/local-refresh-report.json`
- Native UI report under
  `.tools/temp/DoomLauncher-native-ui-7c54abd10083414ebd123a1283433fef/`, including
  `visual-qa/local-refresh-de-DE-800.png`.

The standalone default suite skips real-database integration without arguments;
the isolated real-copy check above supplements that gap. Genuine engine runs were
not repeated: launch and tracking production code were not changed. No mod files,
user database or generated artwork are committed. Z: was only read to prepare the
isolated copy; no hotfix deployment, live metadata write, push or public release
was performed as part of this fix.
