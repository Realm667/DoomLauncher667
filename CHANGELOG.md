# Changelog

## 0.8.1 - 2026-08-02

- The main-window size is now stored in the portable user-state file and
  restored on the next normal launch. Debug launches deliberately use the
  safe 1440 × 900 default and do not overwrite the saved normal size.
- XML themes are listed alphabetically by display name. The obsolete `order`
  attribute was removed from all bundled themes and from the theme format.
- The separate package import and export buttons were consolidated into one
  localized **Manage package** menu.
- Startup state is read once during the splash screen and reused by the main
  page; the duplicate second disk read was removed.
- Theme discovery and validation now share one implementation for both the
  settings list and theme application, eliminating divergent loading paths.
- Added persistence coverage for window dimensions and updated the portable
  runtime and reset distributions to version 0.8.1.

## 0.8 - 2026-08-02

- First official open-source release of Doom Launcher 667 under the Realm667
  organization.
- Includes the complete WinUI 3 source, portable launcher services, migration
  and setup workflows, /idgames integration, collections, achievements,
  package import/export, themes, localization, and project-owned assets.
- Reproducible publish output and framework binaries are no longer tracked in
  source control; release builds are generated from the checked-in projects.
- This public version consolidates the previous internal beta development
  history through `1.24 Beta` under the official `0.8` version line.

## 1.24 Beta - 2026-07-30

- Downloads von `/idgames`, normale Dateiimporte und Paketimporte behalten
  jetzt den ursprünglichen Archivnamen. Temporäre GUID- und
  `DoomLauncher-`-Präfixe können nicht mehr in `Data\Mods` gelangen.
- Bei Dateinamenskonflikten wird für jede betroffene Datei ausdrücklich
  zwischen „Überschreiben“ und „Überspringen“ gewählt. Überschreiben
  aktualisiert den älteren Bibliothekseintrag, statt einen zweiten Eintrag
  anzulegen.
- Der Paketexport bietet eine granulare Inhaltsauswahl für allgemeine
  Metadaten, individuelle Metadaten, Screenshots, Titel Artwork und
  Collection-Informationen; das Modarchiv bleibt der obligatorische Kern.
- Das Paketformat wurde auf Version 3 erweitert und kennzeichnet die
  tatsächlich enthaltenen Inhaltskategorien.
- Der Paketimport liest vor dem Dialog das Manifest ein und bietet nur die
  tatsächlich vorhandenen Kategorien zur Auswahl.
- Bereits durch den alten Paketimport erzeugte Dateinamensduplikate können
  sicher auf den jeweils älteren Eintrag konsolidiert werden; dabei werden
  Collection-Zuordnungen übertragen und künstliche Dateipräfixe bereinigt.
- Die reale Testbibliothek wurde nach einem vollständigen SQLite-Backup
  von 13 eindeutig erkannten Duplikaten bereinigt und enthält wieder 989
  Einträge.

## 1.23 Beta - 2026-07-30

- `/idgames`-Archive mit LZMA-komprimierten ZIP-Einträgen werden nun über
  SharpCompress gelesen; TITLEPIC-Extraktion und Metadatenaktualisierung
  scheitern deshalb nicht mehr an dieser Kompressionsmethode.
- Klassische Doom-Titelbilder mit 320 × 200 Pixeln werden mit der
  ursprünglichen nicht-quadratischen Pixelgeometrie auf 4:3 dargestellt,
  statt links und rechts beschnitten zu werden. Normale Artworks und
  Screenshots behalten ihr bisheriges Crop-Verhalten.
- Der Paketexport führt nun durch zwei Schritte: Einzelmod, Mehrfachauswahl
  oder komplette Sammlung sowie persönliches Backup oder bereinigtes
  Verteilungspaket.
- Sammlungsexporte enthalten Sammlungsname, Zuordnungen und Artwork.
  Persönliche Backups enthalten zusätzlich Favoriten, Abschlussstatus,
  Spielzeit und letzte Nutzung; bereinigte Pakete lassen diese Nutzerdaten
  konsequent aus.
- Beim Paketimport lassen sich persönliche Metadaten, Screenshots,
  Titelbild sowie Sammlung und Sammlungszuordnung unabhängig auswählen.
- Ein bereinigtes Paket kann beim Import keine bereits vorhandenen
  persönlichen Werte versehentlich zurücksetzen.

## 1.22 Beta - 2026-07-29

- Die Mod-Detailansicht ist jetzt auch in der Kachelansicht einer geöffneten
  Sammlung dauerhaft rechts sichtbar und entspricht funktional der
  Bibliotheksansicht.
- Klicks auf Mods in „Collections“ bleiben vollständig im
  Sammlungsbereich: Einträge aus der Accordion-Übersicht öffnen ihre Sammlung,
  Kachel- und Listenauswahl aktualisieren dort direkt den Detailbereich.
- Die bisherige Aktion „Improve metadata from /idgames“ heißt nun kurz und
  aussagekräftig „Refresh metadata & artwork“ beziehungsweise
  „Metadaten & Titelbild aktualisieren“.
- Ein lokalisierter Tooltip erklärt, dass Titel, Autor, Beschreibung,
  Veröffentlichungsdatum und Bewertung aus `/idgames` aktualisiert werden und
  ein vorhandenes `TITLEPIC` als Titelbild extrahiert wird; lokale Moddatei,
  Spielzeit und Nutzerstatus bleiben erhalten.

## 1.21 Beta - 2026-07-29

- Die letzten systemseitigen violetten Auswahlfarben wurden beseitigt:
  ComboBox-Auswahlindikator und Textmarkierung verwenden nun in Hauptfenster,
  Flyouts und Dialogen konsequent `ControlAccent` des aktiven XML-Themes.
- Im Dialog „Eintrag bearbeiten“ lassen sich Titelbilder und einzelne
  Screenshots jetzt löschen; die Datenbankverknüpfungen und verwalteten
  Mediendateien werden dabei gemeinsam bereinigt.
- „Sammlungen verwalten“ befindet sich in der Mod-Detailansicht jetzt direkt
  unter „Eintrag bearbeiten“ und besitzt ein eindeutiges Sammlungs-Icon.
- Der direkte Play-Button übergibt bewusst weder Map noch Schwierigkeitsgrad,
  sodass der Source Port im Hauptmenü startet. Map und Skill bleiben weiterhin
  ausschließlich über die Startoptionen auswählbar.
- Das nur per Debug-Kommandozeile sichtbare Debug-Menü kann jetzt alle
  Bibliothekseinträge mit verknüpfter `/idgames`-ID erneut vollständig
  aktualisieren. Dabei werden Titel, Autor, Beschreibung, Veröffentlichungsdatum
  und Bewertung aktualisiert sowie ein lokales `TITLEPIC` als Titelbild
  extrahiert, sofern vorhanden. Fortschritt und Fehlerzahlen werden angezeigt.

## 1.20 Beta - 2026-07-29

### Added

- Accent- und Buttontexte werden anhand des WCAG-Kontrastverhältnisses
  geprüft. Unter 4,5:1 wählt der Launcher automatisch Schwarz oder Weiß mit
  dem besseren Kontrast.
- Unter `Settings > Appearance` lässt sich die Höhe der Collection-Accordions
  unabhängig von den Tabellenzeilen auf Normal, Compact oder Ultra Compact
  einstellen.
- In der Collection-Detailansicht kann ein vorhandenes Artwork nun ersetzt
  oder vollständig entfernt werden.

### Changed

- Der Bereich „Launcher Definitions“ heißt jetzt kompakt und verständlich
  „Setup“ (lokalisiert in allen unterstützten Sprachen).
- `TileImages`, `CollectionArtworks` und der portable UI-Zustand liegen jetzt
  vollständig unter `Data`. Alte Verzeichnisse und Artwork-Referenzen werden
  beim Start automatisch migriert.
- IWADs verwenden ein zugeordnetes Title Artwork vorrangig vor den
  generischen `TileImages`.
- In der Setup-Liste wird die Source-Port-Version mit 50 Prozent Deckkraft
  gegenüber dem Namen zurückgenommen.
- Der Artwork-Button auf Collection-Kacheln wird nur noch angezeigt, solange
  kein eigenes Artwork zugewiesen ist.
- Sämtliche WinUI-Auswahlzustände, einschließlich ComboBox-Popups, beziehen
  ihre Farbe aus `ControlAccent`; der historische violette Systemakzent wird
  nicht mehr verwendet.
- Eigene XML-Theme-IDs bleiben jetzt auch nach dem Speichern und Neustart
  erhalten und fließen in die Theme-Achievements ein.

### Fixed

- Das Ersetzen eines Collection-Artworks aktualisiert die Anzeige sofort; ein
  in WinUI zwischengespeichertes Bild mit identischer URI kann nicht mehr den
  alten Stand festhalten.
- Nicht lesbare dunkle Schrift auf dunklen oder gesättigten Accent-Flächen in
  benutzerdefinierten Light-Themes wurde behoben.

## 1.19 Beta - 2026-07-29

### Changed

- Die portable Datenstruktur wurde vereinfacht: Alle bisherigen Unterordner
  von `Data\GameFiles` liegen jetzt direkt unter `Data`.
- Beim ersten Start migriert der Launcher vorhandene Dateien, die
  Verzeichniskonfiguration und Source-Port-Pfade automatisch auf das neue
  Layout. Relative Mod-, IWAD- und Medienreferenzen bleiben dabei erhalten.
- Neue Installationen, die Ersteinrichtung, die Legacy-Migration und die
  Reset-Instanz verwenden unmittelbar `Data\Mods`, `Data\GameWads`,
  `Data\Sourceports`, `Data\Screenshots` und die weiteren direkten
  Unterverzeichnisse.
- Farbschemata werden nicht mehr als C#-Paletten gepflegt. Die sechs
  mitgelieferten Themes liegen als editierbare XML-Dateien in `Data\Themes`.
- Eigene `*.xml`-Themes werden dynamisch in der Theme-Auswahl angezeigt.
  Zwölf verständliche semantische Farbwerte decken Flächen, Texte, Akzente,
  Auswahl, Rahmen, Erfolg und Warnungen ab; Zustandsvarianten werden daraus
  automatisch berechnet.
- Das Theme-Achievement berücksichtigt die jeweils tatsächlich verfügbaren
  XML-Themes statt einer fest codierten Anzahl.
- Der IWAD-Scan führt veraltete direkte WAD-Verweise sicher mit den
  zugehörigen Archivdatensätzen zusammen. Spielzeit, Medien und weitere
  Nutzerdaten bleiben erhalten; leere Scanreste fehlender Archive werden
  bereinigt.

## 1.18 Beta - 2026-07-29

### Changed

- Die Hauptnavigation ist jetzt in der Reihenfolge Home, Library, Collections, Discover, Trennlinie, Favorites, Recently Played, Latest, Trennlinie und Achievements organisiert.
- „Launcher Definitions“ wurde vollständig aus dem Settings-Dialog entfernt und als eigener lokalisierter Sidebar-Eintrag mit Werkzeug-Symbol direkt unter „Settings“ platziert.
- Der Settings-Dialog besitzt nun die beiden klar getrennten Tabs „General“ und „Appearance“ im selben segmentierten Design wie die Launcher-Definitionen.
- Bibliotheksverzeichnis, Standard-Engine, Standard-IWAD, Startdialog, Screenshot-Import und Mengenbegrenzungen befinden sich unter „General“. Theme, Sprache, Listenzeilenhöhe und Platzhalter-Artwork befinden sich unter „Appearance“.

## 1.17 Beta - 2026-07-29

### Added

- Die Collections-Toolbar bietet nun dieselbe Sortierung, Spaltenauswahl, Ansichtsumschaltung und Kachelgrößensteuerung wie die Bibliothek.
- Über „New Collection“ lassen sich leere Sammlungen mit Name, optionalem Bibliotheksfilter und optionalem Collection-Artwork direkt im Collections-Tab anlegen.
- Leere Sammlungen bleiben als eigenständige Collections sichtbar und können anschließend mit Mods befüllt werden.

### Changed

- Der Dialog zum Anlegen einer Collection und die bestehende Sammlungsverwaltung verwenden dieselben Eingabeelemente und dieselbe Artwork-Speicherlogik.
- Mod-Platzhalter werden vorrangig direkt aus den portablen Ordnern `TileImages/grayscale` und `TileImages/colored` geladen. Dateiänderungen werden bei geöffnetem Launcher überwacht und lösen automatisch einen entprellten Bibliotheks-Refresh aus; ein Rebuild oder Neustart ist nicht nötig.
- Collection-Karten reagieren auf den Kachelgrößenregler und platzieren die Artwork-Aktion im reservierten Kartenfuß statt über dem Titelbild.

### Fixed

- „Release Date“ und „Downloaded“ verwenden in Collection-Listen nun dieselben belastbaren Datenbindungen wie die Bibliothek.
- Hover- und Auswahlflächen der Collection-Kacheln enden bündig an den abgerundeten Kartenrändern.
- Die Artwork-Aktion bleibt auch bei Collections ohne eigenes Artwork klar sichtbar und innerhalb der Karte ausgerichtet.

## 1.16 Beta - 2026-07-29

### Added

- Sammlungen ohne eigenes Artwork verwenden das neue neutrale 4:3-Motiv `collection_placeholder.jpg`.
- In den Einstellungen kann für fehlende Mod-Artworks zwischen farbigen und graustufigen IWAD-Platzhaltern gewählt werden. Graustufen sind der neue Standard.
- Die Listenansicht einer geöffneten Sammlung besitzt nun dieselbe Mod-Detailleiste mit Screenshot-Slider, Statusaktionen, Metadaten und Startfunktionen wie die Bibliothek.

### Changed

- Die Collection-Aktion heißt eindeutig „Collection-Artwork auswählen“ statt „Titelbild auswählen“.
- Collection-Listen verwenden dieselbe Zeilenoptik, Auswahlmarkierung und Detailinteraktion wie die Bibliotheksliste.

## 1.15 Beta - 2026-07-29

### Added

- Sammlungen können ein eigenes Titelbild erhalten. Die Bilder werden im portablen Ordner `UserData/CollectionArtworks` verwaltet und im gleichen 4:3-Crop wie Mod-Artworks dargestellt.
- Die Kachelansicht der Sammlungen zeigt zunächst eigenständige Collection-Karten mit Titelbild, Name und Abschlussfortschritt. Ein Klick öffnet ausschließlich die enthaltenen Mods.
- Innerhalb einer geöffneten Sammlung kann unabhängig zwischen Kachel- und Listenansicht gewechselt werden; die bestehenden Collection-Spalteneinstellungen gelten auch dort.

### Changed

- Offene und geschlossene Collection-Accordions werden beim Tabwechsel beibehalten und dauerhaft im portablen Nutzerzustand gespeichert.
- Der wirkungslose Button „Sammlungen verwalten“ wurde aus der Collections-Toolbar entfernt. Die modbezogene Sammlungsverwaltung bleibt in Kontextmenü und Detailansicht verfügbar.

## 1.14 Beta - 2026-07-29

### Changed

- Die hervorgehobenen Home-Showcase-Kacheln bleiben unabhängig vom Abschlussstatus vollständig sichtbar; die reduzierte Deckkraft gilt weiterhin in den regulären Bibliotheksdarstellungen.
- Erfolgsmeldungen in der globalen Statusanzeige werden nach fünf Sekunden automatisch ausgeblendet. Eine neue Meldung startet den Zeitraum erneut.
- Die native Fenstertitelleiste einschließlich Versionsuntertitel und Systemschaltflächen übernimmt jetzt die Sidebar-Farbe des aktiven Themes, bleibt kontrastreich lesbar und aktualisiert sich auch bei einem Themewechsel.

## 1.13 Beta - 2026-07-29

### Added

- Die Discover-Aktionen zeigen nun ein Download-Symbol beziehungsweise ein Bibliothekssymbol passend zum aktuellen Zustand.

### Fixed

- Doom-1-IWADs verwenden jetzt exakt das vorhandene Motiv `DoomLauncher/TileImages/doom.png` statt des zuvor extrahierten Ultimate-Doom-TITLEPICs.
- Hauptspiel-IWADs verwenden für Übersicht und Detailansicht bewusst ihre kuratierten `TileImages`, auch wenn eine ältere TITLEPIC-Zuordnung in der Datenbank vorhanden ist.

## 1.12 Beta - 2026-07-29

### Added

- Bereits vorhandene `/idgames`-Einträge bieten in „Entdecken“ jetzt die Aktion „In Bibliothek öffnen“ und springen direkt zum markierten Mod.
- Die Launcher-Definitionen zeigen eindeutige Scan- und Löschsymbole an den Source-Port- und IWAD-Aktionen.
- Die Achievement-Zusammenfassung enthält zusätzliche Kennzahlen für gesammelte Items und gefundene Geheimnisse.

### Changed

- Die sichtbare Oberfläche verwendet durchgängig den fachlich korrekten Begriff „IWAD“ statt „GameWad“. Der bestehende physische Ordnername `GameWads` bleibt zur portablen Abwärtskompatibilität unverändert.
- `/idgames`-Downloads werden primär über ihre persistierte `/idgames`-ID erkannt; der Dateiname dient nur noch als Rückfall.
- Erfolgsdialoge verwenden eine eindeutige „OK“-Aktion statt „Abbrechen“.

### Fixed

- Doom-1-, Shareware- und Ultimate-Doom-Einträge fallen nicht mehr fälschlich auf das Doom-2-Artwork zurück.

## 1.11 Beta - 2026-07-29

### Added

- Das Kontextmenü von Mods enthält jetzt die Aktion „Mod löschen“.
- Beim Löschen von Mods, IWADs und Sourceports kann separat gewählt werden, ob die zugehörigen physischen Dateien dauerhaft mitgelöscht werden sollen.
- Sicherheitsprüfungen verhindern das Löschen gemeinsam verwendeter IWAD-Archive, gemeinsam verwendeter Source-Port-Verzeichnisse und unzulässiger Verzeichnisziele.

### Fixed

- Ein bekannter IWAD-Hash wird beim ersten Scan nicht mehr voreilig übersprungen, wenn die IWAD inzwischen in ein anderes Archiv verschoben wurde. Definition und Archivpfad werden nun in demselben Scan aktualisiert.
- Das Löschen eines Mods bereinigt jetzt auch Metadaten, Statistiken, Sammlungszuordnungen, Profile und verwaltete Medienreferenzen konsistent.

## 1.10 Beta - 2026-07-29

### Added

- Source-Port- und IWAD-Definitionen können direkt in den Launcher-Definitionen gelöscht werden.
- Vor dem Löschen erscheint eine Bestätigung; verknüpfte Mods werden auf automatische Auswahl zurückgesetzt, während Dateien auf dem Datenträger unangetastet bleiben.

### Changed

- Erneute Scans von `GameWads` und `Sourceports` gleichen den Datenbestand jetzt vollständig mit den verwalteten Definitionen ab.
- Definitionen fehlender IWAD-Dateien, nicht mehr enthaltener IWADs und fehlender Source-Port-Executables werden entfernt und namentlich im Scan-Ergebnis ausgewiesen.
- Externe, manuell angelegte Definitionen außerhalb der portablen Ordner bleiben von der automatischen Bereinigung ausgeschlossen.

### Fixed

- Die portable WinUI-Ausgabe enthält die .NET-10-x64-Runtime und das Windows App SDK nun vollständig selbst. Eine systemweite .NET-Installation ist zum Start nicht mehr erforderlich.
- Veraltete `resources.pri`-Dateien aus früheren frameworkabhängigen Deployments werden nicht mehr in der Testausgabe weitergeführt; die WinUI-Theme-Ressourcen werden dadurch wieder korrekt aufgelöst.

## 1.9 Beta

- Neue geführte Ersteinrichtung mit getrennten Schritten für IWADs, Sourceports und Mods.
- Automatischer GameWads-Scan erkennt IWADs in WAD-, ZIP-, 7Z- und RAR-Dateien, gleicht Versionen mit dem Hash-Katalog ab und legt vollständige IWAD-Definitionen an.
- Automatischer Sourceports-Scan erkennt die Haupt-EXE je Unterverzeichnis und übernimmt portablen Pfad, Programmversion sowie Screenshot- und Statistikfähigkeiten.
- Automatischer Mods-Scan importiert unterstützte Archive rekursiv, liest Mapnamen aus und übernimmt vorhandene TITLEPIC-Artworks.
- Launcher-Definitionen bieten neue Schaltflächen, um GameWads und Sourceports jederzeit erneut zu scannen.
- Portable Bibliotheksstruktur vereinheitlicht: Modreferenzen werden unter `Data\GameFiles\Mods`, IWAD-Archive unter `Data\GameFiles\GameWads` geführt.
- Bestehende Datenbankreferenzen und verwaltete Dateien werden beim Start sicher auf die neue Unterordnerstruktur migriert.
- Klassische DoomLauncher-Migration sortiert alte, direkt unter GameFiles gespeicherte Mods automatisch in den neuen Mods-Unterordner ein.
- Eine leere portable Datenbankvorlage ermöglicht die Ersteinrichtung ohne vorhandene hobomaster-Installation; die Reset-Instanz erzeugt sie beim ersten Start automatisch.
- Einrichtungshinweise für die erwartete Ordnerstruktur in Englisch, Deutsch, Französisch und Spanisch ergänzt.

All notable changes to the WinUI 3 rebuild are documented here.

## 1.8 Beta - 2026-07-29

### Added

- Added portable IWAD version detection using an MD5-and-file-size catalog. Direct WAD files and WADs inside ZIP, PK3, PK7, 7z and RAR containers can be matched; detected version, hash, size and catalog origin are persisted separately from the classic database schema.
- Added IWAD version display in Launcher Definitions, including an editable value, manual re-detection and a clear known/unknown hash result.
- Added source-port version labels to Settings, the library-entry editor and native launch-option selectors.
- Added safe database repair to the command-line-only Debug page alongside integrity checking and the existing achievement notification/sound test.

### Changed

- Home showcase navigation now resets the library to the complete unfiltered view, selects the clicked mod in both grid and list mode, and scrolls it into view.
- Database health tools moved from regular Settings into Debug mode.
- Source ports now always use the standard `-file` argument; the redundant editable field was removed.
- Removed the optional screenshot command-line argument. Screenshot capture now relies on automatic known locations and explicit manually monitored directories.
- Screenshot- and statistics-dependent fields are hidden whenever the selected capability is marked unsupported.
- Clarified that additional screenshot and savegame directories are optional manually configured monitoring paths.
- Portable CMD launchers now detach the WinUI process and terminate immediately instead of keeping a console window open.
- Replaced the remaining legacy purple/system-accent fallback with `#3E95BE`.
- Advanced the application version to `1.8 Beta`.

### Fixed

- Restored the localized Debug navigation label and all Debug-page control labels.
- Debug mode now exposes usable diagnostics, safe database maintenance and an isolated visual/audio achievement test.

## 1.7 Beta - 2026-07-29

### Added

- Added persistent source-port capabilities for screenshot support, monitored directories, image formats, optional `{path}` capture arguments, statistics adapters, save-game directories and save-game formats.
- Added automatic executable product/file-version detection when selecting a source-port EXE, while retaining editable and empty version values.
- Added a live capability summary and structural configuration test to Launcher Definitions.
- Added persistent achievement-unlock notifications with an in-app success banner, a simple WinUI sound and an unseen-count badge in the sidebar.
- Added a command-line-only Debug section with runtime information, database checking, source-port capability inspection, library refresh and achievement notification testing.
- Added `Start-DoomLauncher-WinUI-Debug.cmd` for portable test installations.

### Changed

- Screenshot and statistics capture now follow the selected source port’s declared capabilities instead of implying identical support for every engine.
- Existing ZDoom-family definitions are migrated automatically to the ZDoom-compatible `.zds`/JSON statistics adapter.
- Advanced the application version to `1.7 Beta`.

### Fixed

- Ports declared without screenshot or statistics support no longer run irrelevant capture or save-game scans.
- Achievement notifications are seeded silently for existing progress, preventing an upgrade from replaying every previously completed achievement.

## 1.6 Beta - 2026-07-29

### Changed

- Discover now enriches incomplete latest-file results through the official `/idgames` detail endpoint, with bounded concurrency and an in-memory detail cache.
- Each main tab now presents a localized, context-specific summary for Home, Library, Discover, Favorites, Recently Played, Latest, Collections and Achievements.
- Download actions in Discover cards are aligned consistently at the bottom.
- Legacy WinUI system-accent fallbacks now resolve to `#215E8E`; the former purple-gray fallback is no longer used.
- Advanced the application version to `1.6 Beta`.

### Fixed

- Date and Rating values in Discover no longer remain blank when the latest-files response only contains abbreviated records.
- Library list selection and hover backgrounds now align flush with the table header instead of retaining the default rounded item inset.
- Dialog title areas now use the native Windows caption-drag gesture across Settings, Launcher Definitions and other application dialogs.

## 1.5 Beta - 2026-07-28

### Added

- Added map and difficulty selection to native launch options. Map names are extracted from WAD, PK3, PK7, ZIP, 7z and RAR content, including nested ZIP/PK3 archives, with a one-time conservative metadata backfill that preserves richer existing map data.
- Added achievement tiers for playtime, completed mods, defeated enemies, collected items, `/idgames` downloads, library size, original IWAD launches, imported collections, favorites, themes and difficulty settings.
- Added persistent counters for tested themes, original IWAD launches and imported collections.
- Added safe collection deletion with an explicit warning. Deleting a collection removes only its assignments; mods and files remain intact.
- Added labeled Date and Rating metadata to Discover entries.
- Added drag-to-move support to application dialogs.

### Changed

- Redesigned Achievements with compact summary cards, grouped milestone sections, fully wrapped descriptions and responsive IWAD statistic cards.
- Collection tile view now uses the same accordion model as list view and lays out tiles in a wrapped grid without horizontal scrolling.
- The collection toolbar now toggles between “Collapse all” and “Open all”.
- Moved the `/idgames` metadata action into the top-right title area of the library-entry editor.
- Replaced ambiguous package import/export glyphs with labeled icon actions.
- Reworked the Settings theme selector to use a consistent native ComboBox without an overlay.
- Renamed sidebar statistics to Mods, Maps, Played mods and Unplayed mods in all supported languages.
- Advanced the application version to `1.5 Beta`.

### Fixed

- Existing members of the legacy `Finished` collection are migrated to the native finished state and the duplicate collection is removed.
- Collection deletion also cleans every affected tag mapping and saved library-filter reference.
- Achievement descriptions and wide IWAD statistics no longer clip at the right edge.

## 1.4 Beta - 2026-07-28

### Added

- Added an Achievements tab driven by play, completion and engine-session statistics.
- Added native GZDoom/UZDoom-family save-game statistic capture for maps, kills, secrets, items, level time and skill.
- Added played, unplayed and finished totals plus entries and maps grouped by IWAD.
- Added database health checks for SQLite integrity, orphaned relations and missing managed files, with backup-first safe repair.
- Added portable `.dl667pack` export and import for multiple mods or complete collections, including archives, metadata, `/idgames` links, artwork, screenshots, finished state and collection assignments.
- Added a one-click action to collapse every collection accordion.
- Added finished/total progress indicators and explanatory tooltips to collection headings.

### Changed

- Standardized all finished/success greens on `#6CCB5F`.
- Matched the Columns control height to the Sorting control across Library and Collections.
- Removed card outlines from Discover and Collections.
- Reserved scrollbar space so collection content is never covered by the vertical scrollbar.
- Expanded the library-entry editor and reserved right-side scrollbar space so media and collection controls remain fully visible.
- Advanced the application version to `1.4 Beta`.

### Fixed

- The dynamic `Finished` collection filter now appears as `Abgeschlossen` in German.
- Nested collection lists no longer render a scrollbar over collection content.
- Database repair no longer removes missing-file entries automatically; it reports them while safely repairing orphaned relations.

## 1.3 Beta - 2026-07-28

### Added

- Added title-artwork selection, screenshot upload, screenshot ordering, and screenshot-to-title promotion to the library-entry editor.
- Added the `/idgames` metadata improvement action directly to the library-entry editor.
- Added a Collections management button to the Collections toolbar.
- Added explicit HEXDD.WAD dependency guidance and native Deathkings launch handling with HEXEN.WAD as the base IWAD.
- Added media persistence APIs for assigning original artwork and maintaining an ordered screenshot gallery without generated thumbnails.

### Changed

- Unified the former “Tags and collections” wording and editor under “Collections”.
- Replaced missing `/idgames` metadata dashes with a short subtle divider.
- Reduced `/idgames` card-border contrast across every theme.
- Advanced the application version to `1.3 Beta`.

### Fixed

- Collection checkboxes now color only the checked box instead of the complete control row.
- Made expanded Collections content fully reachable inside the library-entry editor.
- Screenshot discovery now scans source-port subdirectories, configured paths, and common engine user directories recursively and ignores inaccessible paths.
- Screenshot imports now preserve a stable per-entry display order.
- Re-importing the same source screenshot no longer creates duplicate gallery entries.
- Replacing title artwork removes obsolete title-picture and derived-thumbnail records for that entry.

## 1.2 Beta - 2026-07-28

### Added

- Added an `Ultra-Kompakt` list-row density for Library and Collections.
- Added a consistent mod context menu with favorite, finished, collection, and edit actions.
- Added optional collection tags as persistent Library quick filters.
- Added an explicit Close action and inline save feedback to Launcher Definitions.

### Changed

- Launcher Definitions now save without closing; Cancel restores the selected definition.
- Removed launch profiles from the modern UI, launch flow, and native data services. Source Port and IWAD are selected directly.
- Tile artwork now uses centered 4:3 fill cropping on every side.
- TITLEPIC originals are used directly; newly imported artwork no longer creates derived thumbnails.
- Existing derived thumbnails are removed only when their intact, higher-resolution original is available.
- Advanced the application version to `1.2 Beta`.

### Fixed

- Applied green checked-state colors to collection membership and filter checkboxes.
- Kept collection-filter selections independent from the built-in Library category filters.

## 1.1 Beta - 2026-07-28

### Added

- Added normal and compact row-density settings for Library and Collections list views.
- Added persistent list-density state with backward-compatible defaults.
- Added selectable definition lists beside the Source Port, IWAD, and launch-profile editors.
- Added Windows folder/file pickers for Source Port directories and executables and for IWAD archive files.
- Added portable relative-path conversion and a visible warning when a Source Port or IWAD remains outside the launcher directory.

### Changed

- Replaced pill-shaped library filters with rectangular buttons with softly rounded corners.
- Made list titles use the same font size as the remaining columns while retaining their white semibold emphasis.
- Disabled the tile-size slider while the Library list presentation is active.
- The two secondary Home showcase projects are randomized from all mods whenever Home is entered again.
- Home hero and detail media now use the original high-resolution artwork source while tiles continue using efficient thumbnails.
- Expanded launch-profile help to explain reusable engine, IWAD, map, skill, and argument combinations.
- Advanced the application version to `1.1 Beta`.

### Fixed

- Applied the saved UI language to the initial Library heading before the first section change.
- Removed duplicate TITLEPIC slides by excluding an artwork source image from the screenshot carousel when its thumbnail was derived from that image.
- Preserved portable IWAD references relative to the launcher root while remaining compatible with legacy filename-only IWAD definitions.

## 1.0 Beta - 2026-07-28

### Added

- Added a separate empty `doomlauncher.run-reset` distribution with a guarded reset command for repeatable first-start and migration testing.

### Changed

- Made the Collections table view the default presentation.
- Advanced the application version to `1.0 Beta`.

### Fixed

- Centered Collections list artwork using an explicitly centered crop brush.
- Removed residual spacing and content overflow from hidden table columns in both Library and Collections.
- Made every header and cell follow the selected column visibility, keeping headings and row values aligned for arbitrary column combinations.

## 0.9 Beta - 2026-07-28

### Added

- Added accordion-style collection headers to the Collections list view so every collection can be expanded and collapsed independently.
- Added the complete Library column set to Collections, including artwork, title, author, release date, maps, rating, download state, source port, playtime, and finished state.
- Added a separate Collections column selector that initially inherits the Library selection for existing installations and persists independently afterwards.

### Changed

- Advanced the application version to `0.9 Beta`.

## 0.8 Beta - 2026-07-28

### Added

- Added a grouped list presentation to Collections alongside the existing tile presentation, with an accessible view-mode toggle and direct navigation from every list row.

### Changed

- Removed the heuristic `Total Conversions` filter from the Library while retaining All, IWADs, Mods, and Unplayed.
- Enlarged Launcher Definitions and replaced the cramped native tab strip with three wide, non-closable Source Port, IWAD, and Launch Profile selectors.
- Reorganized Settings into a responsive two-column layout for clearer grouping of game and display options.
- Advanced the application version to `0.8 Beta`.

### Fixed

- Replaced the disabled sort item with a real localized ComboBox placeholder and widened the table-column control with an explicit Fluent list/columns icon.
- Restored theme selection and added a persistent visible label for the currently selected color scheme.
- Made Settings vertically scrollable so Theme and Language remain accessible at smaller window heights.
- Made Settings, Launcher Definitions, metadata, collection, edit, migration, and error dialogs inherit the active application theme.
- Removed hard-coded light-theme text brushes from Launcher Definitions so labels, input fields, cards, and descriptions retain readable contrast in every color scheme.

## 0.7 Beta - 2026-07-28

### Added

- Added server-side 20-entry loading through the Doomworld `/idgames` `latestfiles` endpoint and a localized `Load more` action at the list footer that grows the result set in 20-entry steps. Compact teaser data is retained in the API's newest-first order; full archive details are requested by ID only when a download actually starts.
- Added cached 20-entry paging for `/idgames` searches because the Doomworld search endpoint ignores limit and offset parameters.
- Added native TITLEPIC artwork discovery for local and downloaded ZIP, PK3, PK7, and WAD files, including classic Doom palette-lump decoding and 4:3 thumbnail generation.
- Added TITLEPIC extraction to both new `/idgames` downloads and metadata improvement of existing library entries.
- Added database integration coverage for persistent `/idgames` IDs, TITLEPIC files, derived thumbnails, entry totals, and map-count auditing.

### Changed

- Reworked Launcher Definitions into separate Source Port, IWAD, and Launch Profile tabs with explanatory text and clearly grouped definition, path, argument, and assignment fields.
- Added a horizontal visual divider beside the localized `Mods sorted by game` Home heading.
- Changed the Light theme accent color to `#3A90BC`.
- Advanced the application version to `0.7 Beta`.

### Fixed

- Added a localized `Sorting` placeholder to the library sort selector.
- Replaced the empty table-column dropdown content with a visible list/columns glyph.
- Preserved the existing persistent `/idgames` source mapping for downloads and metadata-only matches.

## 0.6 Beta - 2026-07-28

### Added

- Added Doomworld, ZDoom, and UZDoom color themes using their requested background and accent colors.
- Added a configurable Home rail limit from 1 to 20 entries, defaulting to 10.
- Added localized library statistics to the sidebar for total entries, total maps, and played hours.
- Added a localized `Mods sorted by game` section on Home.
- Added an `/idgames` metadata improvement workflow for existing mods that searches likely matches and updates title, author, description, release date, and rating without replacing local user state.

### Changed

- Removed the System theme and made Dark the fallback for installations that previously selected it.
- Removed the version label from the navigation pane while retaining `0.6 Beta` in the application title bar and executable metadata.
- Limited IWAD-specific Home rails to games whose IWAD has actually been configured.
- Made finished entries 50% transparent in list, grid, and Home showcase views.
- Replaced the post-settings label `Game session` with the more accurate localized `Status`.
- Changed the library view control to show the active list or grid icon without a colored button background.

### Fixed

- Changed the finished-state checkbox accent from purple to green.
- Fixed Settings navigation state so the dialog can be opened again immediately after closing it.
- Centered featured Home artwork both horizontally and vertically in responsive layouts.

## 0.5 Beta - 2026-07-28

### Changed

- Changed headline typography to the official Titillium Web SemiBold font at weight 600.
- Made the list layout the default library view.
- Made every sortable list column header toggle ascending and descending order and added a visible direction indicator.
- Standardized artwork, list thumbnails, and detail media to a cropped 4:3 presentation.
- Centered 4:3 artwork cropping in tiles and the detail media slider.
- Centered the cropped 4:3 thumbnails in every list row using an explicitly aligned image brush.
- Replaced the previous application branding with the supplied Doom Launcher 667 logo across the window title, navigation pane, executable, taskbar, and packaged Windows assets.
- Renamed the Downloads navigation item to Latest in all four languages.
- Made the Realm667 navigation pane darker while retaining its blue and teal accents.
- Made Recently Played strictly chronological, with the most recently played entry first.
- Made Latest sort the classic download history and new `/idgames` imports by newest download first.
- Changed settings and list refreshes to avoid reentrant incremental-collection updates.
- Changed the navigation brand title to `DOOM LAUNCHER 667`.
- Reworked Home into a Steam/GOG-inspired showcase with one large featured game, two supporting spotlights, and the existing recommendation rails.
- Moved favorite state from the tile metadata line to a teal star badge beside the finished badge on the artwork.
- Changed selected filters, view controls, navigation indicators, list indicators, and sliders from the Windows purple accent to `#28A1A0`.
- Changed the Discover search placeholder to `/idgames search` (localized in all supported languages).
- Decoupled the classic executable directory from its database working directory so the legacy launcher can live in a dedicated `Legacy` folder.
- Reorganized the test runtime into `Data`, `Legacy`, `Backups`, and `UserData` while keeping the database and launch scripts discoverable at the root.
- Renamed the application and title bar to `Doom Launcher 667` and exposed the `0.5 Beta` version in the title area and navigation pane.
- Replaced the transitional classic-launcher bridge with a native WinUI launch pipeline.
- Synchronized horizontal scrolling between list headers and rows so both remain aligned.
- Replaced the XAML splash host with a per-pixel-alpha layered splash window for a genuinely transparent background.
- Updated the embedded SQLite native library to the non-vulnerable 2.1.12 maintenance release.

### Added

- Added a borderless centered startup splash using `gfx/logo_alpha_cropped.png`.
- Added true transparent splash composition and a subtle indeterminate loading bar.
- Added a multi-resolution Windows executable icon generated from `gfx/logo_alpha_small_cropped.png`.
- Added the selectable `Finished` list column with a manual checkbox per library entry.
- Added a database-backed finished state, a detail action to toggle it, and a green check badge on finished tiles.
- Added detail-view media navigation with previous/next arrows and a position indicator.
- Added legacy screenshot discovery through `Files` entries with `FileTypeID = 1`.
- Added an `Import screenshots created while playing` setting so the classic GZDoom/UZDoom capture workflow remains available.
- Added native collection management backed by the existing `Tags` and `TagMapping` tables, including creating collections and assigning entries.
- Added real tag-grouped collection rows and made tag assignments editable directly in Edit Entry.
- Added a tile-based Home dashboard with random unplayed mods, newest releases, favorites, and IWAD-specific recommendations.
- Added a live Doomworld `/idgames` Discover catalog with title search, metadata, progress reporting, download, native import, and source tracking.
- Added a tile-size slider to Home and the tile library view.
- Added sortable Author, Release Date, Maps, Rating, Downloaded, Source Port, and Finished fields.
- Added localized labels for the new functionality in English, German, French, and Spanish.
- Added integration coverage for database integrity, finished state, collections, and `/idgames` imports.
- Added native source-port, IWAD, and launch-profile definition management.
- Added native ZIP, 7z, and RAR extraction plus source-port argument construction.
- Added native LastPlayed/playtime updates and post-session GZDoom, UZDoom, and VKDoom screenshot discovery/import.
- Added a first-start and on-demand migration workflow that copies an existing database and its referenced managed files, rewrites portable paths, creates backups, and verifies SQLite integrity.
- Added integration coverage for multi-tab normalization, launcher definitions, a real native process session, screenshot import, and complete migration.

### Fixed

- Fixed the crash when saving settings and ensured both native and WinUI preferences are persisted.
- Fixed the crash when changing the library sort order.
- Fixed crashes when opening Home, Discover, Favorites, Recently Played, Collections, and Downloads.
- Fixed crashes when selecting IWAD, Mod, and Total Conversion filters.
- Fixed the crash when typing in the header search field.
- Preserved favorite, finished, theme, language, and visible-column state when another preference is saved.
- Fixed clipped titles in the tile view by reserving a stable two-line text area.
- Corrected the German label `Unbespielt` to `Ungespielt`.
- Fixed square hover backgrounds around rounded tiles by using a rounded custom item-container template for every tile grid.
- Fixed the Home scrollbar overlapping the tile-size control by reserving right-side scroll clearance.
- Fixed disappearing or malformed edit text by normalizing database tabs and control characters on read and write while preserving description paragraphs.
- Fixed new local and `/idgames` imports so every run of one or more tab characters becomes exactly one space.
- Fixed purple Discover/download actions by explicitly defining normal, hover, and pressed teal accent-button resources.
