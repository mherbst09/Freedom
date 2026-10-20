using System.IO;
using System.Text.Json;

namespace ViennaAtomicClock.Tutor;

/// <summary>Folders the tutor uses. Kept in one place so tests can point everything at a temp folder.</summary>
public sealed class TutorPaths
{
    public TutorPaths(string root)
    {
        Root = root;
        Chats = Path.Combine(root, "Chats");
        Knowledge = Path.Combine(root, "Knowledge");
        SettingsFile = Path.Combine(root, "settings.json");
    }

    public string Root { get; }
    public string Chats { get; }
    public string Knowledge { get; }
    public string SettingsFile { get; }

    public static TutorPaths Default => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ViennaAtomicClock", "Tutor"));
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Writes via a temp file so a crash never leaves a half-written file behind.</summary>
    public static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", content);
        File.Move(path + ".tmp", path, overwrite: true);
    }
}

/// <summary>One JSON file per chat in the Chats folder.</summary>
public sealed class ChatStorageService
{
    private readonly TutorPaths _paths;

    public ChatStorageService(TutorPaths paths) => _paths = paths;

    public IReadOnlyList<TutorChat> LoadAll()
    {
        if (!Directory.Exists(_paths.Chats)) return Array.Empty<TutorChat>();
        var chats = new List<TutorChat>();
        foreach (var file in Directory.EnumerateFiles(_paths.Chats, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<TutorChat>(File.ReadAllText(file), Json.Options) is { } chat) chats.Add(chat);
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                // A damaged file must not hide all other chats.
            }
        }
        return chats.OrderByDescending(c => c.UpdatedAt).ToList();
    }

    public void Save(TutorChat chat) =>
        Json.WriteAtomic(Path.Combine(_paths.Chats, $"{chat.Id}.json"), JsonSerializer.Serialize(chat, Json.Options));

    public void Delete(TutorChat chat)
    {
        var file = Path.Combine(_paths.Chats, $"{chat.Id}.json");
        if (File.Exists(file)) File.Delete(file);
    }
}

public sealed class SettingsService
{
    private readonly TutorPaths _paths;

    public SettingsService(TutorPaths paths) => _paths = paths;

    public TutorSettings Load()
    {
        try
        {
            return File.Exists(_paths.SettingsFile)
                ? JsonSerializer.Deserialize<TutorSettings>(File.ReadAllText(_paths.SettingsFile), Json.Options) ?? new TutorSettings()
                : new TutorSettings();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return new TutorSettings();
        }
    }

    public void Save(TutorSettings settings) => Json.WriteAtomic(_paths.SettingsFile, JsonSerializer.Serialize(settings, Json.Options));
}

/// <summary>Markdown export of a whole chat or one answer.</summary>
public static class MarkdownExport
{
    public static string Chat(TutorChat chat)
    {
        var lines = new List<string> { $"# {chat.Title}", "", $"_Exportiert am {DateTime.Now:dd.MM.yyyy HH:mm}, Modell: {chat.Model ?? "unbekannt"}_", "" };
        foreach (var message in chat.Messages)
        {
            lines.Add(message.Role == ChatRole.User ? "## Du" : "## Eagle 1.1");
            lines.Add("");
            lines.Add(message.Content.Trim());
            foreach (var file in message.Attachments) lines.Add($"\n_Angehängt: {file.FileName}_");
            lines.Add("");
        }
        return string.Join("\n", lines);
    }

    public static string Answer(ChatMessage message) => message.Content.Trim() + "\n";
}
