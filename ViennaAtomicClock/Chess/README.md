# Schach – gegen drei Computergegner in Freedom

Ein vollständig spielbares Schachspiel gegen drei Bots: Leicht, Mittel und Schwer. Es läuft als App in Freedom
(Kachel „Schach“ im App-Umschalter). Es braucht keine Downloads, keine Internetverbindung und keine fremden
Schachdienste. Die Figuren kommen aus der Windows-Schrift Segoe UI Symbol und werden zur Laufzeit gezeichnet.

## Aufbau

| Datei | Inhalt |
| --- | --- |
| `ChessRules.cs` | Brett (`int[64]`, a1 = 0), Figuren, Züge, Zuggenerator, Angriffe, Schach, FEN, Notation (SAN), Zobrist-Hash |
| `ChessGame.cs` | Partie: Zugliste, Spielende (Matt, Patt, die drei Remisregeln, Aufgabe, Zeit), Zug zurück, geschlagene Figuren, Schachuhr |
| `ChessEval.cs` | Stellungsbewertung (einfach für Mittel, voll für Schwer) |
| `ChessBots.cs` | Die drei Bots, die Minimax-Suche und ein kleines Eröffnungsbuch |
| `ChessBoardView.cs` | Brett-Darstellung: Figuren, Markierungen, Klick- und Zieh-Bedienung, Animationen |
| `ChessPage.cs` | Oberfläche: Spielerleisten, Status, Zugliste, Dialoge, Bot im Hintergrund |
| `ChessSettings.cs` | Gespeicherte Einstellungen und laufende Partie |
| `ChessStrings.cs` | Texte in 7 Sprachen |

Die Spielregeln, die Bots und die Oberfläche hängen nur in diese Richtung voneinander ab: Oberfläche → Bots →
Regeln. Die Regeln kennen weder Bildschirm noch Bots.

## Regeln

- Der Zuggenerator erzeugt nur legale Züge. Jeder mögliche Zug wird ausgeführt und wieder zurückgenommen, um
  zu prüfen, ob der eigene König danach im Schach stünde. So werden Fesselungen und Züge des Königs auf
  bedrohte Felder automatisch richtig behandelt, auch bei en passant.
- **Rochade:** nur mit unbewegtem König und Turm, freien Feldern dazwischen, nicht aus dem Schach, nicht über
  ein bedrohtes Feld und nicht auf ein bedrohtes Feld. Ein geschlagener Turm nimmt sein Rochaderecht mit.
- **En passant:** nur im Zug direkt nach dem Doppelschritt.
- **Bauernumwandlung:** Ein Dialog fragt nach Dame, Turm, Läufer oder Springer. Abbrechen lässt den Bauern stehen.
- **Spielende:** Schachmatt, Patt, dreifache Stellungswiederholung, 50-Züge-Regel, zu wenig Material
  (nur Könige, ein Leichtfigur, nur Läufer auf gleichfarbigen Feldern), Aufgabe und Zeitüberschreitung.
  Hat der Gegner bei Zeitüberschreitung kein Mattmaterial mehr, ist es Remis. Ein Matt mit dem 100. Halbzug
  zählt als Matt.
- Wiederholungen werden über einen Zobrist-Hash erkannt. En passant zählt dabei nur, wenn der Schlag wirklich
  möglich ist, so wie es die Regeln verlangen.

## Die Bots

Alle Bots rechnen in einem Hintergrund-Thread auf einer Kopie der Stellung. Die Oberfläche bleibt bedienbar,
das Brett nimmt solange keine Eingaben an, und „Bot denkt …“ wird angezeigt. Neues Spiel und Zug zurück
brechen die Rechnung ab.

| Stufe | Verhalten |
| --- | --- |
| **Leicht** | Meist ein zufälliger legaler Zug. Ein Matt in einem Zug nimmt der Bot zu 80 %. Manchmal schlägt er die wertvollste Figur, ohne auf Deckung zu achten, oder gibt Schach. Macht also offensichtliche Fehler. |
| **Mittel** | Alpha-Beta mit 2 Halbzügen (3 im Endspiel), dazu eine Schlagabfolge-Suche. Kennt die Figurenwerte, sieht direkte Drohungen, schlägt nur sichere Figuren, rettet bedrohte Figuren und entwickelt in der Eröffnung. Etwas Zufall sorgt für Abwechslung. |
| **Schwer** | Negamax-Minimax mit Alpha-Beta in iterativer Vertiefung: so viele Halbzüge, wie in 2,5 s möglich sind; im Mittelspiel gemessen 9–11 (mit Reduktionen, etwa 1,3 Mio. Stellungen pro Sekunde). Dazu Transpositionstabelle, Ruhesuche bei Schlagzügen, Schach-Verlängerung, Null-Zug- und Spätzug-Reduktion. Zugreihenfolge: bester Zug aus der Tabelle, Schlagzüge (wertvollstes Opfer zuerst), Umwandlungen, Killer-Züge, History. Bewertet Material, Figurenstellung und Zentrum, Königssicherheit (Bauernschild, offene Linien), Bauernstruktur (Doppel-, Isoliert- und Freibauern), Türme auf offenen Linien, Mobilität, Läuferpaar und Entwicklung. Ein Matt zählt mehr als jedes Material, ein kürzeres mehr als ein längeres. |

Mittel und Schwer beginnen mit einem kleinen Eröffnungsbuch aus bekannten Zugfolgen, damit nicht jede Partie gleich anfängt.

## Bedienung

- Figur anklicken und dann das Zielfeld anklicken, oder die Figur ziehen.
- Mögliche Zielfelder zeigen Punkte, mögliche Schläge Ringe. Markiert werden die gewählte Figur, der letzte
  Zug und ein König im Schach (rot).
- Spielerleisten über und unter dem Brett: Name, wer am Zug ist, geschlagene Figuren, Materialvorsprung, Uhr.
- Seitenleiste: Status (am Zug, Schach, Bot denkt …, Ergebnis), Partie-Infos, Zugliste in Notation.
- Buttons: Neues Spiel, Zug zurück, Aufgeben (mit Rückfrage), Brett drehen, Darstellung.
- Tastatur: Strg+Z Zug zurück, Strg+N neues Spiel, Esc schließt Dialoge oder hebt die Auswahl auf.
- Ab 900 px Breite stehen Brett und Seitenleiste nebeneinander, darunter untereinander.
- „Neues Spiel“ fragt Gegner, Figurenfarbe (Weiß, Schwarz, Zufall) und Schachuhr ab. Die Uhr ist optional:
  ohne, 3+2, 5+0, 10+0, 15+10 oder 30+0. Sie startet nach dem ersten Zug von Weiß und pausiert, solange die
  Seite nicht sichtbar ist.
- Unter „Darstellung“: Farbschema (wie die App, hell, dunkel), vier Bretter (Holz, Turnier, Eis, Nacht),
  mögliche Züge zeigen, Koordinaten, Animationen und ob „Zug zurück“ erlaubt ist.

Einstellungen und eine laufende Partie werden in `%AppData%\ViennaAtomicClock\chess.json` gespeichert. Nach einem
Neustart von Freedom geht die Partie weiter. Ein beschädigter Spielstand wird verworfen und nie halb geladen.

## Tests

```bash
dotnet test Freedom.Tests --filter "FullyQualifiedName~Chess"
```

- **Zuggenerator:** Perft-Zählungen von 6 Referenzstellungen, bis zu 674 624 Zugfolgen, inklusive
  Rochade-, en-passant- und Umwandlungsfallen. Der Hash stimmt nach jedem Zug und jeder Rücknahme.
- **Regeln:** jede Figur nach ihren Regeln, kein Königszug auf bedrohte Felder, Fesselungen (auch en passant),
  Schach, Matt und Patt, alle Rochade-Bedingungen, en passant nur direkt danach, Umwandlung in alle vier Figuren,
  dreifache Wiederholung, 50-Züge-Regel, zu wenig Material, Aufgabe und Zeit, FEN und Notation, Zug zurück.
- **Bots:** Alle drei spielen nur legale Züge, auch im Schach. Leicht nimmt meist ein Matt in 1, ist aber zufällig.
  Mittel nimmt freie Figuren, rettet die eigene Dame und entwickelt. Schwer findet ein Matt in 2, zieht Matt dem
  Material vor, verteidigt gegen Matt, findet Gabeln, hält das Zeitlimit ein, lässt sich abbrechen und schlägt
  den leichten Bot.
- **Oberfläche:** Dialog vor dem Spiel, Züge per Klick, Eingabesperre und „Bot denkt …“, flüssige Oberfläche
  während der Bot rechnet, Zug zurück (auch während der Bot rechnet), Umwandlungsdialog, Matt-Anzeige, Neustart
  setzt alles zurück, Aufgeben, alle Sprachen, Themes und Farbschemata, vollständige Texte.
