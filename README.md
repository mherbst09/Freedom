# Freedom

Windows-Desktop-App (WPF, .NET 9) für den Schulalltag an der HTL: NTP-genaue Uhr mit echtem Stundenplan, Feuerwerk zum Stundenende, Koffein-Tracker, Aufgaben, Notizen und eine lokale Lern-KI – in sechs Themes.

## Funktionen

- **Uhr:** Wien-Zeit mit Mikrosekunden, automatischer NTP-Abgleich (Cloudflare, Google, NTP-Pool), Offline-Fallback auf die Systemzeit, echter Vollbildmodus für den Beamer (`F11` / ⛶, `Esc` beendet).
- **Stundenplan:** lädt den Plan der Klasse 4BHITS automatisch aus dem öffentlichen WebUntis der HTL Salzburg (Fach, Raum, Entfall, Vertretung, Test; Religion katholisch; Gruppe 1/2 umschaltbar), mit Countdown bis zum Stundenende und Offline-Cache.
- **Feuerwerk:** Casino-Show mit selbst erzeugtem Sound zum Ende jeder echten Stunde; Test mit `F9` oder ✦.
- **Koffein-Tracker:** 53 Getränke mit selbst gezeichneten Bildern, Tageslimit und geschätzter aktiver Menge.
- **Aufgaben:** Listen, Mein Tag, Wichtig, Fälligkeiten, Details mit Notiz.
- **Notizen:** Suche, Anheften, Farbmarken, Checklisten, automatisches Speichern.
- **AI – Eagle 1.1:** lokale Lern-KI für C#, OOP, Unity, UML, Patterns, SQL und mehr über Ollama, ohne Cloud. Details: [`ViennaAtomicClock/Tutor/README.md`](ViennaAtomicClock/Tutor/README.md).
- **Themes:** Kriegsfilm, Hacker, Anime, Tetris, America, Minecraft – jeweils hell, dunkel oder „Farbig“ mit eigenem Farbwähler. Fenster ist responsiv.

## Bauen und starten

Voraussetzung: Windows 10/11 und das [.NET 9 SDK](https://dotnet.microsoft.com/download).

```powershell
cd ViennaAtomicClock
dotnet run                     # zum Ausprobieren
dotnet publish -c Release -r win-x64 --self-contained true -o ..\Release   # eigenständige Freedom.exe
```

Die fertige `Freedom.exe` ist nicht im Repository; sie wird mit dem Befehl oben erzeugt (oder als GitHub-Release bereitgestellt). Sie ist nicht signiert, daher kann Windows SmartScreen beim ersten Start nachfragen.

Tests: `cd Freedom.Tests` und `dotnet test`.

## Daten

Alle persönlichen Daten (Einstellungen, Stundenplan-Cache, Koffein-Einträge, Aufgaben, Notizen, KI-Chats und Wissensdateien) liegen nur lokal unter `%AppData%\ViennaAtomicClock\` und nie im Projektordner.

## Genauigkeit der Uhr

Die Zeit wird per NTP abgeglichen und mit `Stopwatch` interpoliert. Eine normale Windows-App mit Internet-NTP kann keine garantierte physikalische Mikrosekundengenauigkeit liefern; die reale Genauigkeit hängt von Netzwerk, Servern und Hardware ab.
