using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Kampf.Application.Abstractions;

namespace Kampf.Infrastructure.Ai;

/// <summary>
/// Chat completions from a local Ollama server, so the vault never leaves the
/// machine. Opt in with <c>KAMPF_OLLAMA_URL</c> and <c>KAMPF_CHAT_MODEL</c>;
/// without them Idea-Craft answers with cited passages instead of prose.
/// </summary>
public sealed class OllamaChatModel : IChatModel, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _modelName;

    public OllamaChatModel(Uri baseAddress, string modelName)
    {
        _http = new HttpClient
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromMinutes(3),
        };
        _modelName = modelName;
    }

    public string Name => $"ollama:{_modelName}";

    public async Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("api/chat", Request(systemPrompt, userPrompt, stream: false), ct);

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<ChatResponse>(ct);
        return payload?.Message?.Content ?? string.Empty;
    }

    /// <summary>
    /// NDJSON stream from <c>api/chat</c>: one JSON object per line, each carrying
    /// the next token(s) in <c>message.content</c> until <c>done</c> is true.
    /// </summary>
    public async IAsyncEnumerable<string> StreamAsync(
        string systemPrompt,
        string userPrompt,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat")
        {
            Content = JsonContent.Create(Request(systemPrompt, userPrompt, stream: true)),
        };

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            ChatResponse? update;
            try
            {
                update = JsonSerializer.Deserialize<ChatResponse>(line, JsonOptions);
            }
            catch (JsonException)
            {
                continue; // a partial or unexpected line must not end the answer
            }

            var content = update?.Message?.Content;
            if (!string.IsNullOrEmpty(content))
                yield return content;

            if (update?.Done == true)
                break;
        }
    }

    private object Request(string systemPrompt, string userPrompt, bool stream) => new
    {
        model = _modelName,
        stream,
        options = new { temperature = 0.2 },
        messages = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userPrompt },
        },
    };

    public void Dispose() => _http.Dispose();

    private sealed record ChatResponse(ChatBody? Message, bool Done = false);

    private sealed record ChatBody(string? Content);
}

/// <summary>Chooses the local chat backend from the environment.</summary>
public static class ChatModels
{
    /// <summary>
    /// <c>KAMPF_OLLAMA_URL</c> + <c>KAMPF_CHAT_MODEL</c> select Ollama; otherwise
    /// returns null and chat stays extractive (cited passages, no synthesis).
    /// </summary>
    public static IChatModel? CreateFromEnvironment()
    {
        var url = Environment.GetEnvironmentVariable("KAMPF_OLLAMA_URL");
        var model = Environment.GetEnvironmentVariable("KAMPF_CHAT_MODEL");

        if (!string.IsNullOrWhiteSpace(url) &&
            !string.IsNullOrWhiteSpace(model) &&
            Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var baseAddress))
        {
            return new OllamaChatModel(baseAddress, model.Trim());
        }

        return null;
    }
}
