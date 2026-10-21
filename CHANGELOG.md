# Changelog

Alle wesentlichen Änderungen an Freedom werden in dieser Datei dokumentiert. Die Versionsnummern folgen [Semantic Versioning](https://semver.org/lang/de/).

## [Unveröffentlicht]

### Hinzugefügt

- FileFlow als lokale Konverter-App mit 47 Audio-, Video-, Bild-, PDF- und Office-Konvertierungen.
- Drag-and-drop, Mehrfachdateien, Formaterkennung am Dateiinhalt, Vorschauen, Warteschlange mit Fortschritt, Abbruch und ZIP-Export.
- Auswahl von Audio-, Video-, Bild- und PDF-Qualität sowie sortierbares Zusammenfassen mehrerer Bilder in eine PDF.
- Rechtshinweis und technische Schutzgrenzen für YouTube- und SoundCloud-Links.
- Zufällige temporäre Dateinamen, Größen-, Laufzeit- und Parallelitätslimits sowie automatische Löschung nach spätestens 60 Minuten.
- 40 FileFlow-Funktions- und Sicherheitstests sowie responsive Render-Tests für die Oberfläche.
- Drei neue animierte Themes: Weihnachten mit Schnee und Lichterkette, Halloween mit Mond, Nebel und Fledermäusen sowie Jungle mit Blättern, Lianen und Glühwürmchen.
- Räumlichere Stundenanimationen mit Neon-Perspektivtunnel, rotierenden 3D-Würfeln und plastischen Münzen im Feuerwerk sowie perspektivischem Friedhof, Bodennebel und Geisterschädel in der Trauer-Show; adaptive Detailstufen halten die 60-FPS-Ausgabe stabil.

### Abhängigkeiten

- PdfPig 0.1.16 für PDF-Textextraktion.
- Optionale lokale Werkzeuge FFmpeg, LibreOffice und yt-dlp für die zugehörigen Konvertierungen.

## [1.1.0] - 2026-10-20

### Hinzugefügt

- Solution-Datei für den gemeinsamen Build von Anwendung und Tests.
- Windows-CI für Restore, Release-Build, Tests und eine selbstständige `win-x64`-EXE.
- Lokalisierte „Über Freedom“-Ansicht mit Version, Git-Commit, Builddatum, Laufzeit und lokalem Datenpfad.
- Explizite WebView2-Runtime-Prüfung und Link zur offiziellen Microsoft-Downloadseite.
- Drittanbieterhinweise in `THIRD-PARTY-NOTICES.md`.

### Geändert

- Versionsbasis von 1.0.0 auf 1.1.0 angehoben.
- Microsoft.Web.WebView2 SDK von 1.0.2849.39 auf 1.0.4258.31 aktualisiert.

### Technischer Hinweis

- Die Migration von .NET 9 auf .NET 10 LTS wurde geprüft, aber nicht teilweise begonnen: Auf dem Entwicklungsrechner ist derzeit nur das .NET-9-SDK installiert. Bis das .NET-10-SDK verfügbar ist und Build, Tests sowie Veröffentlichung gemeinsam umgestellt werden können, bleibt das Projekt auf `net9.0-windows`.

## [1.0.0]

- Erste zusammengefasste Freedom-Version mit Atomuhr, Stundenplan, Aufgaben, Notizen, Koffein-Tracker, Tutor, Wetter, Autoclicker, GLB-Viewer, Themes und Stundenanimationen.
