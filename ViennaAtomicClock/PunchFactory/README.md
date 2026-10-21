# Punch Factory – Idle-Boxspiel in Freedom

Ein eigenständiges 3D-Idle-Clicker-Spiel im Pixel-Look. Du startest in einer alten Garage und schlägst auf einen Boxsack.
Mit der Schlagkraft kaufst du automatische Boxer, Maschinen und Hallen, trainierst Fähigkeiten und arbeitest dich bis zur
orbitalen Trainingsstation hoch. Alle Namen, Figuren, Grafiken und Sounds sind eigene Platzhalter, es werden keine fremden Assets verwendet.

Punch Factory läuft als App in Freedom (Kachel „Punch Factory“ im App-Umschalter). Statt Unity wird WPF verwendet, so wie
bei Treeman. Die Unity-Vorgaben sind sinngemäß umgesetzt:

| Vorgabe (Unity) | Umsetzung in Freedom |
| --- | --- |
| 3D-Szene, Beleuchtung, Schatten, Reflexionen | WPF-`Viewport3D`: Boxsack, Kette, Raum, Handschuhe als 3D-Meshes. Umgebungs-, Richtungs-, Punkt- und Spotlichter, Glanzlichter (Specular) auf Sack, Handschuhen und Arenaböden, ein Bodenschatten, der dem schwingenden Sack folgt. |
| Pixelation-/Low-Resolution-Shader | Die ganze Szene wird von der GPU als 384×216-Bitmap gecacht (`BitmapCache`) und ohne Glättung hochskaliert (Nearest Neighbor). Texturen sind Pixel-Texturen mit harten Kanten. |
| Partikel, Pixel-Effekte | Pixel-Partikel, Trefferspuren, Pixel-Boxer und Maschinen, Ereignisziele werden in dasselbe niedrig aufgelöste Bild gezeichnet. |
| ScriptableObjects | `%AppData%\ViennaAtomicClock\punchfactory-balance.json`: Preise, Produktion, Multiplikatoren, Combo, Fähigkeiten, Ereignisse, Umgebungen, Karriere-Upgrades. Wird beim ersten Start geschrieben; fehlerhafte Werte werden repariert. |
| Object Pooling | Partikel (420), schwebende Zahlen (40) und Trefferspuren sind feste Pools. Pinsel werden zwischengespeichert, es entsteht kein Müll pro Bild. |
| Große Zahlen | `BigNum`: Mantisse plus 64-Bit-Zehnerpotenz, also praktisch unbegrenzt. Anzeige als 1K, 1M, 1B, 1T, 1Qa … 1Vg, danach aa, ab …, zuletzt 1,23e1234. |
| Spiellogik / Darstellung / UI / Speicherung getrennt | `PunchEngine` (Regeln, ohne Grafik), `PunchScene` (3D/Pixel), `PunchFactoryPage` (UI), `PunchStorage` (Dateien), `PunchAudio` (Ton), `Gamepad` (XInput). |

## Spielinhalt

- **Schlagen:** per Klick auf den Sack, Leertaste/Enter, Touch oder Controller (A). Handschuhe fliegen abwechselnd als Jab, Haken oder Aufwärtshaken.
  Der Sack schwingt und staucht sich je nach Trefferstärke, Pixel fliegen, eine Zahl schwebt hoch.
- **Combo:**
  - Stufen: 10 Treffer ×1,2, 25 ×1,5, 50 ×2, 100 ×3, 250 **Fury Mode**
  - Fury Mode: Der Sack glüht, die Kamera reagiert stärker, Schläge zählen ×5, die Produktion ×2, eigene Fury-Musik läuft.
  - Ohne Treffer fällt die Combo nach 1,2 s langsam ab. Ab 400 Treffern (mit Fußarbeit) gilt ×4.
- **Kritische Treffer** mit eigenem Sound, Blitz und Vibration. Die Tonhöhe steigt in langen Combos.
- **11 Produzenten:** Boxanfänger, Sparringspartner, Persönlicher Trainer (+1 % für alle anderen), Doppelendball, Boxmaschine,
  Trainingsroboter, Profi-Boxteam, Boxstudio (+2 % für menschliche Boxer), Meisterschaftsarena, Holografisches Trainingszentrum
  und Orbitale Schlagstation.
  - Jeder Kauf wird teurer (×1,15); Kaufmengen 1, 10, 25, 100 und Maximum.
  - Meilensteine bei 10, 25, 50, 100 und 250 Stück verdoppeln jeweils die Leistung.
  - Vor dem Kauf werden Preis und Produktionsgewinn angezeigt; bezahlbare Einträge sind hervorgehoben.
  - Jeder Produzent hat seine eigene Pixel-Animation, die mit der Anzahl schneller wird.
- **22 Upgrades** (12 für Schläge, 10 für die Produktion):
  - mehrere Stufen mit steigendem Preis, Freischaltung über Meilensteine oder verdiente Schlagkraft
  - Vorschau des echten Gewinns (mit denselben Formeln berechnet)
- **6 Spezialfähigkeiten** mit Abklingzeit (Tasten 1–6): Power Punch, Flurry, Adrenalin, Fokus, Coach's Call und Golden Bag.
- **6 zufällige Ereignisse:** Goldener Boxhandschuh, Trainingsdrohne, Herausforderer (12 Treffer), Bonus-Boxsack, Motivationsmoment
  und Sponsorengeschenk. Fangen per Klick oder mit Taste E.
- **8 Umgebungen:** Garage, kleines Boxstudio, Fitnessstudio, lokale Arena, WM-Arena, Hightech-Labor, Megaarena und
  Orbitalstation. Jede hat eigene Texturen, eigenes Licht, eigene Musik und einen Produktionsbonus von 0 bis 75 %.
- **Karriere (Prestige):**
  - ab 1 Milliarde Schlagkraft in der aktuellen Karriere möglich
  - Ruhm insgesamt = 5 × ∛(Schlagkraft insgesamt / 1 Mrd.); jeder Ruhmpunkt gibt dauerhaft +2 %
  - 12 Ruhm-Verbesserungen: Schlagkraft, Produktion, Offline-Zeit, Krit, Combo, Abklingzeit, günstigere Produzenten,
    Startkapital, Combo-Untergrenze (neue Mechanik), Ereignisse, Chrom-Handschuhe und Holo-Boxsack
  - Vor dem Neustart zeigt das Spiel genau, was verloren geht und was bleibt.
- **61 Erfolge:** Jeder gibt einen Meisterschaftspunkt (+1 %), viele zusätzlich Kosmetik.
- **31 kosmetische Gegenstände:** Handschuhe, Boxsäcke, Partikelfarben, Schlagspuren, Boxer-Outfits und Dekoration.
  Sie verändern nur das Aussehen.
- **Offline-Fortschritt:**
  - bis 8 Stunden, mehr über Upgrades
  - beim Start wird die vergangene Zeit samt Ertrag angezeigt
  - zurückgestellte Uhren oder mehr als 400 Tage Abwesenheit bringen nichts
- **Statistiken**, **Einstellungen** (Lautstärken, Kamerabewegung, Blitze, reduzierte Bewegung, schwebende Zahlen, Vibration)
- **Speichern:**
  - automatisch alle 30 s, beim Verlassen der Seite und beim Schließen
  - SHA-256-Prüfsumme und Sicherungskopie; Export und Import als Code
  - vollständiges Zurücksetzen mit doppelter Bestätigung

## Steuerung

| Eingabe | Aktion |
| --- | --- |
| Klick / Tippen auf den Sack, Leertaste, Enter | Schlag |
| 1–6 | Spezialfähigkeiten |
| E | Ereignis fangen |
| Controller A / X Y B LB RB / Start | Schlag / Fähigkeiten 1–5 / Ereignis |
| Esc | Fenster schließen |

## Starten, bauen, testen

```bash
dotnet run --project ViennaAtomicClock
dotnet test Freedom.Tests --filter "FullyQualifiedName~Punch|FullyQualifiedName~BigNum"
dotnet publish ViennaAtomicClock/ViennaAtomicClock.csproj -c Release -r win-x64 --self-contained true -o Release
```

Die Tests prüfen unter anderem:
- Zahlentyp und Abkürzungen
- Klicks, Krit, Combo-Stufen, Fury, Preise und Maximalkauf, Meilensteine, Trainer-Bonus
- Upgrades mit Vorschau, Fähigkeiten, Ereignisse, Karriere, Offline-Grenze und Uhr-Manipulation
- Erfolge, Umgebungen und Stabilität bei extremer Produktion
- Speichern mit Prüfsumme und Sicherungskopie, Export/Import, Reparatur von Werten, Balancing-Datei
- Darstellung aller 8 Studios mit Zeitmessung, Spielseite breit und schmal, alle Sprachen und Themes, Vollständigkeit der Texte

## Erweitern

- **Balancing:** Werte in `punchfactory-balance.json` ändern (Freedom neu starten). Neue Produzenten, Upgrades, Fähigkeiten,
  Ereignisse oder Studios lassen sich dort eintragen. Für ihre Namen kommen Einträge mit dem passenden Schlüssel nach
  `PunchFactoryStrings.cs` (z. B. `pf.prod.<id>`). Neue Effekte brauchen einen Wert in `Effect` und eine Formel in `PunchEngine.Compute`.
- **Erfolge und Kosmetik:** in `PunchContent` (`PunchData.cs`) ergänzen. Bedingungen sind `Goal`-Typen mit Schwellwert.
- **Grafik:** Studios in `PunchScene.Textures`/`Lighting`, Pixel-Figuren in `DrawProducers`/`DrawEvent`.
- **Sound:** neue Klänge in `PunchAudio.Synth`.
