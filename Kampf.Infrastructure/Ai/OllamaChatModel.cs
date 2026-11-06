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

    /// <summary>The models this Ollama server has pulled.</summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(
        Uri baseAddress,
        CancellationToken ct = default)
    {
        using var http = new HttpClient
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(15),
        };

        var tags = await http.GetFromJsonAsync<TagsResponse>("api/tags", ct);

        return (tags?.Models ?? Array.Empty<TagEntry>())
            .Select(entry => entry.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToList();
    }

    /// <summary>
    /// Asks the server which models it has, and whether the configured one is
    /// among them: an unpulled model is the most common reason an answer fails.
    /// </summary>
    public async Task<string?> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            var pulled = (await ListModelsAsync(_http.BaseAddress!, ct))
                .Any(name => Matches(name, _modelName));

            return pulled
                ? null
                : $"model \u201c{_modelName}\u201d is not pulled \u2014 run: ollama pull {_modelName}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"cannot reach Ollama at {_http.BaseAddress}: {ex.Message}";
        }
    }

    /// <summary>Ollama reports <c>name:tag</c>; either side may omit the tag.</summary>
    private static bool Matches(string pulled, string wanted) =>
        pulled.Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
        pulled.StartsWith(wanted + ":", StringComparison.OrdinalIgnoreCase) ||
        pulled.Split(':')[0].Equals(wanted, StringComparison.OrdinalIgnoreCase);

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

    private sealed record TagsResponse(TagEntry[]? Models);

    private sealed record TagEntry(string? Name);
}

/// <summary>Chooses the local chat backend from the environment.</summary>
public static class ChatModels
{
    /// <summary>
    /// <c>KAMPF_CHAT_URL</c> selects an OpenAI-compatible server (LM Studio,
    /// llama.cpp, vLLM); otherwise <c>KAMPF_OLLAMA_URL</c> selects Ollama. Both
    /// need <c>KAMPF_CHAT_MODEL</c>. With neither, chat stays extractive: cited
    /// passages and no synthesis.
    /// </summary>
    public static IChatModel? CreateFromEnvironment() => Create(
        Environment.GetEnvironmentVariable("KAMPF_CHAT_URL"),
        Environment.GetEnvironmentVariable("KAMPF_OLLAMA_URL"),
        Environment.GetEnvironmentVariable("KAMPF_CHAT_MODEL"));

    /// <summary>The choice itself, so it can be reasoned about without the environment.</summary>
    /// <summary>
    /// The environment fallback: <c>KAMPF_CHAT_URL</c> beats <c>KAMPF_OLLAMA_URL</c>,
    /// and both need <c>KAMPF_CHAT_MODEL</c>.
    /// </summary>
    public static IChatModel? Create(string? chatUrl, string? ollamaUrl, string? model) =>
        Create(ChatProtocol.OpenAiCompatible, chatUrl, model) ??
        Create(ChatProtocol.Ollama, ollamaUrl, model);

    /// <summary>The model for one explicit configuration, or null when it is incomplete.</summary>
    public static IChatModel? Create(ChatProtocol protocol, string? url, string? model)
    {
        if (string.IsNullOrWhiteSpace(model) || !TryEndpoint(url, protocol, out var address))
            return null;

        return protocol == ChatProtocol.Ollama
            ? new OllamaChatModel(address, model.Trim())
            : new OpenAiCompatibleChatModel(address, model.Trim());
    }

    /// <summary>
    /// The API root for a server, filling in <c>/v1</c> for OpenAI-shaped ones so
    /// <c>http://localhost:1234</c> and <c>http://localhost:1234/v1</c> both work.
    /// </summary>
    public static bool TryEndpoint(string? url, ChatProtocol protocol, out Uri address)
    {
        address = null!;

        if (string.IsNullOrWhiteSpace(url))
            return false;

        var text = url.Trim().TrimEnd('/');

        if (protocol == ChatProtocol.OpenAiCompatible &&
            !text.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            text += "/v1";
        }

        return Uri.TryCreate(text + "/", UriKind.Absolute, out address);
    }

    /// <summary>Which models the server serves, for the Settings picker.</summary>
    public static async Task<IReadOnlyList<string>> ListAsync(
        ChatProtocol protocol,
        string? url,
        CancellationToken ct = default)
    {
        if (!TryEndpoint(url, protocol, out var address))
            return Array.Empty<string>();

        return protocol == ChatProtocol.Ollama
            ? await OllamaChatModel.ListModelsAsync(address, ct)
            : await OpenAiCompatibleChatModel.ListModelsAsync(address, ct);
    }
}
