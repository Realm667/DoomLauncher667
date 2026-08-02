# IWAD hash catalog

`DoomLauncher.WinUI/Assets/iwad-hashes.json` is a local, portable recognition
catalog. Matching uses both MD5 and the exact uncompressed WAD size. MD5 is
used only as a compatibility identifier for known historical game data, never
for a security decision.

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
