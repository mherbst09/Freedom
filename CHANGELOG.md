# Changelog

Alle wesentlichen Änderungen an Freedom werden in dieser Datei dokumentiert. Die Versionsnummern folgen [Semantic Versioning](https://semver.org/lang/de/).

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
