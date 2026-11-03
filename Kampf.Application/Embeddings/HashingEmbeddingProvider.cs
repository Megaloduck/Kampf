using System.Text;
using Kampf.Application.Abstractions;

namespace Kampf.Application.Embeddings;

/// <summary>
/// Dependency-free embedder: hashes words and word pairs into a fixed-size
/// vector (signed feature hashing, sublinear term frequency). It captures word
/// overlap rather than meaning, which is enough to rank candidates offline and
/// keeps the vault usable with no model installed.
/// </summary>
public sealed class HashingEmbeddingProvider : IEmbeddingProvider
{
    public const int Dimensions = 512;

    public string Model => "kampf-hash-512";

    /// <summary>Lexical overlap of unrelated notes is non-zero, so the floor is low.</summary>
    public float SimilarityFloor => 0.16f;

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var vectors = new float[texts.Count][];
        for (var i = 0; i < texts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            vectors[i] = Embed(texts[i]);
        }

        return Task.FromResult<IReadOnlyList<float[]>>(vectors);
    }

    /// <summary>Embeds one text into a unit-length vector (all zeros when empty).</summary>
    public static float[] Embed(string? text)
    {
        var vector = new float[Dimensions];
        var tokens = TextTokenizer.Tokenize(text);
        if (tokens.Count == 0)
            return vector;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in tokens)
            counts[token] = counts.GetValueOrDefault(token) + 1;

        foreach (var (token, count) in counts)
            Add(vector, token, 1f + MathF.Log(count));

        // Word pairs carry a little more signal than single words, at half weight.
        for (var i = 1; i < tokens.Count; i++)
            Add(vector, tokens[i - 1] + "|" + tokens[i], 0.5f);

        VectorMath.NormalizeInPlace(vector);
        return vector;
    }

    private static void Add(float[] vector, string term, float weight)
    {
        var hash = Fnv1a(term);
        var index = (int)(hash % Dimensions);
        var sign = (hash & (1UL << 63)) == 0 ? 1f : -1f;
        vector[index] += sign * weight;
    }

    private static ulong Fnv1a(string term)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var hash = offset;
        foreach (var b in Encoding.UTF8.GetBytes(term))
        {
            hash ^= b;
            hash *= prime;
        }

        return hash;
    }
}
