# Tutor – lokale Lern-KI in Freedom

Der Tutor ist ein Bereich in Freedom (App-Raster oben links → **◆ Tutor**). Er beantwortet Fragen zu C#/.NET, OOP, Unity, UML, Design Patterns, Clean Code, Algorithmen, Datenbanken/SQL, Netzwerktechnik und Betriebssystemen – mit einem lokalen Sprachmodell über **Ollama**. Nach dem einmaligen Download des Modells läuft alles ohne Internet, ohne Konto, ohne Cloud und ohne Telemetrie.

## Voraussetzungen

- Windows 10/11 (Freedom ist eine WPF-App)
- .NET 9 SDK nur zum Selberbauen; die fertige `Release/Freedom.exe` bringt alles mit
- [Ollama](https://ollama.com) und ein lokales Modell
- RAM: für 7B-Modelle mindestens 8 GB frei, besser 16 GB. Ohne Grafikkarte läuft das Modell auf der CPU – langsamer, aber brauchbar.

## Ollama installieren

1. Installer von ollama.com herunterladen und ausführen (einmalig Internet nötig).
2. Ollama startet danach automatisch im Hintergrund (Symbol im Infobereich). Manuell: `ollama serve`.
3. Test im Terminal: `ollama --version`

## Modell herunterladen bzw. auswählen

```powershell
ollama pull qwen2.5-coder:7b     # Standard: gut bei Code, ca. 4,7 GB
ollama pull qwen2.5-coder:3b     # schneller auf schwachen Laptops, ca. 1,9 GB
ollama pull llama3.2:3b          # kleines Allround-Modell
```

Im Tutor oben rechts auf **◆ Modellname ▾** klicken und ein installiertes Modell wählen.

## Starten

`Release/Freedom.exe` starten → App-Raster → **Tutor**. Die Seitenleiste zeigt unten, ob Ollama erreichbar ist. Läuft Ollama nicht, erklärt ein Hinweis im Chat, was zu tun ist („Erneut prüfen“).

Bedienung: Frage eintippen, **Strg+Enter** sendet, **■ Stopp** bricht ab, **📎** hängt Text-, Markdown- oder C#-Dateien an (sie werden nur beim Senden dieser Nachricht mitgeschickt). Codeblöcke haben „Kopieren“, jede Antwort und der ganze Chat lassen sich als Markdown exportieren (⇩). Den Chat-Titel benennst du um, indem du ihn oben anklickst und änderst.

## Konfiguration (⚙ Einstellungen in der Seitenleiste)

| Einstellung | Standard | Bedeutung |
|---|---|---|
| Ollama-Adresse | `http://127.0.0.1:11434` | lokale API von Ollama |
| Modell | `qwen2.5-coder:7b` | Name wie bei `ollama list` |
| Temperatur | 0,3 | niedrig = genauer, hoch = kreativer |
| Kontext (Tokens) | 8192 | `num_ctx`, wie viel das Modell auf einmal liest |
| Verlauf (Zeichen) | 16000 | ältere Nachrichten darüber hinaus werden nicht mitgeschickt, damit lange Chats schnell bleiben |
| Wissensbasis | an | passende Abschnitte aus dem Knowledge-Ordner mitschicken |
| System-Prompt | eingebaut | Rolle und Regeln des Tutors, „Standard wiederherstellen“ setzt ihn zurück |

## Speicherorte

Alles liegt unter `%AppData%\ViennaAtomicClock\Tutor\`:

- `Chats\` – ein JSON pro Chat
- `Knowledge\` – deine Wissensdateien
- `settings.json` – Tutor-Einstellungen

## Eigene Wissensdateien

Lege `.md`, `.txt` oder `.cs`-Dateien in den Ordner `Knowledge` (Einstellungen → „📁 Ordner öffnen“). Überschriften mit `#` teilen eine Datei in Abschnitte. Bei jeder Frage werden die Abschnitte mit den meisten gemeinsamen Wörtern (bis ca. 3000 Zeichen) dem Modell als Kontext mitgegeben, mit Dateiname als Quelle. Ohne Dateien funktioniert der Tutor genauso.

## Aufbau (kurz)

- `Tutor/Models.cs` – Chat, ChatMessage, TutorSettings, LocalModelInfo
- `Tutor/OllamaClient.cs` – HTTP zu Ollama (Version, Modelle, gestreamter Chat mit Abbruch)
- `Tutor/ChatService.cs` – Prompt-Aufbau: System-Prompt, Wissen, Verlauf im Zeichenbudget
- `Tutor/Storage.cs` – Chats und Einstellungen als JSON, Markdown-Export
- `Tutor/KnowledgeService.cs` – einfache Textsuche im Knowledge-Ordner
- `Tutor/ViewModels.cs` – TutorViewModel, ChatViewModel, SettingsViewModel (MVVM ohne Fremdbibliothek)
- `Tutor/TutorPage.xaml` – Oberfläche, `Tutor/MarkdownView.cs` – Markdown und Syntaxfarben
- `../Freedom.Tests` – Unit-Tests (Prompt, Speicherung, Ollama-Kommunikation mit Fake-Server, Markdown, Wissen): `dotnet test`

## Bekannte Einschränkungen

- WPF statt Avalonia, weil der Tutor ein Bereich der bestehenden WPF-App Freedom ist; dadurch nur Windows.
- Die Wissenssuche ist eine einfache Wortsuche, keine semantische Suche.
- Markdown deckt das Übliche ab (Überschriften, Listen, Zitate, fett/kursiv/Code, Codeblöcke); Tabellen werden als Text angezeigt, Mermaid/PlantUML als Code, nicht als Diagramm.
- Wie gut und schnell die Antworten sind, hängt vom Modell und vom PC ab. Kleine Modelle können sich irren – vor allem bei Unity-APIs lieber in der offiziellen Doku gegenprüfen.
- Bewusst nicht enthalten: Konten, Cloud-Sync, Sprache, Bilder, Agenten, automatisches Ausführen von Code, Plugins, Fine-Tuning, Vektordatenbank.
