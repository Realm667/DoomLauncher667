# IWAD editions and portable updates — 1.1.0

The implementation keeps all prior source work and user IDs, titles, aliases,
archive references and launch options. The IWAD Library filter uses a small gray
inline identity suffix and the original eleven-column layout. Ordinary Mods do
not inherit the selected launch IWAD's version. No schema migration is added.

Catalog coverage: 112 distinct fingerprints; all 45 UZDoom 5.0.3 IWad definitions;
both the Names block and each definition's IWADName. All 31 prior fingerprints
are retained. See [catalog provenance and limits](../iwad-hash-catalog.md).
Unknown hashes/content-only matches never receive invented numeric versions.

The shared scanner and explicit importer reject ambiguous basenames, preserve
raw PK3/IPK3 payloads, deduplicate identical contents and roll back late dependency
failures. Large archive payloads spool to bounded temporary files. Setup does not
create unprotected Mod rows for duplicate IWAD copies. Deathkings and Veteran
components require full base games, also when their files have been renamed.

## Reproductions

Load `. ./Enter-Project.ps1`, then run `./Build-Project.ps1 -Offline -Test`.
Run `dotnet run --project DoomLauncher.Modern.Tests -c Release -p:Platform=x64 --
--verify-iwad-versions` for the focused identity/import/updater checks.

Failing reproductions were retained before their fixes: duplicate setup imports,
ambiguous setup payload names, renamed IPK3 recognition and Veteran import without
a base game. After correction, the same regressions pass. Additional checks cover
45 ordered rule fixtures, all 31 prior fingerprints, Registered/Ultimate 1.9,
alias/ID preservation, raw-package byte identity and production launch arguments.

The updater tests use fake HTTP responses but the real Windows PowerShell helper:
verified download, exact-version skip persistence, malformed settings, rejected
SHA-256/traversal/Data payloads, successful overlays and rollback after a partial
copy. Native tests render the actual three-button German dialog using a synthetic
release offer. That screenshot is not proof of a public future-update transaction.

## Real-engine evidence

An isolated official UZDoom 5.0.3 download starts six scenarios through the actual
NativeGameLaunchService: Registered Doom, Ultimate Doom, Doom II, Deathkings,
Strife Veteran and Square. Each logs a loaded world and normal exit; stored launch
options remain unchanged. Engine settings and all data stay in isolated D: folders.

The first 5.0.3 tracking regression failed because the engine no longer creates the
requested logfile path. Its source confirms the changed naming/sanitization.
Automatic tracking is now blocked by the capability gate for that version and
newer unvalidated UZDoom releases. The actual 5.0.3 executable is rejected and the
previously supported 5.0.0-rc.1 executable remains accepted. The latter passes all
seven real warp/changemap/save/load/resume/direct-load cases, including repeat
SQLite imports and negative completion checks. No existing progress is deleted.

## Release acceptance

Run deployment/test-native-ui.ps1 against the packaged application, then
 deployment/test-portable-update.ps1 and deployment/test-release-acceptance.ps1.
The native suite checks realized Run styling, column count, IWAD/Mod filter return,
exact edition labels and stable selection IDs. Existing tests cover navigation,
selection/scroll, keyboard, narrow layouts, scaling, language and theme workflows.
Screenshots are actual WinUI renders, not static XAML inspection.

Local evidence is retained under outputs/iwad-library-20261127 (excluded from Git).
The earlier column and current inline screenshots use the same native fixture;
there is no claimed interactive screenshot comparison against stable 1.0 itself.
The final package is also checked against original historical release packages and
the 11,000-archive migration fixture before publication. Clean-OS installation and
full playability of every recognized IWAD remain untested.

The isolated second-wave collection import reaches 86 definitions from 31 existing
ones and 55 new checksum-confirmed contents. Its verifier checks existing database
rows, original IWAD ZIP SHA-256 values and existing Data file lengths/timestamps.
Original G: source archives are read-only and matched to the original inventory.
The live Z: installation is updated only after this isolated preservation check.

For deployment/rollback, retain the database backup and program backup. A network
share outage can prevent rollback too; the helper reports this for manual recovery.
No execution-policy bypass, new application dependencies or public game data are
introduced.
