Doom Launcher 667 - leere Einrichtungstest-Instanz
===================================================

Start:
  Start-DoomLauncher-WinUI.cmd

Zuruecksetzen:
  Reset-DoomLauncher-WinUI.cmd

Diese Distribution enthaelt absichtlich keine Datenbank, IWADs, Source Ports,
Mods oder Benutzereinstellungen. Beim ersten Start erscheint deshalb der
Einrichtungs- beziehungsweise Migrationsablauf.

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
