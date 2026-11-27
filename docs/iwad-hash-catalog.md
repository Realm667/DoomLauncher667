# IWAD identities and versions

The portable catalogs are `DoomLauncher.WinUI/Assets/iwad-hashes.json` and
`iwad-identities.json`. They contain **112 distinct content fingerprints** and
all **45 ordered IWad definitions** from UZDoom **5.0.3**. The search aliases
combine its Names block with every IWADName, including square.pk3 and square1.pk3.

Checksums identify the exact edition and, where documented, its data-file version.
Renaming doom.wad cannot turn Doom Registered 1.9 into The Ultimate Doom 1.9.
MD5 and SHA-1 are historical compatibility identifiers, not security checks.
Published SHA-1-only entries also work when the owner supplies the exact file.
Known MD5 entries additionally retain their uncompressed size. All 31 previously
supported fingerprints remain present.

UZDoom's ordered MustContain rules identify editions by internal lump names,
including known standalone PWAD games. A generic MAP01 PWAD is not an IWAD.
PK3/IPK3 packages are identified from their contents or root IWADINFO. Their
contents remain intact inside a managed ZIP. Unknown hashes, generated WadSmoosh
packages and content-only matches receive no invented version number. IWADINFO
names are package metadata, not a checksum-verified release claim.

The IWAD Library filter shows identity information in small gray text after the
title, with the full text in its tooltip. The launch-option and definition selectors
show the same detected edition and version while retaining custom aliases and IDs.
Existing titles, archive references, collections and launch settings are preserved.
The same labels are used in Edit Entry. Content-recognized IWADs are excluded
from Mod metadata updates even when an old entry has not been registered as an IWAD.
Catalog metadata is loaded once per process; ordinary Mod rows do not read archives
or perform per-row database queries to show these labels. No schema change is needed.

Explicit IWAD imports use separate content-qualified names, for example
`doom_v1_666_54978d12.zip`. Existing files are never overwritten. Identical
contents reuse the existing definition. Same-name multi-IWAD packages are rejected;
extract individual payloads into separate archives first. ZIP companions are kept.
Deathkings and Strife Veteran components require a registered full base game; their
launch uses the base as -iwad and the component as -file, including renamed bases.

Recognition does not prove playability. Historical alphas/betas and 2025 Heretic /
Hexen KEX data may need different engines. UZDoom 5.0.3 changed logfile naming;
automatic map progress is withheld for that version and newer unvalidated UZDoom
versions. Launching and existing stored progress remain available. API validation
remains required for the older supported engines.

Sources (catalog entries retain individual source URLs):

- [UZDoom 5.0.3 IWAD definitions and names](https://github.com/UZDoom/UZDoom/blob/5.0.3/wadsrc_extra/static/iwadinfo.txt)
- [Doom-Utils published SHA-1 tables](https://github.com/Doom-Utils/iwad-patches/tree/471ec9be0babbb7b5dcb3aff696c2802b95955a2)
- [Libretro DOOM.dat](https://github.com/libretro/libretro-database/blob/master/dat/DOOM.dat): only primary game payloads; music, voices, resource packs and ordinary add-ons are excluded.
- [Debian Doom definitions](https://sources.debian.org/src/game-data-packager/67/data/doom.yaml/)
- [Debian Heretic definitions](https://sources.debian.org/src/game-data-packager/73/data/heretic.yaml/)
- [HHexen historical checksum references](https://code.nephatrine.net/QuakeArchive/hhexen/src/branch/master/README)
- [UZDoom 5.0.3 logfile implementation](https://github.com/UZDoom/UZDoom/blob/5.0.3/src/common/console/c_enginecmds.cpp)

Pinned downloaded research inputs and local content crosschecks are retained only
in the development outputs directory; commercial IWADs are never distributed.
