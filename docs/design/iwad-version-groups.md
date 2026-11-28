# Expandable IWAD version groups

Status: implemented for the IWAD Library list. Grouping is enabled by default and
can be disabled with the tree toggle beside the sort selector. Its preference and
expanded edition keys are stored in portable user state; no SQLite schema change.

Collapsed:

    > Doom Registered          1.9       5 versions      id Software
    > The Ultimate Doom        1.9       3 versions      id Software

Expanded (example, subject to the installed entries):

    v Doom Registered          1.9       5 versions      id Software
        1.9  latest
        1.8
        1.666
        1.2
        1.1

The version remains small and gray after the title. A chevron and version count
make older variants discoverable. Single-version games retain their ordinary row.
The collapsed header always shows the newest *verified comparable* release.

Group by curated catalog identity/edition, not filename, custom title or shared
version string. Registered Doom and Ultimate Doom remain distinct. TNT and Plutonia
remain distinct. Shareware/demo, full game, alpha/beta and BFG/Unity/KEX editions
also remain separate, unless a future explicit game-family parent is requested.
Unknown hashes and unrelated Mods are not automatically merged. A custom title
remains attached to its concrete entry; the group heading uses the canonical game
name. Any future Mod grouping requires explicit reliable identity or user assignment.

Release order needs a curated chronological rank per edition or verified release
dates. A lexical/numeric/ordinary semantic version comparison is insufficient:
Doom 1.666 precedes 1.8 and 1.9. Unordered/unknown variants are labelled accordingly,
never silently promoted to latest. Build channels and platform variants are not
assumed interchangeable. No guessed versions or fabricated release dates.

Selecting the group row selects its latest concrete release. Selecting a child
selects that exact installation. Launch, resume, saves, collections, ratings and
launch options continue to use its existing GameFileID/IWadID. No database entries
or archives are merged, renamed or deleted; saves are never transferred automatically.
Collapsing alone only changes presentation and preserves the active child in the
details pane. If an older selected child is hidden, the highlighted group also
shows 'Selected: 1.8', and the launch action names that selected version. Clicking
the group title explicitly selects the newest release; the chevron never does.

Persist expansion by group key and selection/scroll anchor by stable entry ID.
Right/Left expands/collapses; Up/Down visits visible rows; Enter selects/launches
according to the existing launcher keyboard conventions. Searching keeps a group
when one child matches, reveals matching children temporarily, and restores the
previous expansion when the query is cleared. Counts should distinguish matching
from total versions. Filtering must not silently replace the active launch target.

The implementation flattens group headers and visible children into the existing
virtualized ListView. There are no nested ListViews/Expanders containing full collections.
Verify selection, scroll/focus, keyboard, sorting/search, narrow windows, high DPI,
German labels and 11,000 entries before enabling the optional 'Group versions' view.

Ordering coverage: 59 fingerprint records have explicit per-edition release ranks.
Registered Doom 1.666 precedes 1.8 and 1.9. Unranked, unknown-version or structurally
identified-only entries remain ordinary separate rows. This deliberately avoids
guessing chronology for different binaries merely sharing an edition label.
Search may be folded temporarily with the chevron, without changing stored
expansion preferences. Group roots sort by their displayed canonical title (or
selected sort field), while children remain in verified newest-first order.
