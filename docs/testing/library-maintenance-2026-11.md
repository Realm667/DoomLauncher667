# Library maintenance verification — 18 November 2026

## Baseline and scope

The comparison uses the actual published `v1.0.0` (`d6f1f0a`), not RC14.
The original local `main` was five commits behind that release. All stable 1.0
changes were retained on `codex/library-performance-maintenance`. In particular,
1.0 already fixes collection virtualization and responsive layouts; these are
not new fixes in this maintenance change. Earlier RC14 diagnostic runs are
excluded from the following results.

Three focused changes were selected:

1. Reconcile large Library changes with one native collection notification.
   Keep small edits incremental, unchanged item instances, and an unchanged
   ItemsSource. Publish nothing for an unchanged view.
2. Synchronize native selection when the selected model changes, including the
   catalog reload after a game exits. Retain viewport and keyboard context during
   reconciliation, without refocusing a surviving control or a new dialog.
3. Reuse loaded Collection groups on navigation. Existing data, artwork and sort
   mutation paths continue to rebuild them.

No database schema, launch argument construction, save handling, tracking rules,
archive decoder, SQLite query, dependencies or product features were changed.

## Reproducible native measurements

Both binaries use the same opt-in `MainPage.PerformanceTests.cs` harness. The
reference is a `git archive v1.0.0` with only this partial class and its guarded
call after `InitializeListScrollSynchronization()` added. Clean Release x64
publishes use the project-local SDK and caches.

`deployment/test-library-performance.ps1` prepares marked, independent portable
data roots. Each version uses three processes per size, followed by a new-process
warm restart of the same installation. Three action repetitions per process give
nine samples; import and catalog refresh have one sample per process. Reported
values are the median of all samples, not the median of per-process medians.

Conditions: existing Windows development desktop, local D: storage, native 125%
display scaling, synthetic 65- and 11,000-entry libraries, populated collections
(65/250 members), local synthetic ZIP/PWAD and PNG artwork. Rendering and scroll
observations await two real WinUI render frames after dispatcher/layout work.
There is no concurrent build during the measured series. Windows file caches
are not flushed. “First start” means a new process and a freshly prepared data
root; it does not mean a reboot or an OS-cold cache. Warm starts reuse the root
after the workflow, including the one imported entry.

All values below are milliseconds, rounded to the nearest millisecond.

| Workflow | Samples per version | 65 before | 65 after | 11,000 before | 11,000 after |
|---|---:|---:|---:|---:|---:|
| First process start to rendered Library | 3 | 1,586 | 1,515 | 2,253 | 1,863 |
| Warm process start to rendered Library | 3 | 1,465 | 1,293 | 1,996 | 1,426 |
| Filter application | 9 | 113 | 46 | 164 | 50 |
| Clear filter | 9 | 183 | 67 | 331 | 50 |
| Reverse title sort | 9 | 111 | 83 | 6,141 | 50 |
| Library → Collections → Library | 9 | 716 | 200 | 715 | 167 |
| Selected mod launch choices | 9 | 33 | 34 | 34 | 33 |
| Catalog refresh | 3 | 93 | 67 | 266 | 283 |
| Import archive, reload and reveal Latest | 3 | 881 | 450 | 12,624 | 621 |
| Refresh metadata and local artwork | 9 | 217 | 202 | 355 | 383 |

The filter measurement starts after the existing 350 ms typing debounce; that
debounce is unchanged. Import uses a small deterministic archive and measures
the complete local import/UI tail, not large-archive throughput. Metadata uses a
deterministic local payload and artwork, without network latency. These numbers
do not predict NAS latency or `/idgames` server/download time.

The large metadata refresh and plain catalog refresh did not improve (about
28/18 ms slower in this sample). No speedup is claimed for them or for launch
choices. Catalog reading and archive inspection were already moved off the UI
thread, supporting SQLite indexes already existed, and the new work does not
replace those implementations.

## State and regression evidence

- The initial regression test reproduced 11,000 collection notifications for
  initial publication. The same reconciler implementation existed in stable 1.0.
  The change publishes one; unchanged reloads publish zero. Small removal and
  randomized reorder/subset cases retain the expected references and order.
- The stable native reference loses native list selection after catalog refresh
  in all six full runs. The model has the preferred mod but the list has a
  different item instance. The Play and Enter workflows use this same refresh
  path after session exit. All six candidate runs pass the selection check.
- All six candidate full runs and all six warm starts pass. Full runs check
  actual rendered list/tile containers, search results, complete restoration
  after clearing search, selected identity and scroll position on navigation,
  refreshed native selection and viewport, keyboard context, import and
  collection virtualization. Rendered collection rows stay bounded.
- Collection-group identity changes on every reference navigation (diagnostic
  value 0) and is retained on every candidate navigation (value 1).
- Sorting changes the row order and may move WinUI's visible anchor. Its
  invariant is the selected mod and keyboard context. Pixel offsets are checked
  strictly for refresh and section return, not imposed on a changed ordering.

Raw samples and checks are retained locally in
`outputs/optimization/rendered-before/results.json` and
`outputs/optimization/rendered-after/results.json`, with per-instance
`performance-full.json` and warm `performance-report.json` files. They contain
synthetic data only and are intentionally excluded from Git.

Example candidate reproduction, after a clean publish:

The commands below use the workspace's local SDK wrapper. Other checkouts can
use the standard .NET build documented in the repository; the new test scripts
load the local wrapper only when it is present.

```powershell
. ./Enter-Project.ps1
./Build-Project.ps1 -Offline -Test
./deployment/test-library-performance.ps1 `
  -ApplicationDirectory <clean-publish> `
  -OutputDirectory <new-isolated-output> -KeepLastWindow
```

Use `-RecordFailures` only for the stable reference to retain its expected
selection failure while collecting the remaining measurements.

## Build, UI and portable compatibility

- `Build-Project.ps1 -Offline -Test`: successful; 181 printed console contract
  checks, no build warnings or errors. Linked production service implementations
  cover refresh coordination, rollback, settings recovery, imports, metadata
  locks, IWAD protection, launch/resume arguments and engine-gated tracking.
- Native UI suite: 191 checks pass for both the stable reference and candidate.
  Four languages (`en-US`, `de-DE`, `fr-FR`, `es-ES`) and five requested window
  sizes (800×600 through 1920×1080) are exercised on actual WinUI controls.
  Native moves between three displays report 125%, 125% and 200% scaling.
  Separate 100–200% raster exports are live XAML captures, not additional OS DPI
  configurations. Narrow German and wide Library captures were visually read.
- Version/package, guarded reset and portable overlay-update contracts pass.
  The local package contains no personal database or populated user Data.
- All eight acceptance cases pass: a fresh portable directory and all four
  first-setup scans; original 0.8.1, 0.8.10, 0.9.0, 0.9.3, RC1 and RC15 package
  upgrades with original compiled schemas, protected rows/files and complete
  folder rollback; and the existing 11,000-real-ZIP/12-collection migration
  fixture generator. Original downloads are checked against published SHA-256.
- The large migration retains all 11,000 entries and memberships, preserves its
  source database, reaches last accordion/detail/tile entries and returns to the
  overview. Checked accordions realize 6/917 rows; the detail table realizes
  9/917. The loaded CLR is verified inside the portable package.
- The existing native-write/process integration also passes against a separate
  original-0.8.1-schema fixture: imports, LZMA artwork, definitions, saved launch
  options, a real controlled OS process, playtime, screenshot import and legacy
  migration. This controlled process is not a Doom engine. An exploratory run
  against the already migrated 11,000-entry database was inapplicable to its
  legacy Finished-tag assertion: that test injects legacy data after the modern
  schema revision has already been marked complete. Product migration rules
  were not weakened to accommodate that fixture.

Local evidence:

- `outputs/optimization/final-build-and-tests.log` and
  `final-source-verification.log`.
- `outputs/optimization/native-ui.log` and `reference-native-ui.log`, including
  printed report/visual-qa paths under `.tools/temp`.
- `outputs/optimization/overlay-contract.log`, `reset-contract.log`,
  `version-contract.log` and `native-launch-integration.log`.
- `outputs/optimization/acceptance-matrix/matrix.json` and per-installation
  `release-test-report.json` / runtime-module evidence.

## Comparable visible refresh regression

`deployment/test-refresh-visual.ps1` repeats the same task on both clean builds:
select **Seed 002**, refresh the catalog, return the viewport to the top and
capture the live Library. The stable reference reproduces the expected selection
failure; its selected row is blank while details still show Seed 002. The
candidate renders the row and its selection correctly. Layout, themes and
controls remain those of stable 1.0.

The unmodified native PNGs are retained at
`outputs/optimization/refresh-visible-before/refresh-selection.png` and
`outputs/optimization/refresh-visible-after/refresh-selection.png`. They were
opened and visually compared. Reproduce with `test-refresh-visual.ps1`, adding
`-ExpectSelectionFailure` only for the stable reference.

## Deliberate limits and release assessment

No newly reproduced database-integrity or crash defect justified schema work.
Existing archive inspection, SQLite indexes, collection virtualization, responsive
controls, saved-option resolution, save compatibility and validated-engine
tracking remain intact. The change adds no product functionality or dependency.

A clean Windows installation, live NAS timing and genuine Doom-engine runs were
not performed. The available workspace contains no engine executable, and launch,
save and tracking production code was not changed. Engine-specific regression
commands remain available for a later engine-equipped check. The 350 ms typing
debounce, large-archive throughput and internet latency are not optimized or
certified by the local synthetic measurements.

The candidate is suitable for a focused maintenance release on this evidence:
the dominant large-library UI stalls are removed, selection after refresh is
correct, and preservation/update checks pass without an additional migration.
Normal release review and a new version number would still precede publication.
This task creates local commits and a local test package only; no push, public
release or deployment to Z: is authorized or performed.
