using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace ViennaAtomicClock.Tutor;

/// <summary>Things only the view can do (dialogs, clipboard, folders), so view models stay testable.</summary>
public interface IUserDialogs
{
    bool Confirm(string question);
    IReadOnlyList<string> PickTextFiles();
    void SaveMarkdown(string suggestedName, string content);
    void CopyText(string text);
    void OpenFolder(string path);
}

/// <summary>Shared services and settings for all chats.</summary>
public sealed class TutorContext
{
    public TutorContext(TutorPaths paths, HttpClient http)
    {
        Paths = paths;
        Settings = new SettingsService(paths);
        Storage = new ChatStorageService(paths);
        Knowledge = new KnowledgeService(paths.Knowledge);
        Prompts = new ChatService(Knowledge);
        Ollama = new OllamaClient(http);
        Current = Settings.Load();
    }

    public TutorPaths Paths { get; }
    public SettingsService Settings { get; }
    public ChatStorageService Storage { get; }
    public KnowledgeService Knowledge { get; }
    public ChatService Prompts { get; }
    public OllamaClient Ollama { get; }
    public TutorSettings Current { get; set; }
}

public sealed class MessageViewModel : ObservableObject
{
    private string _content;
    private bool _isStreaming;

    public MessageViewModel(ChatMessage message)
    {
        Message = message;
        _content = message.Content;
    }

    public ChatMessage Message { get; }
    public bool IsUser => Message.Role == ChatRole.User;
    public string Sender => IsUser ? "Du" : "Eagle 1.1";
    public string TimeLabel => Message.CreatedAt.ToString("HH:mm");
    public string AttachmentLabel => Message.Attachments.Count == 0 ? "" : "📎 " + string.Join(", ", Message.Attachments.Select(a => a.FileName));
    public bool HasAttachments => Message.Attachments.Count > 0;

    public string Content
    {
        get => _content;
        set { if (Set(ref _content, value)) Message.Content = value; }
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        set => Set(ref _isStreaming, value);
    }

    public bool Incomplete => Message.Incomplete;

    public void MarkIncomplete()
    {
        Message.Incomplete = true;
        Raise(nameof(Incomplete));
    }
}

public sealed class ChatViewModel : ObservableObject
{
    private readonly TutorContext _context;
    private readonly IUserDialogs _dialogs;
    private readonly Action<string> _setStatus;
    private CancellationTokenSource? _generation;
    private string _input = "";
    private bool _isBusy;

    public ChatViewModel(TutorChat chat, TutorContext context, IUserDialogs dialogs, Action<string> setStatus)
    {
        Chat = chat;
        _context = context;
        _dialogs = dialogs;
        _setStatus = setStatus;
        foreach (var message in chat.Messages) Messages.Add(new MessageViewModel(message));

        SendCommand = new RelayCommand(() => _ = SendAsync(), () => !IsBusy && (Input.Trim().Length > 0 || PendingAttachments.Count > 0));
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        AttachCommand = new RelayCommand(Attach, () => !IsBusy);
        RemoveAttachmentCommand = new RelayCommand(p => { if (p is Attachment a) PendingAttachments.Remove(a); });
        CopyMessageCommand = new RelayCommand(p => { if (p is MessageViewModel m) _dialogs.CopyText(m.Content); });
        ExportMessageCommand = new RelayCommand(p =>
        {
            if (p is MessageViewModel m) _dialogs.SaveMarkdown($"{SafeName(Title)}-Antwort.md", MarkdownExport.Answer(m.Message));
        });
    }

    public TutorChat Chat { get; }
    public ObservableCollection<MessageViewModel> Messages { get; } = new();
    public ObservableCollection<Attachment> PendingAttachments { get; } = new();

    public ICommand SendCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand AttachCommand { get; }
    public ICommand RemoveAttachmentCommand { get; }
    public ICommand CopyMessageCommand { get; }
    public ICommand ExportMessageCommand { get; }

    public event Action? Changed;

    public string Title
    {
        get => Chat.Title;
        set
        {
            var title = value.Trim().Length == 0 ? "Neuer Chat" : value.Trim();
            if (title == Chat.Title) return;
            Chat.Title = title;
            Raise();
            Save();
        }
    }

    public DateTime UpdatedAt => Chat.UpdatedAt;
    public string UpdatedLabel => Chat.UpdatedAt.Date == DateTime.Today ? Chat.UpdatedAt.ToString("HH:mm") : Chat.UpdatedAt.ToString("dd.MM.");
    public bool IsEmpty => Messages.Count == 0;

    public string Input
    {
        get => _input;
        set => Set(ref _input, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public bool Matches(string search) =>
        search.Length == 0 ||
        Chat.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
        Chat.Messages.Any(m => m.Content.Contains(search, StringComparison.CurrentCultureIgnoreCase));

    public string ExportMarkdown() => MarkdownExport.Chat(Chat);

    private void Attach()
    {
        foreach (var path in _dialogs.PickTextFiles())
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length > 200_000)
                {
                    _setStatus($"„{info.Name}“ ist zu groß (max. 200 KB).");
                    continue;
                }
                PendingAttachments.Add(new Attachment { FileName = info.Name, Content = File.ReadAllText(path) });
            }
            catch (IOException e)
            {
                _setStatus($"Datei konnte nicht gelesen werden: {e.Message}");
            }
        }
    }

    public async Task SendAsync()
    {
        var text = Input.Trim();
        if (IsBusy || (text.Length == 0 && PendingAttachments.Count == 0)) return;

        var settings = _context.Current;
        var question = new ChatMessage { Role = ChatRole.User, Content = text, Attachments = PendingAttachments.ToList() };
        Chat.Messages.Add(question);
        Messages.Add(new MessageViewModel(question));
        Input = "";
        PendingAttachments.Clear();
        if (Chat.Messages.Count(m => m.Role == ChatRole.User) == 1)
        {
            Chat.Title = ChatService.TitleFrom(text.Length > 0 ? text : question.Attachments[0].FileName);
            Raise(nameof(Title));
        }

        var answer = new ChatMessage { Role = ChatRole.Assistant };
        Chat.Messages.Add(answer);
        var answerVm = new MessageViewModel(answer) { IsStreaming = true };
        Messages.Add(answerVm);
        Chat.Model = settings.Model;
        Raise(nameof(IsEmpty));
        Save();

        IsBusy = true;
        _generation = new CancellationTokenSource();
        _setStatus("Modell wird geladen …");
        var buffer = new StringBuilder();
        var lastUpdate = Stopwatch.StartNew();
        try
        {
            var prompt = _context.Prompts.BuildPrompt(Chat, settings);
            await foreach (var token in _context.Ollama.ChatStreamAsync(settings.ApiAddress, settings.Model, prompt, settings.Temperature, settings.ContextTokens, _generation.Token))
            {
                if (buffer.Length == 0) _setStatus("Antwort wird erstellt …");
                buffer.Append(token);
                // Re-rendering Markdown for every token would be wasteful; ~15 updates per second look smooth.
                if (lastUpdate.ElapsedMilliseconds > 66)
                {
                    answerVm.Content = buffer.ToString();
                    lastUpdate.Restart();
                }
            }
            answerVm.Content = buffer.ToString();
            _setStatus("");
        }
        catch (OperationCanceledException)
        {
            answerVm.Content = buffer.ToString() + (buffer.Length > 0 ? "\n\n" : "") + "_⏹ Abgebrochen._";
            answerVm.MarkIncomplete();
            _setStatus("Antwort abgebrochen.");
        }
        catch (OllamaException e)
        {
            answerVm.Content = "⚠ " + e.Message;
            answerVm.MarkIncomplete();
            _setStatus("Fehler bei der Verbindung zu Ollama.");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or System.Text.Json.JsonException)
        {
            answerVm.Content = buffer + "\n\n⚠ Die Verbindung wurde unterbrochen: " + e.Message;
            answerVm.MarkIncomplete();
            _setStatus("Verbindung unterbrochen.");
        }
        finally
        {
            answerVm.IsStreaming = false;
            IsBusy = false;
            _generation.Dispose();
            _generation = null;
            Chat.UpdatedAt = DateTime.Now;
            Save();
            Raise(nameof(UpdatedLabel));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public void Cancel() => _generation?.Cancel();

    private void Save()
    {
        try { _context.Storage.Save(Chat); }
        catch (IOException e) { _setStatus($"Chat konnte nicht gespeichert werden: {e.Message}"); }
        Changed?.Invoke();
    }

    internal static string SafeName(string title)
    {
        var name = new string(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        return name.Length == 0 ? "Chat" : name.Length > 40 ? name[..40] : name;
    }
}

public sealed class SettingsViewModel : ObservableObject
{
    private readonly TutorContext _context;
    private string _apiAddress = "", _model = "", _temperature = "", _contextTokens = "", _maxHistory = "", _systemPrompt = "", _error = "";
    private bool _useKnowledge;

    public SettingsViewModel(TutorContext context) => _context = context;

    public string ApiAddress { get => _apiAddress; set => Set(ref _apiAddress, value); }
    public string Model { get => _model; set => Set(ref _model, value); }
    public string Temperature { get => _temperature; set => Set(ref _temperature, value); }
    public string ContextTokens { get => _contextTokens; set => Set(ref _contextTokens, value); }
    public string MaxHistory { get => _maxHistory; set => Set(ref _maxHistory, value); }
    public bool UseKnowledge { get => _useKnowledge; set => Set(ref _useKnowledge, value); }
    public string SystemPrompt { get => _systemPrompt; set => Set(ref _systemPrompt, value); }
    public string Error { get => _error; set => Set(ref _error, value); }
    public string KnowledgeInfo => $"{_context.Knowledge.FileCount} Datei(en) in {_context.Paths.Knowledge}";

    public void Load()
    {
        var s = _context.Current;
        ApiAddress = s.ApiAddress;
        Model = s.Model;
        Temperature = s.Temperature.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture);
        ContextTokens = s.ContextTokens.ToString();
        MaxHistory = s.MaxHistoryCharacters.ToString();
        UseKnowledge = s.UseKnowledge;
        SystemPrompt = s.SystemPrompt;
        Error = "";
        Raise(nameof(KnowledgeInfo));
    }

    /// <summary>Validates and stores the values; returns false and sets <see cref="Error"/> if something is off.</summary>
    public bool Save()
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        if (!Uri.TryCreate(ApiAddress.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return Fail("Die API-Adresse muss wie http://localhost:11434 aussehen.");
        if (Model.Trim().Length == 0) return Fail("Bitte einen Modellnamen angeben.");
        if (!double.TryParse(Temperature.Replace(',', '.'), System.Globalization.NumberStyles.Float, invariant, out var temperature) || temperature is < 0 or > 2)
            return Fail("Die Temperatur muss zwischen 0 und 2 liegen.");
        if (!int.TryParse(ContextTokens, out var context) || context is < 512 or > 131072)
            return Fail("Das Kontextfenster muss zwischen 512 und 131072 Tokens liegen.");
        if (!int.TryParse(MaxHistory, out var history) || history is < 2000 or > 500000)
            return Fail("Der Verlauf muss zwischen 2000 und 500000 Zeichen liegen.");

        _context.Current = new TutorSettings
        {
            ApiAddress = ApiAddress.Trim(), Model = Model.Trim(), Temperature = temperature, ContextTokens = context,
            MaxHistoryCharacters = history, UseKnowledge = UseKnowledge,
            SystemPrompt = SystemPrompt.Trim().Length > 0 ? SystemPrompt : TutorPrompts.DefaultSystemPrompt
        };
        try
        {
            _context.Settings.Save(_context.Current);
        }
        catch (IOException e)
        {
            return Fail($"Einstellungen konnten nicht gespeichert werden: {e.Message}");
        }
        Error = "";
        return true;
    }

    public void ResetPrompt() => SystemPrompt = TutorPrompts.DefaultSystemPrompt;

    private bool Fail(string message)
    {
        Error = message;
        return false;
    }
}

public sealed class TutorViewModel : ObservableObject
{
    private readonly TutorContext _context;
    private readonly IUserDialogs _dialogs;
    private readonly DispatcherTimer _connectionTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private ChatViewModel? _selected;
    private string _search = "", _status = "", _connectionText = "Verbindung wird geprüft …", _connectionHelp = "";
    private bool _isConnected, _showSettings, _checking;

    public TutorViewModel(TutorContext context, IUserDialogs dialogs)
    {
        _context = context;
        _dialogs = dialogs;
        Settings = new SettingsViewModel(context);
        _context.Knowledge.EnsureFolder();

        foreach (var chat in context.Storage.LoadAll()) Chats.Add(CreateChatVm(chat));
        ChatList = CollectionViewSource.GetDefaultView(Chats);
        ChatList.Filter = o => o is ChatViewModel c && c.Matches(_search.Trim());
        ChatList.SortDescriptions.Add(new SortDescription(nameof(ChatViewModel.UpdatedAt), ListSortDirection.Descending));

        NewChatCommand = new RelayCommand(NewChat);
        DeleteChatCommand = new RelayCommand(DeleteSelected, () => Selected is { IsBusy: false });
        ExportChatCommand = new RelayCommand(() => { if (Selected is { } c) _dialogs.SaveMarkdown($"{ChatViewModel.SafeName(c.Title)}.md", c.ExportMarkdown()); }, () => Selected is { IsEmpty: false });
        CheckConnectionCommand = new RelayCommand(() => _ = CheckConnectionAsync());
        OpenSettingsCommand = new RelayCommand(() => { Settings.Load(); ShowSettings = true; });
        SaveSettingsCommand = new RelayCommand(() => { if (Settings.Save()) { ShowSettings = false; Raise(nameof(ModelName)); _ = CheckConnectionAsync(); } });
        CancelSettingsCommand = new RelayCommand(() => ShowSettings = false);
        ResetPromptCommand = new RelayCommand(Settings.ResetPrompt);
        OpenKnowledgeCommand = new RelayCommand(() => _dialogs.OpenFolder(_context.Paths.Knowledge));
        PickModelCommand = new RelayCommand(p =>
        {
            if (p is not LocalModelInfo model) return;
            Settings.Load();
            Settings.Model = model.Name;
            if (Settings.Save()) Raise(nameof(ModelName));
        });

        Selected = Chats.FirstOrDefault() ?? AddChat();
        _connectionTimer.Tick += (_, _) => _ = CheckConnectionAsync();
    }

    public ObservableCollection<ChatViewModel> Chats { get; } = new();
    public ICollectionView ChatList { get; }
    public ObservableCollection<LocalModelInfo> Models { get; } = new();
    public SettingsViewModel Settings { get; }

    public ICommand NewChatCommand { get; }
    public ICommand DeleteChatCommand { get; }
    public ICommand ExportChatCommand { get; }
    public ICommand CheckConnectionCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand CancelSettingsCommand { get; }
    public ICommand ResetPromptCommand { get; }
    public ICommand OpenKnowledgeCommand { get; }
    public ICommand PickModelCommand { get; }

    public ChatViewModel? Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) ChatList.Refresh(); }
    }

    public string Status { get => _status; private set => Set(ref _status, value); }
    public string ModelName => _context.Current.Model;
    public bool IsConnected { get => _isConnected; private set => Set(ref _isConnected, value); }
    public string ConnectionText { get => _connectionText; private set => Set(ref _connectionText, value); }
    public string ConnectionHelp { get => _connectionHelp; private set => Set(ref _connectionHelp, value); }

    public bool ShowSettings
    {
        get => _showSettings;
        set => Set(ref _showSettings, value);
    }

    /// <summary>Starts or stops the periodic connection check while the page is visible.</summary>
    public void SetActive(bool active)
    {
        if (active)
        {
            _ = CheckConnectionAsync();
            _connectionTimer.Start();
        }
        else _connectionTimer.Stop();
    }

    public async Task CheckConnectionAsync()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            var address = _context.Current.ApiAddress;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var version = await _context.Ollama.GetVersionAsync(address, timeout.Token);
            if (version is null)
            {
                IsConnected = false;
                ConnectionText = "Ollama läuft nicht";
                ConnectionHelp = OllamaClient.NotRunningHelp(address);
                Models.Clear();
                return;
            }

            var models = await _context.Ollama.ListModelsAsync(address, timeout.Token);
            Models.Clear();
            foreach (var model in models) Models.Add(model);
            IsConnected = true;
            var hasModel = models.Any(m => m.Name == ModelName || m.Name == ModelName + ":latest");
            ConnectionText = $"Ollama {version} · {models.Count} Modell(e)";
            ConnectionHelp = models.Count == 0
                ? $"Ollama läuft, aber es ist noch kein Modell installiert.\n\nLade eines im Terminal, z. B.:\nollama pull {ModelName}"
                : hasModel ? "" : $"Das eingestellte Modell „{ModelName}“ ist nicht installiert. Wähle oben ein installiertes Modell oder lade es mit:\nollama pull {ModelName}";
        }
        catch (Exception e) when (e is OllamaException or OperationCanceledException)
        {
            IsConnected = false;
            ConnectionText = "Ollama läuft nicht";
            ConnectionHelp = OllamaClient.NotRunningHelp(_context.Current.ApiAddress);
        }
        finally
        {
            _checking = false;
        }
    }

    private ChatViewModel CreateChatVm(TutorChat chat)
    {
        var vm = new ChatViewModel(chat, _context, _dialogs, s => Status = s);
        vm.Changed += () => ChatList?.Refresh();
        return vm;
    }

    private ChatViewModel AddChat()
    {
        var vm = CreateChatVm(new TutorChat { Model = _context.Current.Model });
        Chats.Insert(0, vm);
        return vm;
    }

    private void NewChat()
    {
        // Reuse an empty chat instead of piling up "Neuer Chat" entries.
        Selected = Chats.FirstOrDefault(c => c.IsEmpty) ?? AddChat();
        Search = "";
    }

    private void DeleteSelected()
    {
        if (Selected is not { } chat || !_dialogs.Confirm($"Chat „{chat.Title}“ wirklich löschen?")) return;
        try { _context.Storage.Delete(chat.Chat); }
        catch (IOException e) { Status = $"Löschen fehlgeschlagen: {e.Message}"; return; }
        Chats.Remove(chat);
        Selected = Chats.FirstOrDefault() ?? AddChat();
    }
}
