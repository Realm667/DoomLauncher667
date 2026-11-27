# IWAD hash catalog

`DoomLauncher.WinUI/Assets/iwad-hashes.json` is a local, portable recognition
catalog. Matching uses both MD5 and the exact uncompressed WAD size. MD5 is
used only as a compatibility identifier for known historical game data, never
for a security decision.

The catalog now also includes checksum-verified Doom Alpha 0.2–0.5, the
press-release beta, and registered Doom 1.1, 1.2, 1.666 and 1.8 from Debian's
`game-data-packager` 67 `data/doom.yaml`. There are 31 distinct content hashes.
Strife 1.2–1.31 and several Veteran packages share the same base IWAD; that hash
does not identify the complete package or engine version.

When the Library's **IWADs** filter is selected, **Version** is shown between
Title and Author. It uses the entry's own IWAD metadata, not its selected launch
IWAD. Unknown versions remain blank. Collection column preferences are unchanged.

Importing while the **IWADs** filter is selected uses a separate IWAD import.
The managed archive is named after its WAD, verified version (or `unknown`) and
a short content hash, for example `doom_v1_666_54978d12.zip`. Existing archives
are never overwritten; identical WAD contents reuse their existing definition.
ZIP companions are retained. Multi-IWAD packages must first be split, preventing
ambiguous same-name entry selection. Existing archive names are retained to
preserve saved launch and savegame references.

The initial catalog was cross-checked against:

- Debian `game-data-packager`, whose purpose includes identifying
  user-supplied commercial game data by checksums:
  <https://wiki.debian.org/Games/GameDataPackager>
- Debian's maintained Doom data definitions:
  <https://sources.debian.org/src/game-data-packager/67/data/doom.yaml/>
- Debian's maintained Heretic data definitions:
  <https://sources.debian.org/src/game-data-packager/73/data/heretic.yaml/>
- The HHexen project's documented Hexen and Deathkings checksums:
  <https://code.nephatrine.net/QuakeArchive/hhexen/src/branch/master/README>

Unknown hashes are deliberately not guessed. The UI displays the calculated
hash and leaves the version editable, so newer rereleases can be added to the
catalog without blocking a portable IWAD definition.
