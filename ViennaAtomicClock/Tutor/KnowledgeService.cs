using System.IO;
using System.Text.RegularExpressions;

namespace ViennaAtomicClock.Tutor;

public sealed record KnowledgeSnippet(string File, string Heading, string Text, int Score);

/// <summary>
/// Simple local knowledge base: Markdown/text files in the Knowledge folder are split into sections,
/// and the sections that share the most words with the question are added to the prompt.
/// No vector database on purpose; works fine for a handful of class notes.
/// </summary>
public sealed partial class KnowledgeService
{
    private static readonly string[] Extensions = { ".md", ".txt", ".cs" };

    // Very common German/English words that say nothing about the topic.
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "der", "die", "das", "und", "oder", "ein", "eine", "einen", "ist", "sind", "wie", "was", "wer", "mit", "für", "von",
        "zu", "im", "in", "an", "auf", "den", "dem", "des", "ich", "du", "es", "nicht", "kann", "man", "mir", "mich", "bitte",
        "the", "and", "or", "is", "are", "to", "of", "a", "an", "in", "for", "how", "what", "erkläre", "zeig", "zeige"
    };

    private readonly string _folder;

    public KnowledgeService(string folder) => _folder = folder;

    public string Folder => _folder;

    public int FileCount => Directory.Exists(_folder)
        ? Directory.EnumerateFiles(_folder, "*.*", SearchOption.AllDirectories).Count(IsKnowledgeFile)
        : 0;

    public void EnsureFolder()
    {
        if (Directory.Exists(_folder)) return;
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "LIESMICH.md"),
            "# Wissensbasis\n\nLege hier Markdown- (.md), Text- (.txt) oder C#-Dateien (.cs) ab, zum Beispiel Mitschriften, " +
            "Aufgabenstellungen oder C#-Konventionen deiner Klasse.\n\nEagle 1.1 durchsucht sie bei jeder Frage nach passenden " +
            "Abschnitten (Überschriften mit # trennen Abschnitte) und gibt die besten davon dem Modell als Kontext mit.\n");
    }

    public IReadOnlyList<KnowledgeSnippet> Search(string question, int maxSnippets = 3, int maxCharacters = 3000)
    {
        var terms = Terms(question);
        if (terms.Count == 0 || !Directory.Exists(_folder)) return Array.Empty<KnowledgeSnippet>();

        var scored = new List<KnowledgeSnippet>();
        foreach (var file in Directory.EnumerateFiles(_folder, "*.*", SearchOption.AllDirectories).Where(IsKnowledgeFile))
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { continue; }

            foreach (var (heading, body) in Sections(text))
            {
                var words = Terms(heading + " " + body);
                var score = terms.Sum(t => words.Contains(t) ? (heading.Contains(t, StringComparison.OrdinalIgnoreCase) ? 3 : 1) : 0);
                if (score > 0) scored.Add(new KnowledgeSnippet(Path.GetFileName(file), heading, body.Trim(), score));
            }
        }

        var result = new List<KnowledgeSnippet>();
        var used = 0;
        foreach (var snippet in scored.OrderByDescending(s => s.Score).Take(maxSnippets))
        {
            if (maxCharacters - used < 40) break;
            var text = snippet.Text.Length > maxCharacters - used ? snippet.Text[..(maxCharacters - used)] : snippet.Text;
            result.Add(snippet with { Text = text });
            used += text.Length;
        }
        return result;
    }

    private static bool IsKnowledgeFile(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) &&
        !Path.GetFileName(path).Equals("LIESMICH.md", StringComparison.OrdinalIgnoreCase);

    internal static IEnumerable<(string Heading, string Body)> Sections(string text)
    {
        var heading = "";
        var body = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith('#'))
            {
                if (body.Any(l => l.Trim().Length > 0)) yield return (heading, string.Join("\n", body));
                heading = line.TrimStart('#').Trim();
                body.Clear();
            }
            else body.Add(line);
        }
        if (body.Any(l => l.Trim().Length > 0)) yield return (heading, string.Join("\n", body));
    }

    internal static HashSet<string> Terms(string text) =>
        WordPattern().Matches(text.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(w => w.Length > 2 && !StopWords.Contains(w))
            .ToHashSet();

    [GeneratedRegex(@"[\p{L}\p{N}_#+]+")]
    private static partial Regex WordPattern();
}
