# 1.0 release acceptance

## Scope

The final acceptance combines console contracts, first setup, guarded reset,
version/package checks, data-preserving overlay updates, native UI workflows,
historical compiled schemas, whole-folder rollback and large legacy migration.
All fixtures are marked isolated installations. User databases and archives are
never uploaded or used as writable test fixtures.

## Reproduce

1. Restore/build the projects and package version 1.0.0.
2. Run `deployment/test-native-ui.ps1 -PackageDirectory <package>`.
3. Download the original releases with `deployment/get-test-releases.ps1`.
4. Run `deployment/test-release-acceptance.ps1 -PackageDirectory <package>
   -HistoricalDirectory <downloads> -OutputDirectory <new-empty-output>`.

The historical matrix covers 0.8.1, 0.8.10, 0.9.0, 0.9.3, RC1 and RC15.
An additional empty-folder case drives the actual first-launch prompt and all
four native setup scans through their Next/Finish dialogs and verifies that the
completion marker persists. It is a fresh application installation, not a clean OS.
Each old ZIP is checked against its published SHA-256. Its original shipped
database and original compiled schema create a representative installation with
synthetic metadata, a collection, completion state, mod/IWAD archives, a save,
custom placeholder artwork, a theme and personal notes. The new files are copied
over it; the new native application starts and exits normally. Existing table
columns/values and protected files are compared, SQLite integrity is checked,
and the complete pre-update folder backup is restored separately and verified.
Where supported by the original version, fixtures also contain personal stars,
Play later order, map progress, metadata locks and history. All original WinUI
user tables are included; only schema revisions and workflow markers are excluded.
The expected `Data/GameFiles` to `Data` path migration is checked explicitly.
Only exact hashes of known obsolete 320x200 shipped placeholders may be removed
from the old root or replaced with current defaults; custom images must retain
their original hashes. New schema/default rows may be added, but original user
rows and fields must remain present (apart from the specified path migration).
This is not a claim to have tested every historical user's individual database.

The large-migration fixture contains 11,000 real ZIP archives and 12 collections.
The production migration copies it into a separate portable installation. Native
checks open accordions, scroll to their last rows, switch detail table/tile views
and return to the overview. They assert finite row viewports and fewer than 200
realized rows per tested list, rather than only timing a database query.
The original reporter's private databases were not supplied; reporter-side
confirmation remains useful even after the reproduced scaling defect is fixed.

Native source-port regression checks exercise the actual Setup Save button with
an external executable path, rename the definition and change its version,
verify the database, and retain the inline warning without nested modal dialogs.

`matrix.json`, per-instance `release-test-report.json`, runtime-module evidence
and the native UI/visual reports are written to the printed fixture directories.
The recorded runtime module path confirms use of the bundled .NET runtime;
it does not prove a clean operating-system installation.

## Explicit remaining test gap

**No clean Windows 10/11 test was performed.** No clean VM or separate test PC
was available. The project owner explicitly approved 1.0 publication with this
gap disclosed. Development-machine and GitHub Windows-runner checks do not
certify fresh-OS prerequisites, every device/driver or every Windows edition.
No system security settings or Windows features were changed to hide this gap.

The download targets Windows x64. Prefer a Windows edition supported by its
vendor and by the bundled runtime; see Microsoft's
[.NET Windows support guidance](https://learn.microsoft.com/en-us/dotnet/core/install/windows).
The EXEs are unsigned. Commercial IWADs, engines and Obsidian are not bundled.
Some tested Obsidian-Content v21 builds replace requested seeds. Unsupported
engines do not receive approximate/manual completion tracking.

## Recorded local results — 2026-11-12

- Console, first-setup service, reset, version/package and overlay-update contracts passed.
- 191 native UI checks passed, including actual external source-port creation,
  renaming and version changes, viewport/locale/theme/focus regressions and raster exports.
- Fresh empty-folder setup, six original-package upgrades/rollbacks and the
  11,000-archive / 12-collection migration passed. Only 6/917 accordion rows and
  7/917 detail rows were realized in the measured large-library fixture;
  end-of-list access and table/tile navigation passed.
- RC15 personal ratings, queue position, progress, metadata locks and history
  survived both upgrade and whole-folder rollback.
- Real GZDoom, UZDoom and LZDoom regressions passed all seven cases per engine:
  warp, changemap, save/load without exit, genuine exit after load, loading a
  previous-process save without exit, genuine exit after that resume, and direct
  `-loadgame` startup followed by a genuine exit. Production event import and
  repeat-import idempotence were checked in each case (21/21 total).
- A private read-only snapshot of the existing user database passed additive
  schema/value preservation and SQLite integrity checks. It is not a public artifact.
- Clean Windows 10/11: **not performed**, explicitly retained as the release gap above.
