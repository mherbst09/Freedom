namespace ViennaAtomicClock.Tutor;

public enum ChatRole { User, Assistant }

public sealed class Attachment
{
    public string FileName { get; set; } = "";
    public string Content { get; set; } = "";
}

public sealed class ChatMessage
{
    public ChatRole Role { get; set; }
    public string Content { get; set; } = "";
    public List<Attachment> Attachments { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    /// <summary>Set when the answer was cancelled or failed, so it is shown but not sent again as context.</summary>
    public bool Incomplete { get; set; }
}

public sealed class TutorChat
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "Neuer Chat";
    public string? Model { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<ChatMessage> Messages { get; set; } = new();
}

public sealed class TutorSettings
{
    public string ApiAddress { get; set; } = "http://127.0.0.1:11434";
    public string Model { get; set; } = "qwen2.5-coder:7b";
    public double Temperature { get; set; } = 0.3;
    /// <summary>Context window (tokens) requested from Ollama.</summary>
    public int ContextTokens { get; set; } = 8192;
    /// <summary>Upper limit for chat history sent with each request, so long chats stay fast.</summary>
    public int MaxHistoryCharacters { get; set; } = 16000;
    public bool UseKnowledge { get; set; } = true;
    public string SystemPrompt { get; set; } = TutorPrompts.DefaultSystemPrompt;
}

public sealed record LocalModelInfo(string Name, long SizeBytes, string ParameterSize, string Quantization)
{
    public string Label => string.IsNullOrEmpty(ParameterSize) ? Name : $"{Name} · {ParameterSize}";
    public string SizeLabel => SizeBytes > 0 ? $"{SizeBytes / 1024.0 / 1024 / 1024:0.0} GB" : "";
}

/// <summary>One message as sent to the model.</summary>
public sealed record PromptMessage(string Role, string Content);
