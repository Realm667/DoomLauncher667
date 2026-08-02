Doom Launcher 667 - portable Beta
=================================

English
-------

Start:
  Start-DoomLauncher-WinUI.cmd

Extract the complete ZIP into a writable directory before starting it. The
package is self-contained; a separate .NET installation is not required.

This distribution intentionally contains no user database, IWADs, source
ports, mods or personal settings. The first start opens the setup and migration
assistant. You can optionally populate these folders before starting:
  Data\GameWads       IWAD archives or individual IWAD files
  Data\Sourceports    one subdirectory per portable source port
  Data\Mods           mod archives and WAD, PK3 or PK7 files
  Data\Themes         custom XML color definitions

Reset-DoomLauncher-WinUI.cmd removes only mutable data inside this portable
directory and restores the initial empty folder structure.

Deutsch
-------

Start:
  Start-DoomLauncher-WinUI.cmd

Zuruecksetzen:
  Reset-DoomLauncher-WinUI.cmd

Das vollstaendige ZIP vor dem Start in ein beschreibbares Verzeichnis
entpacken. Das Paket ist selbstenthaltend; eine separate .NET-Installation ist
nicht erforderlich. Diese Distribution enthaelt absichtlich keine
Benutzerdatenbank, IWADs, Source Ports, Mods oder persoenlichen Einstellungen.
Beim ersten Start erscheint der Einrichtungs- beziehungsweise Migrationsablauf.

Optional koennen vor dem ersten Start Dateien abgelegt werden:
  Data\GameWads       IWAD-Archive oder einzelne IWAD-Dateien
  Data\Sourceports    ein Unterordner je portablem Sourceport
  Data\Mods           Modarchive, WAD-, PK3- und PK7-Dateien
  Data\Themes         XML-Farbdefinitionen für Launcher-Themes

Nach dem klassischen Migrationshinweis fuehrt die erste Einrichtung
schrittweise durch IWADs, Sourceports und Mods. Eine leere portable Datenbank
wird dabei automatisch aus der mitgelieferten Vorlage erzeugt.

Die Reset-Datei entfernt ausschliesslich veraenderliche Daten innerhalb dieses
Verzeichnisses und stellt danach die leere Verzeichnisstruktur wieder her.
WinUI-Programmdateien, Startdatei, Resetdatei und diese Dokumentation bleiben
erhalten.
