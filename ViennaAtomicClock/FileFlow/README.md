# FileFlow – Dateikonverter in Freedom

FileFlow wandelt Audio, Video, Bilder und Dokumente direkt auf dem eigenen PC um. Es gibt kein Konto, keine Werbung,
und es wird nichts ins Internet hochgeladen. Eine Ausnahme sind YouTube- und SoundCloud-Links: Dort lädt FileFlow das Audio
freigegebener Inhalte von der jeweiligen Plattform.

FileFlow ist als App in Freedom umgesetzt (Kachel „FileFlow“ im App-Umschalter). Die „Server“-Aufgaben aus der Anleitung
übernimmt ein lokaler Dienst im selben Programm: Dateityp prüfen, Kopien unter zufälligen Namen ablegen, Limits durchsetzen,
Warteschlange abarbeiten und Dateien automatisch löschen.

## Funktionen

- Große Drag-and-drop-Fläche (Dateien und ganze Ordner, auch irgendwo auf die Seite fallen lassen) und „Datei auswählen“
- Feld für YouTube- und SoundCloud-Links mit Pflicht-Bestätigung der Rechte
- Automatische Formaterkennung am Dateiinhalt (Signaturen, Container, ZIP-Inhalt), nicht an der Endung
- Zielformat pro Datei wählbar, oder vorab über eine Karte im Katalog
- Mehrere Dateien gleichzeitig; höchstens 2 Umwandlungen laufen parallel, der Rest wartet
- Fortschritt für „Übernehmen“ (Kopie) und „Umwandeln“, Anzeige von Name, Größe, erkanntem Format und Vorschau
  (Bilder, erste PDF-Seite, Videobild)
- Abbrechen, Entfernen, Erneut versuchen, Speichern einzeln, mehrere Seiten in einen Ordner, alles als ZIP
- Audioqualität 128/192/256/320 kbit/s, Videoauflösung (Original/2160p/1080p/720p/480p) und -qualität,
  Bildqualität und Bildgröße, PDF-Seitengröße A4/Letter/automatisch
- Mehrere Bilder → eine PDF, mit ↑/↓ sortierbar
- Hell/dunkel und alle Freedom-Themes, 7 Sprachen, passt sich der Fensterbreite an

## Unterstützte Umwandlungen (47)

**Hervorgehoben:** MP4 → MP3, YouTube-Link → MP3, SoundCloud-Link → MP3, PNG → PDF, JPG → PDF, TXT → PDF, DOCX → PDF.

**Weitere 40:**

| Bereich | Umwandlungen |
| --- | --- |
| Video | MOV, AVI, MKV, WEBM, FLV, M4V → MP4; GIF → MP4 |
| Audio | MP4 → WAV/AAC; WAV, M4A, AAC, FLAC, OGG, WMA → MP3; MP3 → WAV/M4A |
| Bilder | PNG ↔ JPG; PNG/JPG → WEBP; WEBP → JPG/PNG; HEIC → JPG/PNG; SVG → PNG; BMP → PNG; TIFF → JPG |
| Dokumente | PDF → JPG/PNG/TXT/DOCX; DOCX → TXT; PPTX, XLSX, ODT, RTF, HTML, Markdown, EPUB → PDF |

## Benötigte Programme (kostenlos, Open Source)

| Programm | Wofür | Installation |
| --- | --- | --- |
| FFmpeg („full build“) | Audio, Video, GIF, WEBP | `winget install Gyan.FFmpeg` |
| LibreOffice | DOCX/PPTX/XLSX/ODT/RTF/HTML/Markdown/EPUB/TXT → PDF, SVG | `winget install TheDocumentFoundation.LibreOffice` |
| yt-dlp | YouTube/SoundCloud → MP3 | `winget install yt-dlp.yt-dlp` |
| Deno | wird von yt-dlp für YouTube gebraucht | `winget install DenoLand.Deno` |

Ohne diese Programme funktioniert alles, was in Freedom selbst steckt:
- Bilder (PNG, JPG, BMP, TIFF, HEIC/WEBP über Windows)
- Bilder → PDF
- PDF → JPG/PNG/TXT/DOCX
- DOCX → TXT
- TXT → PDF (dann ohne LibreOffice nur mit lateinischen Zeichen)

Fehlt ein Programm, zeigt die Seite oben „✕ FFmpeg“ und die betroffene Umwandlung eine klare Meldung mit dem
Installationsbefehl. FileFlow sucht die Programme im PATH (auch im neu gesetzten Benutzer-PATH), im WinGet-Ordner und unter
`C:\Program Files\LibreOffice`. Programme, die während der Laufzeit installiert werden, findet FileFlow beim nächsten Öffnen der Seite.

In Freedom eingebaut (NuGet): **PdfPig** (Apache 2.0) für PDF-Text. Für PDF-Seiten als Bild wird der PDF-Renderer von
Windows (`Windows.Data.Pdf`) genutzt, für Bilder Windows Imaging (WIC).

## Starten

```bash
# Programme installieren (einmalig)
winget install Gyan.FFmpeg
winget install TheDocumentFoundation.LibreOffice
winget install yt-dlp.yt-dlp
winget install DenoLand.Deno

# Aus dem Quellcode starten (im Repository-Ordner)
dotnet run --project ViennaAtomicClock

# Tests
dotnet test Freedom.Tests --filter "FullyQualifiedName~FileFlow|FullyQualifiedName~FormatDetector"

# Fertige EXE bauen (Release\Freedom.exe)
dotnet publish ViennaAtomicClock/ViennaAtomicClock.csproj -c Release -r win-x64 --self-contained true -o Release
```

Die Tests wandeln echte Dateien um: Testbilder, mit FFmpeg erzeugte Video- und Tondateien in allen Containern, sowie mit
LibreOffice erzeugte XLSX-, ODT- und PPTX-Dateien. Fehlt ein Programm, werden die betroffenen Tests ohne Prüfung beendet.

## Sicherheit und Datenschutz

- **Echter Dateityp:** Erkannt wird anhand des Inhalts (Magic Bytes, MP4-Brand, RIFF-Typ, EBML-Doctype, ZIP-Inhalt).
  Eine falsche Endung ändert nichts. Die Kopie wird vor der Umwandlung noch einmal geprüft; wurde die Datei
  zwischendurch ausgetauscht, bricht FileFlow ab.
- **Nie ausführen:** Programme (MZ/ELF/Mach-O), Skripte (`#!`, `.bat`, `.ps1`, `.js`, `.vbs` …) und Verknüpfungen werden abgelehnt.
  Dateien werden nur als Argument an FFmpeg/LibreOffice/PdfPig übergeben: ohne Shell, mit Argumentliste, schreibgeschützt.
  HTML und Markdown werden von Skripten, Event-Attributen, `javascript:`/`file:`-Links und Frames befreit.
  XML wird ohne DTD und ohne externe Auflösung gelesen.
- **Zufällige Namen:** Jede Umwandlung bekommt einen eigenen Ordner unter `%TEMP%\Freedom-FileFlow\jobs\<Prozess>-…\<zufällig>`.
  Die Eingabedateien heißen zufällig (96 Bit aus einem kryptografischen Zufallsgenerator), nie nach dem Original.
- **Löschen nach spätestens 60 Minuten:** Eine Minutenuhr löscht abgelaufene Ergebnisse. Eingabekopien und Zwischendateien
  werden sofort nach der Umwandlung gelöscht. Abbrechen, Fehler und „Entfernen“ löschen sofort, das Schließen von Freedom
  löscht alles. Reste eines Absturzes werden beim nächsten Start entfernt. Die Seite zeigt die Löschzeit für jede Datei an.
  Dauerhaft gespeichert werden nur die Einstellungen (`%AppData%\ViennaAtomicClock\fileflow.json`).
- **Limits:**
  - Größe: 2 GB pro Datei, Bilder höchstens 120 Megapixel, PDFs höchstens 500 Seiten
  - Parallel: höchstens 2 Umwandlungen gleichzeitig, LibreOffice immer nur einmal (eigenes Profil, stört kein offenes LibreOffice)
  - Laufzeit: Video 90 Min., Audio 30 Min., Links 30 Min. (bis 1 GB), LibreOffice 5 Min.
  - Läuft eine Umwandlung zu lange, wird der ganze Prozessbaum beendet.
  - Externe Programme laufen mit niedrigerer Priorität.
- **Stabil bei Fehlern:** Jede Umwandlung fängt ihre Fehler selbst ab. Eine beschädigte Datei wird als Zeile mit
  verständlicher Meldung angezeigt; andere Umwandlungen und Freedom laufen weiter.

## YouTube und SoundCloud

- Nur für eigene, gemeinfreie oder ausdrücklich zur Nutzung freigegebene Inhalte. Vor jedem Link muss bestätigt werden,
  dass die Rechte vorliegen. Ohne Bestätigung wird der Link abgelehnt.
- Es gibt keine Umgehung von Schutzmaßnahmen: keine Cookies, keine Anmeldung, kein Geo-Bypass, keine Playlists, keine Livestreams.
  Private, kostenpflichtige, altersbeschränkte, nur für Mitglieder freigegebene und DRM-geschützte Inhalte schlagen fehl
  und bekommen eine eigene, verständliche Meldung.
- Nur Adressen von `youtube.com`, `youtu.be`, `music.youtube.com` und `soundcloud.com` werden angenommen, nur über http(s).
- Die Nutzungsbedingungen der Plattformen gelten weiterhin. YouTube erlaubt das Herunterladen grundsätzlich nur, wo der
  Dienst es selbst anbietet oder der Rechteinhaber zustimmt.

## Hinweise für die Veröffentlichung

- `Release\Freedom.exe` enthält FFmpeg, LibreOffice und yt-dlp **nicht**. Sie werden auf dem Zielrechner per winget
  installiert. So bleibt die EXE klein, und die Lizenzpflichten der Programme gelten nur für deren eigene Installation.
- Wer FFmpeg mitliefern will: Der „full build“ steht unter GPL. Die EXE müsste dann GPL-kompatibel veröffentlicht werden.
  Alternativ einen LGPL-Build ohne GPL-Teile wählen (ohne libx264 gibt es dann kein H.264).
  Lizenzen: LibreOffice MPL 2.0, yt-dlp Unlicense, PdfPig Apache 2.0.
- Soll FileFlow später als echte Web-App laufen: Der Dienst (`FileFlowService` mit Erkennung, Warteschlange, Limits,
  Löschung) und die Konverter (`FileFlowEngines.cs`) haben keine Abhängigkeit zur Oberfläche und lassen sich hinter eine
  ASP.NET-Core-API mit Upload-Endpunkt legen. Dazu kommen Upload-Limits pro IP, HTTPS und ein Reverse-Proxy.

## Aufbau

| Datei | Inhalt |
| --- | --- |
| `FileFlowCatalog.cs` | Formate und die 47 Umwandlungen |
| `FormatDetector.cs` | Erkennung am Inhalt, Sperre für Programme und Skripte |
| `FileFlowTools.cs` | Suche nach FFmpeg/LibreOffice/yt-dlp, sicherer Prozessstart mit Zeitlimit und Abbruch |
| `FileFlowEngines.cs` | FFmpeg, yt-dlp, Bilder (WIC), PDF schreiben/lesen/rendern, DOCX, LibreOffice, Markdown, HTML-Bereinigung, EPUB |
| `FileFlowService.cs` | Warteschlange, Temp-Ordner, Limits, Löschung, Vorschau, Speichern und ZIP |
| `FileFlowPage.cs` | Oberfläche |
| `FileFlowStrings.cs` | Texte in 7 Sprachen |
