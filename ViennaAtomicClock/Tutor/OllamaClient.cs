using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ViennaAtomicClock.Tutor;

/// <summary>Problem talking to Ollama, with a message a student can act on.</summary>
public sealed class OllamaException : Exception
{
    public OllamaException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Minimal client for the local Ollama HTTP API (version, model list, streamed chat).</summary>
public sealed class OllamaClient
{
    private readonly HttpClient _http;

    public OllamaClient(HttpClient http) => _http = http;

    public static string NotRunningHelp(string address) =>
        $"Ollama ist unter {address} nicht erreichbar.\n\n" +
        "So behebst du das:\n" +
        "1. Ollama installieren (ollama.com, einmalig mit Internet).\n" +
        "2. Ollama starten (Startmenü → Ollama, oder im Terminal: ollama serve).\n" +
        "3. Ein Modell laden, z. B.: ollama pull qwen2.5-coder:7b\n" +
        "4. Hier auf „Erneut prüfen“ klicken.";

    public async Task<string?> GetVersionAsync(string address, CancellationToken cancellation)
    {
        try
        {
            using var response = await _http.GetAsync(Url(address, "api/version"), cancellation);
            if (!response.IsSuccessStatusCode) return null;
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation));
            return json?["version"]?.GetValue<string>() ?? "?";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException { CancellationToken.IsCancellationRequested: false } or JsonException or UriFormatException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<LocalModelInfo>> ListModelsAsync(string address, CancellationToken cancellation)
    {
        try
        {
            using var response = await _http.GetAsync(Url(address, "api/tags"), cancellation);
            response.EnsureSuccessStatusCode();
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation));
            var models = new List<LocalModelInfo>();
            foreach (var model in json?["models"]?.AsArray() ?? new JsonArray())
            {
                if (model is null) continue;
                models.Add(new LocalModelInfo(
                    model["name"]?.GetValue<string>() ?? "",
                    model["size"]?.GetValue<long>() ?? 0,
                    model["details"]?["parameter_size"]?.GetValue<string>() ?? "",
                    model["details"]?["quantization_level"]?.GetValue<string>() ?? ""));
            }
            return models.Where(m => m.Name.Length > 0).OrderBy(m => m.Name).ToList();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException { CancellationToken.IsCancellationRequested: false } or UriFormatException)
        {
            throw new OllamaException(NotRunningHelp(address), e);
        }
    }

    /// <summary>Streams the answer token by token. Cancelling the token stops the request.</summary>
    public async IAsyncEnumerable<string> ChatStreamAsync(
        string address, string model, IReadOnlyList<PromptMessage> messages, double temperature, int contextTokens,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = true,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
            ["options"] = new JsonObject { ["temperature"] = temperature, ["num_ctx"] = contextTokens }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, Url(address, "api/chat"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        }
        catch (Exception e) when (e is HttpRequestException or UriFormatException)
        {
            throw new OllamaException(NotRunningHelp(address), e);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadError(response, cancellation);
                if ((int)response.StatusCode == 404 || error.Contains("not found", StringComparison.OrdinalIgnoreCase))
                    throw new OllamaException($"Das Modell „{model}“ ist nicht installiert.\n\nLade es im Terminal mit:\nollama pull {model}\n\noder wähle in den Einstellungen ein installiertes Modell.");
                throw new OllamaException($"Ollama hat einen Fehler gemeldet: {error}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (await reader.ReadLineAsync(cancellation) is { } line)
            {
                if (line.Length == 0) continue;
                var chunk = JsonNode.Parse(line);
                if (chunk?["error"]?.GetValue<string>() is { } streamError)
                    throw new OllamaException($"Ollama hat einen Fehler gemeldet: {streamError}");
                var token = chunk?["message"]?["content"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(token)) yield return token;
                if (chunk?["done"]?.GetValue<bool>() == true) yield break;
            }
        }
    }

    private static async Task<string> ReadError(HttpResponseMessage response, CancellationToken cancellation)
    {
        var text = await response.Content.ReadAsStringAsync(cancellation);
        try
        {
            return JsonNode.Parse(text)?["error"]?.GetValue<string>() ?? text;
        }
        catch (JsonException)
        {
            return string.IsNullOrWhiteSpace(text) ? response.ReasonPhrase ?? "unbekannter Fehler" : text;
        }
    }

    private static Uri Url(string address, string path) => new(new Uri(address.TrimEnd('/') + "/"), path);
}
