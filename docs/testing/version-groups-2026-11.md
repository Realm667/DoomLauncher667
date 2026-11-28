# Versionsgruppen – Verifikation vom 28.11.2026

Ausgangsbasis: 1.1.0 plus geprüfter Autorenkorrektur (b0f115b).
Neue Gruppen betreffen ausschließlich die IWAD-Listenansicht der Library.
Keine Zusammenführung von Datenbankeinträgen oder Archiven, keine Schemaänderung.

Funktionsumfang:
- Kanonische Edition als Gruppenname, neueste verifizierte vergleichbare Version
  klein und grau im Gruppentitel; Anzahl und Aufklapppfeil.
- Aufgeklappte Versionen behalten ihre eigenen Titel, IDs, Bewertungen,
  Collections, Spielstände und Launch Options.
- Zuklappen erhält das konkrete Startziel, auch wenn eine ältere Version gewählt
  ist. Hinweiszeile und Play-Aktion zeigen diese Version ausdrücklich.
- Ein bewusster Klick auf den Gruppentitel wählt dessen neueste konkrete Version.
- Suche berücksichtigt Edition und Version, zeigt passende Kinder zeitweise an
  und stellt den vorherigen Aufklappzustand nach dem Löschen der Suche wieder her.
- Gruppen sortieren nach dem sichtbaren Gruppentitel bzw. gewählten Sortierfeld;
  Kinder bleiben in der belegten Versionsreihenfolge.
- 59 Katalogeinträge haben eine explizite Reihenfolge. Nicht geordnete Ausgaben,
  unbekannte Hashes und nur strukturell erkannte Spiele bleiben einzeln.
- Gruppenschalter und Aufklappzustände werden im portablen JSON-Zustand gesichert.
  Auswahl und Ansichtsposition werden durch die bestehende Navigation und die
  abgestimmte ListView-Reconciliation erhalten. Kein Zusammenlegen von Speicherständen.

Prüfungen und Befunde werden in outputs/version-groups-20261128 aufbewahrt;
synthetische Daten und generierte Bilder gehören nicht ins Repository.
Die Benchmarks laufen sequenziell auf dem normalen Windows-Desktop bei 125%.
Je drei frische synthetische Installationen mit 65 bzw. 11.000 Einträgen;
anschließend jeweils ein eigener Warmstart-Prozess auf derselben Installation.
Windows-Dateicaches wurden nicht geleert: ein frischer Instanzstart ist kein
nachgewiesener Kaltstart des Betriebssystems. Einzelne Messserien zeigten hohe
I/O-/Desktop-Schwankungen; daraus wird keine pauschale Beschleunigung behauptet.
Die erste Kontrolle überlappte beim ersten kleinen Lauf kurz mit einem fokussierten
Testbuild. Die abschließende Serie läuft ohne parallele Projektprüfungen.

Zusätzlicher Stresstest: 65 bzw. 11.000 synthetische Datensätze in einer Gruppe,
mit wiederholten bekannten Metadaten auf künstlichen Nutzdaten. Dies sind keine
11.000 unterschiedlichen oder durch Hashing validierten Doom-Versionen.
Er prüft die echten ItemsSource-, Auswahl- und virtualisierten ListView-Pfade.
Reproduktion: deployment/test-version-groups.ps1 -ApplicationDirectory <WinUI>
-OutputDirectory <neues isoliertes Verzeichnis>. Für die normale Bibliothek:
deployment/test-library-performance.ps1 mit denselben Parametern und -Runs 3.

Realengine-Prüfung: sechs isolierte Starts mit UZDoom 5.0.3 (Registered, Ultimate,
Doom II, Deathkings, Strife Veteran und Square) bestanden. Gespeicherte Launch
Options unverändert. Trackingcode wurde nicht geändert; die Sperre für den
unvalidierten 5.0.3-Log-Parser bleibt bestehen.

Testlücken: kein sauber neu installiertes Windows; keine Behauptung, jede IWAD
mit jeder Engine durchgespielt zu haben. Neue Gruppen sind ein lokaler geprüfter
Entwicklungsstand und noch kein öffentliches Release.

## Gemessene Mediane (Millisekunden)

Vorher ist die erste Vergleichsserie mit Autorenfix; Kontrolle wiederholt denselben
unveränderten Stand später mit 65 Einträgen. Nachher ist die abschließende Serie
mit bedarfsgeladenen Gruppen-Steuerelementen. Alle Rohreihen bleiben erhalten.

| Einträge | Ablauf | Vorher | Kontrolle vorher | Nachher |
| --- | --- | ---: | ---: | ---: |
| 65 | process-start-to-library | 1524 | 2082 | 1794 |
| 65 | warm-process-start-to-library | 1359 | 1562 | 1655 |
| 65 | search | 46 | 157 | 104 |
| 65 | collections-return | 215 | 566 | 483 |
| 65 | details-options | 33 | 33 | 33 |
| 65 | refresh-catalog | 51 | 67 | 67 |
| 65 | import-archive | 524 | 1167 | 700 |
| 65 | refresh-metadata-artwork | 237 | 457 | 183 |
| 11000 | process-start-to-library | 1914 | — | 2665 |
| 11000 | warm-process-start-to-library | 1451 | — | 1825 |
| 11000 | search | 50 | — | 97 |
| 11000 | collections-return | 183 | — | 441 |
| 11000 | details-options | 33 | — | 33 |
| 11000 | refresh-catalog | 233 | — | 333 |
| 11000 | import-archive | 613 | — | 1213 |
| 11000 | refresh-metadata-artwork | 433 | — | 376 |

Der unveränderte Vergleichsstand zeigte in der Kontrolle ebenfalls deutlich
längere Zeiten. Die Ursache der Schwankungen ist nicht isoliert. Die Tabelle
belegt daher keine allgemeine Beschleunigung oder deren Gegenteil; für eine
belastbare kausale Bewertung ist ein kontrollierter ruhiger Rechner erforderlich.
Funktionale Invarianten (Auswahl, Filter, Fokus, Viewport, unveränderte Optionen)
werden in denselben nativen Läufen separat geprüft.

Die normalen Modzeilen erzeugen weder Gruppenpfeil noch leeren Gruppenhinweis.
Beide werden mit x:Load nur für wirkliche Gruppen materialisiert.

Autorenfix: auf Z: nach ausdrücklicher Freigabe installiert; alle 83 Änderungen
und der Erhalt der 6.897 Data-Dateien bestätigt. Die beiden genehmigten Commits
sind gepusht; deren GitHub-CI ist erfolgreich:
https://github.com/Realm667/DoomLauncher667/actions/runs/46130466463
Die neuen Versionsgruppen werden separat lokal geprüft und nicht stillschweigend
in dieses bereits genehmigte Z:-Deployment oder das öffentliche Release aufgenommen.

## Abschließende Gruppen- und Integritätsprüfungen

- Vollständiger Offline-Build und Regressionstest-Suite: RESULT PASS, keine Buildwarnungen.
- Native UI-Suite: 224 Prüfungen bestanden (inklusive echter realisierter Zeilen,
  Auswahlerhalt, Suche, Sprach- und Skalierungsmatrix).
- Portable Overlay-Prüfung, frische Installation, Update von 1.0.0 mit Rollback und
  Migration von 11.000 Archiven / zwölf Collections: bestanden.
- Echte Maus-/Tastaturprüfung an einer markierten 65-Versionen-Instanz: Aufklappen
  per Maus, Links/Rechts am Listenkontext und unveränderte ältere Auswahl bestätigt.
  Eine zuvor gefundene Fokuslücke beim Recycling des geklickten Gruppenknopfs
  ist behoben; Pfeiltasten im Suchfeld klappen keine Gruppe auf.
- Sechs reale Engine-Starts mit erhaltenen individuellen Launch Options: bestanden.

| Synthetische Versionen | Aufklappen, Median ms | Zuklappen, Median ms | Realisierte Zeilen je Lauf |
| --- | ---: | ---: | --- |
| 65 | 284 | 152 | 24, 24, 24 |
| 11000 | 235 | 150 | 16, 25, 26 |

Je Größe drei Prozesse, je Prozess drei Auf-/Zuklappvorgänge; Median der neun
Messungen inklusive nativer Layout-/Renderzyklen und Zustandsspeicherung.
Ein weiterer isolierter 65-Versionen-Lauf prüfte anschließend den Fokusfix.
