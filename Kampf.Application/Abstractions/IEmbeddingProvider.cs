namespace Kampf.Application.Abstractions;

/// <summary>
/// Turns text into vectors for similarity search. Implementations live in
/// infrastructure: an offline hashing embedder (always available) and a local
/// Ollama model (opted into with environment variables).
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>
    /// Identifies the vector space. Stored vectors are tagged with it, so
    /// switching models re-embeds instead of comparing mixed spaces.
    /// </summary>
    string Model { get; }

    /// <summary>
    /// Cosine similarity a pair must reach before a suggestion is worth a
    /// researcher's attention. Lexical embedders score lower than semantic ones.
    /// </summary>
    float SimilarityFloor { get; }

    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}
