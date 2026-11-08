# Backup, restore and duplicate checks

Open **Settings → Backup, restore & duplicates**. Finish any running game,
generation or import first. These tools operate on the current portable installation.

## Back up the installation

Choose **Back up installation** and save the ZIP outside the installation folder.
By default, it includes program files, a consistent SQLite snapshot, settings,
collections, IWADs, source ports, artwork, screenshots, saves and mod archives.
The **Include mod archives** checkbox can exclude `Data/Mods`; the manifest records
that omission and restoration displays a reminder to copy those archives separately.

`Backups`, temporary files and the installation lock are excluded to avoid recursive
copies. Links to external files/folders are rejected rather than silently following
them. Files referenced outside the installation are not copied. Keep an independent
backup of those external files or put them into the portable Data folders first.

Every file is recorded with its size and SHA-256 hash. After creation, the ZIP is
read back and verified before replacing the chosen backup file. Cancellation or
failure leaves any previous backup at that destination intact. Large installations
need enough disk space and time for both writing and verification.

## Restore

Choose **Restore backup into an empty folder**, select the backup ZIP, then choose
an empty folder separate from the running installation. Existing installations
are never overwritten by this operation. The tool validates archive paths, sizes,
hashes and the restored database, and adapts installation-contained absolute paths
to the new location. References outside the original installation remain external.

Start `DoomLauncher667.exe` in the restored folder and verify your library before
retiring another installation. A backup excluding Mods requires the original
`Data/Mods` archives as well. Do not merge two personal databases by copying one
over the other.

## Find duplicate mod files

Choose **Find duplicate mod files** to compare supported archives and WAD files in
the configured managed Mods folder. Files of different sizes are excluded quickly;
remaining candidates are compared using SHA-256. Renamed byte-identical files are
grouped with their paths and potential space savings. Select a row to open its folder.

The tool does not delete files or merge database entries. Different ZIP compression,
different versions or different metadata can produce different file hashes even
when some playable content overlaps. Such files are not reported as identical.

## NAS and concurrent use

An installation-wide file-sharing lock prevents another cooperating launcher
process from opening the same installation, including UNC/drive-letter access.
The lock file remains on disk; the operating system releases the open handle when
the launcher exits. Its presence alone does not mean an installation is locked.

Keep storage available while the launcher is running. This guard and transactional
writes do not make SQLite a distributed database or replace backups. Do not edit
the same database concurrently using other applications or older launcher versions.

## Keyboard and navigation

- **Ctrl+F:** focus search.
- **Ctrl+I:** import one or more files.
- **Enter** on a focused library list/tile item: launch it.
- **Alt+Left:** return to the previous navigation section.
- **Escape:** clear search, or return when search is empty; open dialogs/flyouts
  retain their normal Escape handling.

Search, sort, filters, selected item and scroll offsets are retained when navigating
between sections during the session. Successful imports explicitly open Latest at
the top. Launch definitions can be selected directly from the mod details and saved.
