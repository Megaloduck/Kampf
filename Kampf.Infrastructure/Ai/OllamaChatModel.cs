using System.Net.Http.Json;
using Kampf.Application.Abstractions;

namespace Kampf.Infrastructure.Ai;

/// <summary>
/// Chat completions from a local Ollama server, so the vault never leaves the
/// machine. Opt in with <c>KAMPF_OLLAMA_URL</c> and <c>KAMPF_CHAT_MODEL</c>;
/// without them Idea-Craft answers with cited passages instead of prose.
/// </summary>
public sealed class OllamaChatModel : IChatModel, IDisposable
{
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
        var response = await _http.PostAsJsonAsync(
            "api/chat",
            new
            {
                model = _modelName,
                stream = false,
                options = new { temperature = 0.2 },
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt },
                },
            },
            ct);

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<ChatResponse>(ct);
        return payload?.Message?.Content ?? string.Empty;
    }

    public void Dispose() => _http.Dispose();

    private sealed record ChatResponse(ChatBody? Message);

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
