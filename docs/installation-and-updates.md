# Installation and portable updates

## Install

1. Download the Windows x64 ZIP and its `.sha256` file from the selected
   [GitHub release](https://github.com/Realm667/DoomLauncher667/releases).
   `rc` means Release Candidate, not the final stable release.
2. In PowerShell, run `Get-FileHash -Algorithm SHA256 -LiteralPath '<downloaded ZIP>'`
   and compare the hash with the attached checksum file. Do not use a mismatching archive.
3. Extract the **complete** archive into a writable folder. Do not launch from
   inside the ZIP. Windows x64 is the distributed target; other architectures
   are not included. A separate .NET installation is not required.
4. Start `DoomLauncher667.exe`. Keep its `WinUI` and `Tools` directories beside it.
5. Use First Setup or the first-launch migration prompt. You can prefill
   `Data/GameWads`, `Data/Sourceports/<port>`, `Data/Mods`, and optionally
   `Data/Tools/Obsidian`. Supply your own game data and source ports.

## Update

1. Close the launcher and any generation task. Back up the **whole portable folder**
   to a separate location before proceeding.
2. Verify the new ZIP. Open its top-level `DoomLauncher667-<version>-win-x64`
   directory (older beta names may also contain `-beta`).
3. Copy that directory's **contents** into the existing installation root, next
   to its existing `DoomLauncher667.exe`. Replace program files when prompted.
   Do not accidentally nest the new package inside the old installation.
4. Start the executable. Needed schema upgrades run automatically and create
   backups under `Backups`. Your database and `Data` remain user-owned; release
   packages do not ship personal databases, mods or editable theme replacements.

**Do not run `DoomLauncher667-reset.cmd` to update.** It deliberately clears the
portable instance after confirmation. `DoomLauncher667-debug.cmd` is for diagnostics,
not normal startup, and can reset window sizing.

## Rollback and portability

Restore the entire pre-update backup into a separate folder if a rollback is
needed. Do not run an older executable against a database upgraded by a newer
release; downgrades are not guaranteed. Automatic schema snapshots are not a
replacement for full backups of archives, artwork and settings.

Move/copy the complete installation to preserve portable data. Definitions
pointing at external files still depend on those files; keep IWADs, source ports
and mods within the corresponding `Data` folders for an independent installation.

The RC package checks update preservation, but does not certify every historical
beta database or a clean Windows/device compatibility matrix. See its release notes.
