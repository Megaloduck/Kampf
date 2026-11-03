using System.Security.Cryptography;
using System.Text;
using Kampf.Application.Abstractions;
using Kampf.Application.Embeddings;
using Kampf.Application.Models;
using Kampf.Domain.Entities;

namespace Kampf.Application.Services;

/// <summary>
/// Keeps a cached vector per note. The content hash means a note is only sent to
/// the embedding backend when its text actually changed, and the cache is tagged
/// with the model so vectors from different backends are never compared.
/// </summary>
public sealed class NoteEmbeddingIndex
{
    private const int EmbeddingBatchSize = 32;

    private readonly IVaultRepository _repo;
    private readonly IEmbeddingProvider _embeddings;

    public NoteEmbeddingIndex(IVaultRepository repo, IEmbeddingProvider embeddings)
    {
        _repo = repo;
        _embeddings = embeddings;
    }

    public IEmbeddingProvider Provider => _embeddings;

    /// <summary>The text a note is indexed by: its title and body.</summary>
    public static string TextFor(Note note) => note.Title + "\n" + note.Body;

    /// <summary>
    /// Returns unit-length vectors for the given notes, embedding only the ones
    /// that are new, edited, or not yet stored.
    /// </summary>
    public async Task<Dictionary<string, float[]>> EnsureAsync(
        IReadOnlyList<(string Id, string Text)> items,
        CancellationToken ct = default)
    {
        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
        if (items.Count == 0)
            return vectors;

        var stored = (await _repo.ListEmbeddingsAsync(_embeddings.Model, ct))
            .Where(e => e.Vector.Length > 0)
            .ToDictionary(e => e.NoteId, StringComparer.Ordinal);

        var stale = new List<(string Id, string Text, string Hash)>();

        foreach (var (id, text) in items)
        {
            var hash = ContentHash(text);
            if (stored.TryGetValue(id, out var cached) && cached.ContentHash == hash)
            {
                VectorMath.NormalizeInPlace(cached.Vector);
                vectors[id] = cached.Vector;
                continue;
            }

            stale.Add((id, text, hash));
        }

        foreach (var batch in stale.Chunk(EmbeddingBatchSize))
        {
            var embedded = await _embeddings.EmbedAsync(batch.Select(b => b.Text).ToList(), ct);
            var rows = new List<NoteEmbedding>(batch.Length);

            for (var k = 0; k < batch.Length; k++)
            {
                var vector = k < embedded.Count ? embedded[k] : Array.Empty<float>();
                VectorMath.NormalizeInPlace(vector);

                var (id, _, hash) = batch[k];
                vectors[id] = vector;
                rows.Add(new NoteEmbedding(id, _embeddings.Model, hash, vector));
            }

            await _repo.SaveEmbeddingsAsync(rows, ct);
        }

        return vectors;
    }

    private static string ContentHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
