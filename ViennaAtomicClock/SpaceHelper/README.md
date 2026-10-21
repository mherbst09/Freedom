# SpaceHelper – sicher Speicherplatz freigeben

SpaceHelper ist eine App in Freedom (Kachel „SpaceHelper“ im App-Umschalter). Sie findet, was die Laufwerke füllt, und gibt
Speicher frei, ohne persönliche Daten oder Systemdateien zu gefährden. **Es wird niemals automatisch gelöscht.**

## Funktionen

- **Übersicht:**
  - alle Laufwerke mit belegtem, freiem und gesamtem Speicher
  - große Schaltfläche „Speicher analysieren“, für ein ganzes Laufwerk oder einen einzelnen Ordner
- **Analyse im Hintergrund** mit Fortschritt (Dateien, gelesene GB, Prozent vom belegten Speicher, aktueller Ordner).
  Jederzeit abbrechbar; das Teilergebnis bleibt sichtbar.
- **Bereinigungsbereiche** mit Größe, Anzahl und Bewertung:
  - Windows-Temp und Temp-Dateien der Programme (nur älter als 1 Tag)
  - Vorschaubilder-Cache
  - alte Fehlerberichte und Absturzabbilder
  - Browser-Cache von Chrome, Edge, Brave, Vivaldi, Opera und Firefox – nur Cache-Ordner, niemals Passwörter, Cookies,
    Verlauf oder Sitzungen
  - alte Installationsdateien
  - große Dateien
  - doppelte Dateien
  - alte Downloads
  - lange nicht genutzte Dateien
  - eigene Ordner
  - Papierkorb (Größe, Anzahl, leeren)
  - installierte Programme
- **Diagramme:**
  - „Größte Ordner“ als Balken; ein Klick öffnet den Ordner (Ordnerbaum mit Pfadleiste)
  - „Speicher nach Dateityp“: Videos, Bilder, Dokumente, Archive, Programme, Sonstiges
- **Dateiliste:**
  - Suche nach Name oder Speicherort
  - Sortieren nach Größe, Datum, Name, Typ oder Ort
  - Filter nach Typ, Bewertung, Mindestgröße und Alter
  - Kontrollkästchen pro Datei, „Alle sicheren auswählen“
  - jede Zeile zeigt Name, vollständigen Pfad, Größe, Änderungsdatum, Kategorie und Bewertung (mit Begründung als Tooltip)
  - „Im Explorer zeigen“
- **Aktionen:**
  - In den Papierkorb (Standard)
  - Verschieben
  - Archivieren (ZIP)
  - Endgültig löschen: deutlich rot gekennzeichnet und nur mit zusätzlicher Bestätigung
- **Bestätigungsdialog** vor jeder Aktion:
  - freizugebender Speicher, Anzahl, Aufteilung sicher/geprüft
  - verständliche Folgen, die größten betroffenen Dateien
  - optional ein Wiederherstellungspunkt
- **Simulationsmodus**: zeigt nur, was passieren würde – es wird nichts verändert.
- **Programme:**
  - Liste nach Größe; markiert, wenn der Programmordner lange unverändert ist
  - Deinstallation über den offiziellen Windows-Deinstaller des Programms oder über „Installierte Apps“ in den Einstellungen
- **Einstellungen:**
  - Schwellen für große Dateien, „lange nicht genutzt“, alte Downloads und Mindestgröße von Duplikaten
  - Browser-Cache ein/aus, Simulationsmodus, Wiederherstellungspunkt-Vorschlag
  - eigene Ordner
- Hell/dunkel und alle Freedom-Themes, 7 Sprachen (Deutsch als Standard), passt sich der Fensterbreite an

## Sicherheit

| Regel | Umsetzung |
| --- | --- |
| Nie automatisch löschen | Alles muss angehakt und im Dialog bestätigt werden. Die Analyse liest nur. |
| Papierkorb als Standard | Windows-Shell-Funktion mit „Rückgängig erlauben“. Endgültiges Löschen nur über die rote Schaltfläche plus „Ich verstehe …“. |
| Bewertung | **Sicher löschbar**: Caches und Temp-Dateien, die neu erzeugt werden. **Prüfung empfohlen**: eigene Dateien. **Nicht löschen**: lässt sich nicht anhaken. |
| Geschützte Bereiche | Windows-Ordner, System32, Programme (x86), ProgramData, AppData, Profil-Dateien (NTUSER.DAT …), System Volume Information, Recovery, Boot/EFI, `$Recycle.Bin`, pagefile/hiberfil/swapfile, Treiber (`.sys`, `.drv`, `.efi`, `.cat`, `.mui`), versteckte Systemdateien. Ausnahmen gibt es nur für eindeutig sichere Cache-Orte (Temp, Thumbcache, WER, CrashDumps, Browser-Cache-Ordner). |
| Duplikate | Das älteste Exemplar jeder Gruppe ist immer „Nicht löschen“ (Original). |
| Cloud-Dateien | OneDrive-Platzhalter sind „Prüfung empfohlen“, weil Löschen sie auch in der Cloud entfernen kann. |
| Links | Symbolische Links, Junctions und Bereitstellungspunkte werden nie verfolgt. Vor dem Löschen wird auch der Pfad darüber geprüft. |
| Zweite Prüfung | Direkt vor jeder Datei wird erneut geprüft: existiert sie noch, ist sie unverändert (Größe/Datum), liegt sie nicht in einem geschützten Pfad, ist sie keine Verknüpfung. Sonst wird sie übersprungen. |
| Stabilität | Gesperrte, beschädigte oder unzugängliche Dateien und Ordner werden gezählt und übersprungen. Abbrechen ist jederzeit möglich. |
| Admin-Rechte | Nur für den Wiederherstellungspunkt (Windows fragt nach) und für Deinstaller, die es selbst verlangen. SpaceHelper läuft sonst ohne Admin-Rechte; geschützte Windows-Temp-Dateien werden dann als „Zugriff verweigert“ gemeldet. |
| Wiederherstellungspunkt | Optional, bei mehr als 5 GB vorausgewählt. Schlägt er fehl, wird nichts verändert. Windows erlaubt meist nur einen Punkt pro 24 Stunden. |
| Datenschutz | Analyseergebnisse werden nicht gespeichert und verlassen nie den PC. Gespeichert werden nur die Einstellungen (`%AppData%\ViennaAtomicClock\spacehelper.json`) und das Protokoll (`spacehelper-log.jsonl`). Das Protokoll enthält Zeit, Aktion, Anzahl, Bytes und betroffene Pfade, niemals Dateiinhalte. |

## Duplikatsuche

1. Gleiche Größe: kommt aus dem Scan, ohne zusätzlichen Dateizugriff. Erst ab der Mindestgröße (Standard 1 MB), geschützte Bereiche ausgenommen.
2. Schneller Vergleich: SHA-256 über Größe sowie die ersten und letzten 64 KB.
3. Voller SHA-256 nur für Dateien, die auch beim schnellen Vergleich gleich sind.

Dabei werden höchstens 2 Dateien gleichzeitig gelesen. Gesperrte Dateien gelten nie als Duplikat.

## Bauen und Starten

```bash
# Starten aus dem Quellcode (Repository-Ordner)
dotnet run --project ViennaAtomicClock

# Tests (Dateierkennung, Duplikate, geschützte Pfade, Größenberechnung, Links, Bereinigung, Oberfläche)
dotnet test Freedom.Tests --filter "FullyQualifiedName~Space|FullyQualifiedName~Duplicate|FullyQualifiedName~CleanupArea"

# EXE bauen
dotnet publish ViennaAtomicClock/ViennaAtomicClock.csproj -c Release -r win-x64 --self-contained true -o Release
```

Voraussetzungen sind Windows 10/11 und das .NET 9 SDK. Es werden keine weiteren Programme benötigt.

Die Tests bauen in einem Temp-Ordner ein unechtes Windows-Verzeichnis nach. Echte System- oder Benutzerdateien werden nie angefasst.
Ausnahme ist der Papierkorb-Test: Er legt eine kleine Testdatei (`spacehelper-test-recycle.bin`) in den echten Papierkorb.

## Aufbau

| Datei | Aufgabe |
| --- | --- |
| `SpaceModels.cs` | Datenmodelle, Einstellungen, Laufwerke, Größenformat |
| `FileRules.cs` | Dateitypen, geschützte Pfade, Bewertung, Browser-Cache-Orte |
| `SpaceScanner.cs` | Hintergrund-Scan (Ordnergrößen, Typen, große/alte Dateien, Duplikat-Kandidaten) |
| `CleanupAreas.cs` | Temp/Cache/Logs/Downloads/eigene Ordner, Papierkorb |
| `DuplicateFinder.cs` | Duplikatsuche in drei Stufen |
| `ProgramInventory.cs` | Installierte Programme und offizielle Deinstallation |
| `SpaceCleaner.cs` | Papierkorb, Löschen, Verschieben, ZIP, Simulation, Protokoll, Wiederherstellungspunkt |
| `SpaceHelperPage.cs` | Oberfläche |
| `SpaceHelperStrings.cs` | Texte in 7 Sprachen |
