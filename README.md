# Freedom

Windows-Desktop-App (WPF, .NET 9) für den Schulalltag an der HTL: NTP-genaue Uhr mit echtem Stundenplan, Feuerwerk zum Stundenende, Trauer-Show zum Stundenbeginn, Koffein-Tracker, Aufgaben, Notizen und eine lokale Lern-KI – in zwölf Themes.

## Funktionen

- **Uhr:** Wien-Zeit mit Mikrosekunden, automatischer NTP-Abgleich (Cloudflare, Google, NTP-Pool), Offline-Fallback auf die Systemzeit, echter Vollbildmodus für den Beamer (`F11` / ⛶, `Esc` beendet).
- **Stundenplan:** lädt den Plan der Klasse 4BHITS automatisch aus dem öffentlichen WebUntis der HTL Salzburg (Fach, Raum, Entfall, Vertretung, Test; Religion katholisch; Gruppe 1/2 umschaltbar), mit Countdown bis zum Stundenende und Offline-Cache.
- **Feuerwerk:** Casino-Show mit selbst erzeugtem Sound zum Ende jeder echten Stunde; Test mit `F9` oder ✦.
- **Koffein-Tracker:** 53 Getränke mit selbst gezeichneten Bildern, Tageslimit und geschätzter aktiver Menge.
- **Aufgaben:** Listen, Mein Tag, Wichtig, Fälligkeiten, Details mit Notiz.
- **Notizen (wie OneNote):** Abschnitte mit Seiten, Menüband (Fett, Kursiv, Textmarker, Schriftfarbe, Überschriften, Listen, Einzüge), Aufgaben-/Wichtig-/Frage-Tags, Tabellen, Bilder, Datumsstempel, Suche, Anheften, automatisches Speichern, Export als .txt, .pdf und .docx sowie Drucken.
- **AI – Eagle 1.1:** lokale Lern-KI für C#, OOP, Unity, UML, Patterns, SQL und mehr über Ollama, ohne Cloud. Details: [`ViennaAtomicClock/Tutor/README.md`](ViennaAtomicClock/Tutor/README.md).
- **Wetter:** aktuelles Wetter über einem animierten Himmel (Sonne, Sterne, Wolken, Regen, Gewitter, Schnee, Nebel, Dämmerung), Unwetterwarnungen von GeoSphere Austria, Niederschlag der nächsten Stunde, Stundenleiste mit Sonnenauf-/untergang, 10-Tage-Vorhersage mit gemeinsamer Temperaturskala, Wetterkarte (Niederschlag, Temperatur, Wind, Luftqualität, Bewölkung, Luftdruck) mit Zeitachse, zwölf Detailkarten (Luftqualität, UV, Wind, Sonne, Niederschlag, gefühlte Temperatur, Feuchte, Sicht, Luftdruck, Wolken, Mond, Durchschnittswerte), mehrere Orte mit Suche (Name oder Postleitzahl), Windows-Standortbestimmung, Einheiten, Offline-Cache. Daten: Open-Meteo (CC BY 4.0), GeoSphere Austria, OpenStreetMap – ohne API-Schlüssel.
- **Autoclicker:** 1 bis 500 Klicks pro Sekunde mit eigener hochauflösender Klick-Engine, Zeitintervall oder CPS, zufällige Abweichung, alle fünf Maustasten, Einzel-/Doppel-/Dreifachklick und Halten, feste Anzahl, Dauer oder „solange Taste gehalten“, feste Position, Klickrouten und Zufallsbereich mit Positionsauswahl über alle Monitore, globale Hotkeys (F6 Start/Stopp, F7 Pause, F8 Notstopp, Esc während des Laufs), Profile mit Import/Export, Maus-Aufzeichnung und Wiedergabe (nur Maus, keine Tastatur) sowie ein Testmodus, der die echte Klickrate misst, ohne zu klicken.
- **FileFlow:** lokaler, responsiver Dateikonverter mit 47 Umwandlungen für Audio, Video, Bilder und Dokumente, Drag-and-drop, Mehrfachdateien, Vorschau, Fortschritt, ZIP-Export und automatischer Löschung. YouTube- und SoundCloud-Links werden nur nach Bestätigung der erforderlichen Rechte verarbeitet. Details, Abhängigkeiten und Installationsbefehle: [`ViennaAtomicClock/FileFlow/README.md`](ViennaAtomicClock/FileFlow/README.md).
- **Themes:** Standard, Eagle, Kriegsfilm, Hacker, Anime, Tetris, America, Minecraft, Mystisch, Weihnachten, Halloween und Jungle – jeweils hell, dunkel oder „Farbig“ mit eigenem Farbwähler. Fenster ist responsiv.
- **Über Freedom:** zeigt die semantische Version, den zugehörigen Git-Commit, das Builddatum, die .NET-Laufzeit und den lokalen Datenordner.

## Bauen und starten

Voraussetzung: Windows 10/11 und für eigene Builds das [.NET 9 SDK](https://dotnet.microsoft.com/download). Die veröffentlichte EXE bringt die benötigte .NET-Laufzeit mit. Der GLB-Viewer benötigt zusätzlich die unter Windows 11 üblicherweise vorhandene [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/#download-section); fehlt sie, zeigt Freedom eine Installationshilfe an. Für das vollständige FileFlow-Angebot werden außerdem die kostenlosen Programme FFmpeg, LibreOffice, yt-dlp und Deno benötigt; ohne sie bleiben die direkt in Freedom implementierten Bild-, PDF- und Textfunktionen verfügbar.

```powershell
dotnet build Freedom.sln -c Release
dotnet test Freedom.sln -c Release
dotnet run --project ViennaAtomicClock
dotnet publish ViennaAtomicClock\ViennaAtomicClock.csproj -c Release -r win-x64 --self-contained true -o Release
```

Die fertige `Freedom.exe` ist nicht im Repository; sie wird mit dem Befehl oben erzeugt (oder als GitHub-Release bereitgestellt). Sie ist nicht signiert, daher kann Windows SmartScreen beim ersten Start nachfragen.

GitHub Actions führt Restore, Release-Build und sämtliche Tests auf Windows aus und stellt anschließend `Freedom.exe` als Build-Artefakt bereit. Versionsänderungen werden in [`CHANGELOG.md`](CHANGELOG.md), verwendete Komponenten und Datenquellen in [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) dokumentiert.

Das Projekt bleibt vorerst vollständig auf .NET 9, weil auf dem Entwicklungsrechner noch kein .NET-10-SDK installiert ist. Die Migration auf .NET 10 LTS wird erst gemeinsam für Anwendung, Tests, CI und Veröffentlichung vorgenommen, damit kein halbfertiger Frameworkwechsel im Repository landet.

## Daten

Alle persönlichen Daten (Einstellungen, Stundenplan-Cache, Koffein-Einträge, Aufgaben, Notizen, KI-Chats und Wissensdateien, Wetter-Orte und -Cache, Autoclicker-Profile und -Aufnahmen) liegen nur lokal unter `%AppData%\ViennaAtomicClock\` und nie im Projektordner. FileFlow speichert Arbeitskopien ausschließlich unter `%TEMP%\Freedom-FileFlow\`, löscht Eingaben nach der Umwandlung und Ergebnisse spätestens nach 60 Minuten beziehungsweise beim Schließen von Freedom.

Der Autoclicker startet nie von selbst. Sobald seine Seite einmal geöffnet wurde, sind seine globalen Hotkeys bei jedem Start von Freedom aktiv (abschaltbar in der Hotkey-Karte). Windows lässt normale Programme nicht in Programme mit Administratorrechten oder den Sperrbildschirm klicken; Freedom umgeht das nicht.

## Genauigkeit der Uhr

Die Zeit wird per NTP abgeglichen und mit `Stopwatch` interpoliert. Eine normale Windows-App mit Internet-NTP kann keine garantierte physikalische Mikrosekundengenauigkeit liefern; die reale Genauigkeit hängt von Netzwerk, Servern und Hardware ab.

## Mitwirkende

- **Codex (OpenAI):** realistischere Feuerwerk- und Stundenbeginn-Animationen, 60-FPS-Optimierung sowie Render- und Performance-Tests.
