# Personal library, maps, saves, multiplayer and synchronization

## Ratings, queue, random choice and bulk editing

**My rating** sets personal 1–5 stars; clear to leave unrated. Community ratings
remain unchanged. **Add to Play later** adds a mod to the sidebar queue. Library
tools → Play later changes order and optional auto-removal when finished per mod.

Library tools → **What should I play?** filters by unplayed, maximum map count
(0 = unlimited), minimum personal rating, IWAD, source port and collection. The
result opens in Library; no automatic launch occurs. **Edit multiple mods** retains
selection while filtering, then previews launch definitions, finished state and
collection changes. Unchanged fields retain individual values; a failure rolls
back the entire batch. This is not a bulk archive deletion tool.

## Metadata protection and history

Refresh metadata & artwork previews old/new /idgames values. Uncheck fields to
retain them, protect fields to block future /idgames updates. Metadata history &
locks also edits these locks. Manual edits remain possible. History records manual
and /idgames metadata changes. Undo requires the current entry to match that change’s
result, preventing overwrite of a newer edit. It restores metadata and its previous
/idgames association, not artwork, launch settings, stars, playtime or archives.
The newest 200 records are displayed; older records remain in the database.

## Maps & progress

Read WAD maps directly or inside supported archives, including classic Doom,
Hexen-format and UDMF geometry. Opt in to automap previews if you accept spoilers.
Counts describe standard placements/sector secrets; scripts, custom actors,
difficulty and multiplayer filters change runtime totals. Non-Doom actor counts
remain unknown. Tick maps manually, launch selected or the next unfinished map.
Visited-map statistics do not prove completion and never silently finish a map.
Duplicate map names in alternative embedded WADs share progress identity; normal
engine load-order rules still determine the geometry played.

## Managed saves

Recognized executable names: gzdoom, uzdoom, zdoom, lzdoom, qzdoom, vkdoom, zandronum.
Supported saves are ZIP-based .zds containers with info.json and optional savepic.png.
Other formats are not converted. Directory mods retain their old save behavior.
Supported file-based launches receive Data/SaveGames/archive-SHA256/engine-prefix
via -savedir. Existing engine saves are not moved: explicitly **Import saves** to
assign them to a mod and mark their original environment unverified.

Before each managed session, existing saves/sidecars are snapshotted to
Data/SaveGameBackups. New/changed saves record archive, IWAD/load-stack and exact
engine fingerprints. This protects the pre-session state, not each quicksave made
within one session. Resume shows compatibility information and requires confirmation.
It prefers the recorded executable checksum when configured, otherwise the assigned
or default engine. Confirmation cannot make incompatible saves compatible. Restore
verifies hashes before replacement, backs up current targets and retains unrelated
saves. Keep backups before updating mods and engines.

## Multiplayer

The sidebar browser queries Zandronum internet servers and LAN broadcasts on the
selected UDP port. Filter responding servers by name, map, mode or IWAD. Select a
configured Zandronum and local legal IWAD; files are matched in server order with
MD5 where supplied. Missing hashes are reported. Review versions before joining.

Missing mods can be downloaded after confirmation from trusted HTTPS URLs or selected
/idgames search results. Available server checksums must match before import.
No base-game/IWAD downloads. Supply legal IWADs yourself. Archives are imported as
data, never executed. Do not trust unfamiliar executable-containing archives.

LAN hosting/direct IPv4 or DNS connections support recognized ZDoom-family engines.
All players need the same IWAD, mods and compatible engine. Host/direct mod order
follows the displayed library order. Password entry happens in the engine; nothing
is stored here. No firewall/router/UPnP changes and no internet host advertising
occur. Internet reachability remains user-managed.

## Cross-PC synchronization

Library tools → Synchronization on both PCs: choose the same shared parent folder.
DoomLauncher667-Sync stores a versioned manifest and optional immutable save objects.
Never share the active SQLite database through a cloud client. Close games first.
Appearance and saves are optional, off by default. Initial archive hashing can take
time; later runs cache identity by size/mtime. Recalculate checksums if external tools
changed contents while retaining timestamps.

Preview compares local, remote and last-synced fields. Unilateral changes merge;
simultaneous edits require explicit decisions. Ratings, favorites, finished state,
individual maps, queue, collections and metadata locks sync by archive SHA-256, not
numeric database IDs. Byte-identical duplicate entries are rejected as ambiguous.
Missing mods are reported and retained remotely. Archives, IWADs and engine paths
are never copied. Saves are verified and backed up before replacement; deletions
are not propagated. Optional theme names/language/density/columns synchronize, not
theme files, device paths or window geometry. Restart to apply appearance changes.

Manifests are atomically replaced, with previous revisions retained in Versions.
For recovery, close both launchers, back up the sync folder, copy a chosen Versions
JSON to state.json, reopen and review the preview. Immutable save objects are retained
for recovery; do not remove referenced objects. Missing/unavailable targets cause an
error, not an empty-library reset. Resolve external cloud-client conflict copies first.
