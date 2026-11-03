using System.Net.Http.Json;
using Kampf.Application.Abstractions;
using Kampf.Application.Embeddings;

namespace Kampf.Infrastructure.Embeddings;

/// <summary>
/// Local embeddings from an Ollama server (privacy-first: the model runs on the
/// same machine). Opt in with <c>KAMPF_OLLAMA_URL</c> and
/// <c>KAMPF_EMBEDDING_MODEL</c>; without them the offline hashing embedder is used.
/// </summary>
public sealed class OllamaEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _modelName;

    public OllamaEmbeddingProvider(Uri baseAddress, string modelName)
    {
        _http = new HttpClient
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromMinutes(2),
        };
        _modelName = modelName;
    }

    /// <summary>Tagged with the runtime so vectors from different models never mix.</summary>
    public string Model => $"ollama:{_modelName}";

    /// <summary>Semantic models agree on related prose far more than lexical ones.</summary>
    public float SimilarityFloor => 0.55f;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default)
    {
        if (texts.Count == 0)
            return Array.Empty<float[]>();

        var batched = await TryEmbedBatchAsync(texts, ct);
        if (batched is not null)
            return batched;

        // Older Ollama builds only expose the single-prompt endpoint.
        var results = new List<float[]>(texts.Count);
        foreach (var text in texts)
        {
            var response = await _http.PostAsJsonAsync(
                "api/embeddings",
                new { model = _modelName, prompt = text },
                ct);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<LegacyResponse>(ct);
            var vector = payload?.Embedding ?? Array.Empty<float>();
            VectorMath.NormalizeInPlace(vector);
            results.Add(vector);
        }

        return results;
    }

    private async Task<IReadOnlyList<float[]>?> TryEmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct)
    {
        try
        {
            var response = await _http.PostAsJsonAsync(
                "api/embed",
                new { model = _modelName, input = texts },
                ct);

            if (!response.IsSuccessStatusCode)
                return null;

            var payload = await response.Content.ReadFromJsonAsync<BatchResponse>(ct);
            if (payload?.Embeddings is not { Count: > 0 } vectors)
                return null;

            foreach (var vector in vectors)
                VectorMath.NormalizeInPlace(vector);

            return vectors;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // server too slow: let the caller surface it
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed record BatchResponse(List<float[]>? Embeddings);

    private sealed record LegacyResponse(float[]? Embedding);
}

/// <summary>Chooses the local embedding backend from the environment.</summary>
public static class EmbeddingProviders
{
    /// <summary>
    /// <c>KAMPF_OLLAMA_URL</c> + <c>KAMPF_EMBEDDING_MODEL</c> select Ollama;
    /// otherwise the dependency-free hashing embedder keeps the vault useful offline.
    /// </summary>
    public static IEmbeddingProvider CreateFromEnvironment()
    {
        var url = Environment.GetEnvironmentVariable("KAMPF_OLLAMA_URL");
        var model = Environment.GetEnvironmentVariable("KAMPF_EMBEDDING_MODEL");

        if (!string.IsNullOrWhiteSpace(url) &&
            !string.IsNullOrWhiteSpace(model) &&
            Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var baseAddress))
        {
            return new OllamaEmbeddingProvider(baseAddress, model.Trim());
        }

        return new HashingEmbeddingProvider();
    }
}
