# Personal library, maps, saves and multiplayer

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
remain unknown. Launch a selected map or the next unconfirmed map. There are no
manual completion controls. Only automatically validated engine completions count.

The launcher embeds an open-source ZScript helper and mounts a temporary PK3 on
compatible GZDoom/UZDoom-based single-player launches. The adjacent engine PK3 is
checked for the required event/level API; an engine label alone does not enable it.
The helper requires a genuine transition followed by the engine setting its
completion/visited flag from clear to set. A transition or save-game statistic
alone is not evidence. Normal/secret exits, finales and no-intermission transitions
are supported. Quit, death, restart, loading and console map/changemap do not count.
Hub travel is deliberately unconfirmed, not interpreted as completing a hub map.
Already-set flags in older saves cannot prove a new completion and are not imported.
This is conservative progress tracking, not an anti-cheat system.

After the engine exits, session-token-matched evidence is restricted to maps in
the launched archive (or its IWAD for gameplay-only mods), then stored transactionally.
The archive hash must still match. Manual marks from older versions remain stored
but are excluded from validated progress. No mod or IWAD archive is modified.
The passive helper is not serialized into saves and has a stable versioned name.
Its current launch identity is captured before a save restores server CVars, so
loading an older save cannot replace the identity used for completion reports.
Older saves may still show the engine's changed-resource confirmation; keep backups.
Multiplayer, extra-parameters-only launches, explicit custom logfile arguments,
directory-only entries, missing API support and ambiguous cases remain untracked.
Other engines can be supported by future adapters; no approximate/manual fallback
is used. Nonstandard mods that replace the helper or completion semantics can
remain unconfirmed. The launcher must remain running to import the session.
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

## Optional real-engine tracking regression

With an installed engine and your own legal Doom II IWAD (or ZIP containing it), run:

```powershell
dotnet run --project DoomLauncher.Modern.Tests -c Release -- --verify-map-tracker-regression "C:\Engines\gzdoom.exe" "C:\Games\doom2.wad"
```

Repeat with UZDoom/LZDoom as appropriate. Seven isolated cases cover the six-case
warp/load matrix plus direct `-loadgame` startup. Each imports actual engine logs
into a fresh SQLite database twice. Reports and disposable fixtures are retained
in the printed temporary directory. Engines and IWADs are not bundled with tests;
no existing engine configuration, saves or launcher library are modified.
