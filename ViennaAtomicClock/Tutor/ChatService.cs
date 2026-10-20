namespace ViennaAtomicClock.Tutor;

/// <summary>
/// Turns a chat into the messages sent to the model: system prompt (plus matching knowledge),
/// then as much recent history as fits into the character budget, so long chats stay fast.
/// </summary>
public sealed class ChatService
{
    private readonly KnowledgeService? _knowledge;

    public ChatService(KnowledgeService? knowledge) => _knowledge = knowledge;

    /// <summary>Language the answers should be in by default; follows the app language.</summary>
    public string ReplyLanguage { get; set; } = "Deutsch";

    public IReadOnlyList<PromptMessage> BuildPrompt(TutorChat chat, TutorSettings settings)
    {
        var system = settings.SystemPrompt.Trim().Length > 0 ? settings.SystemPrompt.Trim() : TutorPrompts.DefaultSystemPrompt;

        if (ReplyLanguage != "Deutsch")
            system += $"\n\nWichtig: Antworte standardmäßig auf {ReplyLanguage}, weil die App auf diese Sprache eingestellt ist – außer die Person schreibt in einer anderen Sprache.";

        var lastUser = chat.Messages.LastOrDefault(m => m.Role == ChatRole.User);
        if (settings.UseKnowledge && _knowledge is not null && lastUser is not null)
        {
            var snippets = _knowledge.Search(lastUser.Content);
            if (snippets.Count > 0)
            {
                system += "\n\nAuszüge aus der lokalen Wissensbasis (Unterrichtsunterlagen). Nutze sie, wenn sie zur Frage passen, und nenne die Datei als Quelle:\n" +
                          string.Join("\n\n", snippets.Select(s => $"[{s.File}{(s.Heading.Length > 0 ? " – " + s.Heading : "")}]\n{s.Text}"));
            }
        }

        var history = new List<PromptMessage>();
        var budget = Math.Max(2000, settings.MaxHistoryCharacters);
        var used = 0;

        // Walk backwards from the newest message; the latest question is always included (shortened if huge).
        var relevant = chat.Messages.Where(m => !m.Incomplete && (m.Content.Length > 0 || m.Attachments.Count > 0)).ToList();
        for (var i = relevant.Count - 1; i >= 0; i--)
        {
            var text = ToPromptText(relevant[i]);
            if (used + text.Length > budget)
            {
                if (history.Count > 0) break;
                text = text[..budget] + "\n[… gekürzt, weil die Nachricht sehr lang ist]";
            }
            history.Insert(0, new PromptMessage(relevant[i].Role == ChatRole.User ? "user" : "assistant", text));
            used += text.Length;
        }

        // Models expect the conversation to start with a user turn.
        while (history.Count > 0 && history[0].Role == "assistant") history.RemoveAt(0);

        return new[] { new PromptMessage("system", system) }.Concat(history).ToList();
    }

    /// <summary>User message plus the files the user explicitly attached.</summary>
    public static string ToPromptText(ChatMessage message)
    {
        if (message.Attachments.Count == 0) return message.Content;
        var files = message.Attachments.Select(a => $"Datei „{a.FileName}“:\n```{Language(a.FileName)}\n{a.Content.TrimEnd()}\n```");
        return message.Content + "\n\n" + string.Join("\n\n", files);
    }

    /// <summary>Short chat title from the first question.</summary>
    public static string TitleFrom(string question)
    {
        var line = question.Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "Neuer Chat";
        return line.Length <= 42 ? line : line[..40].TrimEnd() + " …";
    }

    private static string Language(string fileName) => System.IO.Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".cs" => "csharp", ".md" => "markdown", ".sql" => "sql", ".json" => "json", ".xml" or ".xaml" or ".csproj" => "xml",
        ".py" => "python", ".java" => "java", ".js" => "javascript", ".ts" => "typescript", ".puml" => "plantuml", _ => "text"
    };
}
