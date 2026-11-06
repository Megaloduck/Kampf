using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Kampf.Application.Abstractions;

namespace Kampf.Infrastructure.Ai;

/// <summary>
/// Chat completions from any OpenAI-compatible local server (LM Studio,
/// llama.cpp's server, vLLM, text-generation-webui and friends), so the vault
/// never leaves the machine. Opt in with <c>KAMPF_CHAT_URL</c> and
/// <c>KAMPF_CHAT_MODEL</c>; the URL is the API root, e.g.
/// <c>http://localhost:1234/v1</c>.
/// </summary>
public sealed class OpenAiCompatibleChatModel : IChatModel, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _modelName;

    public OpenAiCompatibleChatModel(Uri baseAddress, string modelName, HttpMessageHandler? handler = null)
    {
        _http = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: true);

        _http.BaseAddress = baseAddress;
        _http.Timeout = TimeSpan.FromMinutes(3);
        _modelName = modelName;
    }

    public string Name => $"openai-compatible:{_modelName}";

    public async Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(
            "chat/completions",
            Request(systemPrompt, userPrompt, stream: false),
            ct);

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<CompletionResponse>(ct);
        return payload?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
    }

    /// <summary>
    /// Server-sent events: <c>data: {...}</c> lines carrying a chunk each, ending
    /// with <c>data: [DONE]</c>. Comments and unknown lines are skipped rather
    /// than treated as the end of the answer.
    /// </summary>
    public async IAsyncEnumerable<string> StreamAsync(
        string systemPrompt,
        string userPrompt,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(Request(systemPrompt, userPrompt, stream: true)),
        };

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var data = line[5..].Trim();

            if (data.Length == 0)
                continue;

            if (data == "[DONE]")
                break;

            ChunkResponse? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<ChunkResponse>(data, JsonOptions);
            }
            catch (JsonException)
            {
                continue; // a partial or unexpected payload must not end the answer
            }

            var content = chunk?.Choices?.FirstOrDefault()?.Delta?.Content;
            if (!string.IsNullOrEmpty(content))
                yield return content;
        }
    }

    /// <summary>The model ids this server reports.</summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(
        Uri baseAddress,
        CancellationToken ct = default)
    {
        using var http = new HttpClient
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(15),
        };

        var payload = await http.GetFromJsonAsync<ModelsResponse>("models", ct);

        return (payload?.Data ?? Array.Empty<ModelEntry>())
            .Select(entry => entry.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToList();
    }

    /// <summary>Lists the server's models and checks the configured one is there.</summary>
    public async Task<string?> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            var available = await ListModelsAsync(_http.BaseAddress!, ct);

            if (available.Count == 0)
                return $"the server at {_http.BaseAddress} reports no models";

            return available.Any(id => id.Equals(_modelName, StringComparison.OrdinalIgnoreCase))
                ? null
                : $"model \u201c{_modelName}\u201d is not served \u2014 available: {string.Join(", ", available.Take(5))}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"cannot reach the server at {_http.BaseAddress}: {ex.Message}";
        }
    }

    private object Request(string systemPrompt, string userPrompt, bool stream) => new
    {
        model = _modelName,
        stream,
        temperature = 0.2,
        messages = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userPrompt },
        },
    };

    public void Dispose() => _http.Dispose();

    private sealed record CompletionResponse(Choice[]? Choices);

    private sealed record ChunkResponse(ChunkChoice[]? Choices);

    private sealed record Choice(Body? Message);

    private sealed record ChunkChoice(Body? Delta);

    private sealed record Body(string? Content);

    private sealed record ModelsResponse(ModelEntry[]? Data);

    private sealed record ModelEntry(string? Id);
}
