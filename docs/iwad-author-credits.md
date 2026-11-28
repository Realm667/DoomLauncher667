# IWAD author correction — 2026-11-28

The Library previously substituted the archive basename for an empty Author.
This was a display fallback, not a switched XAML column or overwritten author.
Unknown credits now display an em dash. All 112 existing fingerprint records
carry researched game-creator credits and a separate authorSource; hashes,
editions and versions are unchanged. These short credits name the original
creator/team, not a complete list of engine, port, publisher or remaster credits.

Existing nonempty Author values take precedence. Explicit Author locks also
protect an intentionally blank value. Catalog credits are projected from the
already loaded IWAD metadata, with no archive reads or per-row SQLite commands.
Imports and saved IWAD definitions fill blank unlocked credits in their existing
transaction. SHA-1-only matches retain credits in the identity label; structural
recognition alone assigns neither author nor release version. No schema change.

| Game | Short creator credit | Evidence |
| --- | --- | --- |
| Doom, Doom II and their historical data variants | id Software | [Official Doom source release](https://github.com/id-Software/DOOM) |
| TNT: Evilution | TeamTNT | IWAD ENDOOM credits; [developer catalog](https://github.com/libretro/libretro-database/blob/master/dat/DOOM.dat) |
| The Plutonia Experiment | Dario Casali; Milo Casali | Original IWAD ENDOOM explicitly names both authors, independently checked in classic, Anthology, Unity and PS3 data |
| Heretic, Hexen, Deathkings | Raven Software | [Raven game list](https://www.ravensoftware.com/our-games); [official Heretic listing](https://store.steampowered.com/app/2390/Heretic_Shadow_of_the_Serpent_Riders/) |
| Strife and Veteran Edition | Rogue Entertainment | [Official game listing](https://store.steampowered.com/app/317040/Strife_Veteran_Edition/) distinguishes developer from publisher |
| Chex Quest; Chex Quest 3 (classic) | Digital Café; Charles Jacobi respectively | [Project history and credits](https://www.chexquest3.com/about/) |
| Hacx; Hacx 2.0 | Banjo Software; Banjo Software and Hacx 2.0 team respectively | [Official credits](https://drnostromo.com/hacx/page.php?content=credits) separate original creators and 2.0 team |
| Harmony | Thomas van der Velden (Rabotik) | [Creator interview](https://slayersclub.bethesda.net/en-AU/news/nods-to-mods-interview-harmony) |
| Delaweare | Roland van der Velden; Delaweare | [Creator's project page](https://spaceisgreen.nl/delaweare.htm) |
| The Adventures of Square | BigBrik Games | [Creator's release page](https://bigbrikgames.itch.io/square) |
| Freedoom / FreeDM; Blasphemer | Project contributors | [Freedoom credits](https://github.com/freedoom/freedoom/blob/master/CREDITS); [Blasphemer credits](https://github.com/Blasphemer/blasphemer/blob/master/CREDITS.md) |
| Rise of the Wool Ball | MSPaintR0cks (WatchDaToast) | [Transcribed game credits](https://www.mobygames.com/game/90491/rise-of-the-wool-ball/credits/windows/) (secondary source; creator's forum page returned HTTP 403) |
| Action Doom 2 | Stephen Browning (Scuba Steve); Action Doom team | [Transcribed game credits](https://www.mobygames.com/game/36200/urban-brawl-action-doom-2/) (secondary source; original site timed out) |

## Existing installation repair

Read-only audit of Z: found 86 registered checksum-identified IWADs: 75 blank
Author values and 11 nonempty values. The isolated copy repair fills 75 blanks
and corrects eight exact known legacy values: five Activision publisher credits
become Raven Software, Plutonia's Team TNT becomes Dario/Milo Casali, TNT's team
name is normalized, and Rogque Entertainment is corrected to Rogue Entertainment.
The three already-correct id Software credits stay unchanged. Author locks are
honored. Ordinary Mod authors and every other database column/table are preserved.
A consistent SQLite backup is taken before the narrowly scoped live transaction.
No archives, titles, collection memberships, ratings or launch settings are edited.

## Verification

The unknown-author regression failed before the fix. It now passes alongside
known credits, read-time fallback, locked blank, manual override and unknown hash
cases. Full offline build and regression suite: 200 PASS lines, RESULT PASS.
Native WinUI: 208 checks passed, including both the unknown dash and known id
Software credit, navigation, narrow layouts and 100/125/150/200% XAML rendering.
Portable overlay preservation, fresh native setup, upgrade from original 1.0.0
(including rollback) and migration of 11,000 archives / 12 collections all passed. Matching native WinUI
screenshots are retained in the local author-correction output directory.
No launch or tracking code is changed; real engine tests are not rerun for this
metadata-only correction. This is a working correction to 1.1.0, not a new public
release or replacement of the published release artifacts.

Live deployment status: not applied. Automatic approval review rejected the
live database write and program overlay, requiring a new explicit user order
for this exact stand. The original live installation remains unchanged.
