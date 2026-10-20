using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using ViennaAtomicClock.Tutor;
using Xunit;

namespace Freedom.Tests;

public sealed class TempFolder : IDisposable
{
    public TempFolder() => Directory.CreateDirectory(Path);
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "freedom-tests-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

public class PromptTests
{
    private static TutorChat Chat(params (ChatRole Role, string Text)[] messages) =>
        new() { Messages = messages.Select(m => new ChatMessage { Role = m.Role, Content = m.Text }).ToList() };

    [Fact]
    public void Prompt_starts_with_system_prompt_and_keeps_order()
    {
        var chat = Chat((ChatRole.User, "Was ist eine Klasse?"), (ChatRole.Assistant, "Ein Bauplan."), (ChatRole.User, "Und ein Objekt?"));
        var prompt = new ChatService(null).BuildPrompt(chat, new TutorSettings());

        Assert.Equal("system", prompt[0].Role);
        Assert.Contains("HTL", prompt[0].Content);
        Assert.Equal(new[] { "user", "assistant", "user" }, prompt.Skip(1).Select(p => p.Role));
        Assert.Equal("Und ein Objekt?", prompt[^1].Content);
    }

    [Fact]
    public void Long_history_is_trimmed_to_the_budget_but_keeps_the_latest_question()
    {
        var messages = Enumerable.Range(0, 40).SelectMany(i => new[] { (ChatRole.User, new string('a', 900) + i), (ChatRole.Assistant, new string('b', 900)) }).ToList();
        messages.Add((ChatRole.User, "letzte Frage"));
        var prompt = new ChatService(null).BuildPrompt(Chat(messages.ToArray()), new TutorSettings { MaxHistoryCharacters = 5000 });

        var historyChars = prompt.Skip(1).Sum(p => p.Content.Length);
        Assert.True(historyChars <= 5000, $"history was {historyChars} characters");
        Assert.Equal("letzte Frage", prompt[^1].Content);
        Assert.Equal("user", prompt[1].Role);
    }

    [Fact]
    public void Single_huge_message_is_shortened_instead_of_dropped()
    {
        var prompt = new ChatService(null).BuildPrompt(Chat((ChatRole.User, new string('x', 50_000))), new TutorSettings { MaxHistoryCharacters = 4000 });
        Assert.Equal(2, prompt.Count);
        Assert.Contains("gekürzt", prompt[1].Content);
        Assert.True(prompt[1].Content.Length < 4200);
    }

    [Fact]
    public void Cancelled_answers_are_not_sent_as_context()
    {
        var chat = Chat((ChatRole.User, "Frage"));
        chat.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "halbe Antw", Incomplete = true });
        chat.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "Nochmal" });
        var prompt = new ChatService(null).BuildPrompt(chat, new TutorSettings());
        Assert.DoesNotContain(prompt, p => p.Content == "halbe Antw");
    }

    [Fact]
    public void Attachments_are_included_as_code_blocks()
    {
        var message = new ChatMessage { Content = "Was ist falsch?", Attachments = { new Attachment { FileName = "Program.cs", Content = "int x = ;" } } };
        var text = ChatService.ToPromptText(message);
        Assert.Contains("```csharp", text);
        Assert.Contains("Program.cs", text);
        Assert.Contains("int x = ;", text);
    }

    [Fact]
    public void Knowledge_snippets_are_added_to_the_system_prompt()
    {
        using var temp = new TempFolder();
        File.WriteAllText(Path.Combine(temp.Path, "oop.md"), "# Polymorphie\nPolymorphie bedeutet Vielgestaltigkeit: virtual und override.\n\n# Sonstiges\nNichts.");
        var service = new ChatService(new KnowledgeService(temp.Path));
        var prompt = service.BuildPrompt(Chat((ChatRole.User, "Erkläre Polymorphie")), new TutorSettings());
        Assert.Contains("oop.md", prompt[0].Content);
        Assert.Contains("Vielgestaltigkeit", prompt[0].Content);

        var without = service.BuildPrompt(Chat((ChatRole.User, "Erkläre Polymorphie")), new TutorSettings { UseKnowledge = false });
        Assert.DoesNotContain("oop.md", without[0].Content);
    }

    [Fact]
    public void Title_is_taken_from_the_first_line_and_shortened()
    {
        Assert.Equal("Was ist ein Interface?", ChatService.TitleFrom("Was ist ein Interface?\nUnd wozu?"));
        Assert.EndsWith("…", ChatService.TitleFrom(new string('z', 100)));
    }
}

public class StorageTests
{
    [Fact]
    public void Chats_survive_save_and_load()
    {
        using var temp = new TempFolder();
        var storage = new ChatStorageService(new TutorPaths(temp.Path));
        var chat = new TutorChat { Title = "OOP", Messages = { new ChatMessage { Role = ChatRole.User, Content = "Hallo ```code```" } } };
        storage.Save(chat);

        var loaded = Assert.Single(storage.LoadAll());
        Assert.Equal(chat.Id, loaded.Id);
        Assert.Equal("OOP", loaded.Title);
        Assert.Equal("Hallo ```code```", loaded.Messages[0].Content);

        storage.Delete(loaded);
        Assert.Empty(storage.LoadAll());
    }

    [Fact]
    public void A_broken_file_does_not_hide_the_other_chats()
    {
        using var temp = new TempFolder();
        var paths = new TutorPaths(temp.Path);
        var storage = new ChatStorageService(paths);
        storage.Save(new TutorChat { Title = "gut" });
        File.WriteAllText(Path.Combine(paths.Chats, "kaputt.json"), "{ nicht json");
        Assert.Equal("gut", Assert.Single(storage.LoadAll()).Title);
    }

    [Fact]
    public void Settings_round_trip_and_defaults()
    {
        using var temp = new TempFolder();
        var service = new SettingsService(new TutorPaths(temp.Path));
        Assert.Equal("http://127.0.0.1:11434", service.Load().ApiAddress);
        service.Save(new TutorSettings { Model = "llama3.2:3b", Temperature = 0.7 });
        var loaded = service.Load();
        Assert.Equal("llama3.2:3b", loaded.Model);
        Assert.Equal(0.7, loaded.Temperature);
    }

    [Fact]
    public void Markdown_export_contains_roles_and_content()
    {
        var chat = new TutorChat { Title = "SQL", Messages = { new ChatMessage { Role = ChatRole.User, Content = "JOIN?" }, new ChatMessage { Role = ChatRole.Assistant, Content = "```sql\nSELECT 1;\n```" } } };
        var md = MarkdownExport.Chat(chat);
        Assert.StartsWith("# SQL", md);
        Assert.Contains("## Du", md);
        Assert.Contains("## Eagle 1.1", md);
        Assert.Contains("SELECT 1;", md);
    }
}

public class OllamaClientTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        public string? LastBody { get; private set; }
        public string? LastPath { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri!.AbsolutePath;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return _respond(request);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Streams_tokens_from_ndjson_until_done()
    {
        var ndjson = "{\"message\":{\"role\":\"assistant\",\"content\":\"Hal\"},\"done\":false}\n" +
                     "{\"message\":{\"role\":\"assistant\",\"content\":\"lo\"},\"done\":false}\n" +
                     "{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true}\n";
        var handler = new FakeHandler(_ => Json(ndjson));
        var client = new OllamaClient(new HttpClient(handler));

        var tokens = new List<string>();
        await foreach (var token in client.ChatStreamAsync("http://localhost:11434", "m", new[] { new PromptMessage("user", "hi") }, 0.3, 4096, CancellationToken.None))
            tokens.Add(token);

        Assert.Equal(new[] { "Hal", "lo" }, tokens);
        Assert.Equal("/api/chat", handler.LastPath);
        Assert.Contains("\"stream\":true", handler.LastBody);
        Assert.Contains("\"num_ctx\":4096", handler.LastBody);
    }

    [Fact]
    public async Task Missing_model_gives_a_helpful_message()
    {
        var client = new OllamaClient(new HttpClient(new FakeHandler(_ => Json("{\"error\":\"model 'x' not found\"}", HttpStatusCode.NotFound))));
        var error = await Assert.ThrowsAsync<OllamaException>(async () =>
        {
            await foreach (var _ in client.ChatStreamAsync("http://localhost:11434", "x", new[] { new PromptMessage("user", "hi") }, 0.3, 4096, CancellationToken.None)) { }
        });
        Assert.Contains("ollama pull x", error.Message);
    }

    [Fact]
    public async Task Unreachable_server_explains_how_to_start_ollama()
    {
        var client = new OllamaClient(new HttpClient(new FakeHandler(_ => throw new HttpRequestException("refused"))));
        Assert.Null(await client.GetVersionAsync("http://localhost:11434", CancellationToken.None));
        var error = await Assert.ThrowsAsync<OllamaException>(() => client.ListModelsAsync("http://localhost:11434", CancellationToken.None));
        Assert.Contains("ollama serve", error.Message);
    }

    [Fact]
    public async Task Lists_installed_models()
    {
        var json = "{\"models\":[{\"name\":\"qwen2.5-coder:7b\",\"size\":4700000000,\"details\":{\"parameter_size\":\"7.6B\",\"quantization_level\":\"Q4_K_M\"}}]}";
        var client = new OllamaClient(new HttpClient(new FakeHandler(_ => Json(json))));
        var model = Assert.Single(await client.ListModelsAsync("http://localhost:11434/", CancellationToken.None));
        Assert.Equal("qwen2.5-coder:7b", model.Name);
        Assert.Equal("7.6B", model.ParameterSize);
    }

    [Fact]
    public async Task Cancellation_stops_the_stream()
    {
        var client = new OllamaClient(new HttpClient(new FakeHandler(_ => Json("{\"message\":{\"content\":\"a\"},\"done\":false}\n"))));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.ChatStreamAsync("http://localhost:11434", "m", new[] { new PromptMessage("user", "hi") }, 0.3, 4096, cts.Token)) { }
        });
    }
}

public class MarkdownTests
{
    [Fact]
    public void Parses_code_blocks_with_language()
    {
        var blocks = MarkdownParser.Parse("Idee:\n\n```csharp\nint x = 1;\n```\n\n- Punkt");
        Assert.Equal(BlockKind.Paragraph, blocks[0].Kind);
        Assert.Equal(BlockKind.Code, blocks[1].Kind);
        Assert.Equal("csharp", blocks[1].Language);
        Assert.Equal("int x = 1;", blocks[1].Text);
        Assert.Equal(BlockKind.ListItem, blocks[2].Kind);
    }

    [Fact]
    public void Unclosed_fence_while_streaming_is_still_code()
    {
        var blocks = MarkdownParser.Parse("```csharp\nvoid Update()");
        Assert.Equal(BlockKind.Code, Assert.Single(blocks).Kind);
    }

    [Fact]
    public void Inline_bold_italic_and_code()
    {
        var spans = MarkdownParser.Inline("Ein **Interface** ist *abstrakt* und `public`.");
        Assert.Contains(spans, s => s.Kind == SpanKind.Bold && s.Text == "Interface");
        Assert.Contains(spans, s => s.Kind == SpanKind.Italic && s.Text == "abstrakt");
        Assert.Contains(spans, s => s.Kind == SpanKind.Code && s.Text == "public");
    }

    [Fact]
    public void Highlighter_marks_keywords_strings_and_comments()
    {
        var tokens = CodeHighlighter.Tokenize("public string Name = \"Max\"; // Kommentar", "csharp");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Keyword && t.Text == "public");
        Assert.Contains(tokens, t => t.Kind == TokenKind.String && t.Text == "\"Max\"");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Comment);
        Assert.Equal("public string Name = \"Max\"; // Kommentar", string.Concat(tokens.Select(t => t.Text)));
    }
}

public class KnowledgeTests
{
    [Fact]
    public void Finds_the_best_matching_section()
    {
        using var temp = new TempFolder();
        File.WriteAllText(Path.Combine(temp.Path, "db.md"), "# Normalformen\n1NF, 2NF und 3NF vermeiden Redundanz.\n\n# Joins\nINNER JOIN verbindet Tabellen.");
        var snippets = new KnowledgeService(temp.Path).Search("Was macht ein INNER JOIN?");
        Assert.Equal("Joins", snippets[0].Heading);
    }

    [Fact]
    public void Works_without_a_knowledge_folder()
    {
        var service = new KnowledgeService(Path.Combine(Path.GetTempPath(), "gibt-es-nicht-" + Guid.NewGuid()));
        Assert.Empty(service.Search("Polymorphie"));
        Assert.Equal(0, service.FileCount);
    }
}
