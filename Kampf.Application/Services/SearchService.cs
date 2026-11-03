using Kampf.Application.Abstractions;
using Kampf.Application.Embeddings;
using Kampf.Application.Models;
using Kampf.Domain.Entities;

namespace Kampf.Application.Services;

/// <summary>
/// Search outcome: the ranked hits plus how many of them matched by meaning
/// rather than by literal text (and a warning when the semantic half was skipped).
/// </summary>
public sealed record SearchOutcome(
    IReadOnlyList<SearchHit> Hits,
    int SemanticOnly,
    string? Warning = null);

/// <summary>
/// Hybrid search: SQLite FTS5 for exact terms, then embedding similarity for
/// notes that mean the same thing without sharing the words. Exact-term hits
/// always come first, and a tag: filter still narrows both halves.
/// </summary>
public sealed class SearchService
{
    private const int MaxSemanticHits = 10;

    private readonly IVaultRepository _repo;
    private readonly NoteEmbeddingIndex? _index;

    public SearchService(IVaultRepository repo, NoteEmbeddingIndex? index = null)
    {
        _repo = repo;
        _index = index;
    }

    public async Task<SearchOutcome> SearchAsync(
        string? query,
        int limit = 50,
        CancellationToken ct = default)
    {
        var (text, tag) = ParseQuery(query);

        if (text.Length == 0 && tag is null)
            return new SearchOutcome(Array.Empty<SearchHit>(), 0);

        if (text.Length == 0)
        {
            // Tag-only query: list what carries that tag.
            var tagged = await _repo.ListNotesAsync(ct);
            var hits = tagged
                .Where(n => n.Tags.Any(t => t.Name.Equals(tag, StringComparison.OrdinalIgnoreCase)))
                .Select(n => new SearchHit(n.Id, "note", n.Title, Preview(n.Body)))
                .Take(limit)
                .ToList();
            return new SearchOutcome(hits, 0);
        }

        var lexical = (await _repo.SearchAsync(text, limit * 3, ct)).ToList();

        if (tag is not null)
        {
            var allowed = (await _repo.ListNotesAsync(ct))
                .Where(n => n.Tags.Any(t => t.Name.Equals(tag, StringComparison.OrdinalIgnoreCase)))
                .Select(n => n.Id)
                .ToHashSet(StringComparer.Ordinal);

            lexical = lexical
                .Where(h => h.Kind != "note" || allowed.Contains(h.Id))
                .ToList();
        }

        var results = lexical.Take(limit).ToList();

        if (_index is null)
            return new SearchOutcome(results, 0);

        try
        {
            var semantic = await SemanticNotesAsync(text, tag, results, ct);
            return new SearchOutcome(results.Concat(semantic).ToList(), semantic.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A stopped local model must not break search; exact matches still work.
            return new SearchOutcome(results, 0, "meaning-based matches unavailable: " + ex.Message);
        }
    }

    /// <summary>
    /// Notes whose vector is close to the query vector and which the text search
    /// did not already return. These are appended, never reordered above exact hits.
    /// </summary>
    private async Task<IReadOnlyList<SearchHit>> SemanticNotesAsync(
        string text,
        string? tag,
        IReadOnlyList<SearchHit> alreadyFound,
        CancellationToken ct)
    {
        var notes = await _repo.ListNotesAsync(ct);
        if (notes.Count == 0)
            return Array.Empty<SearchHit>();

        var candidates = tag is null
            ? notes
            : notes.Where(n => n.Tags.Any(t => t.Name.Equals(tag, StringComparison.OrdinalIgnoreCase))).ToList();

        var indexed = candidates
            .Where(n => TextTokenizer.DistinctTokens(NoteEmbeddingIndex.TextFor(n)).Count >= 3)
            .ToList();
        if (indexed.Count == 0)
            return Array.Empty<SearchHit>();

        var queryVectors = await _index!.Provider.EmbedAsync(new[] { text }, ct);
        if (queryVectors.Count == 0 || queryVectors[0].Length == 0)
            return Array.Empty<SearchHit>();

        var queryVector = queryVectors[0];
        VectorMath.NormalizeInPlace(queryVector);

        var vectors = await _index.EnsureAsync(
            indexed.Select(n => (n.Id, NoteEmbeddingIndex.TextFor(n))).ToList(),
            ct);

        var seen = alreadyFound
            .Where(h => h.Kind == "note")
            .Select(h => h.Id)
            .ToHashSet(StringComparer.Ordinal);

        var floor = _index.Provider.SimilarityFloor;

        return indexed
            .Select(note => (Note: note, Score: VectorMath.Cosine(queryVector, vectors[note.Id])))
            .Where(candidate => candidate.Score >= floor && !seen.Contains(candidate.Note.Id))
            .OrderByDescending(candidate => candidate.Score)
            .Take(MaxSemanticHits)
            .Select(candidate => new SearchHit(
                candidate.Note.Id,
                "note",
                candidate.Note.Title,
                Preview(candidate.Note.Body)))
            .ToList();
    }

    private static string Preview(string body)
    {
        var flat = body.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length > 140 ? flat[..140] + "\u2026" : flat;
    }

    public static (string Text, string? Tag) ParseQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return (string.Empty, null);

        string? tag = null;
        var words = new List<string>();

        foreach (var token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
            {
                var value = token[4..].TrimStart('#');
                if (value.Length > 0)
                    tag = Tag.Normalize(value);
                continue;
            }

            words.Add(token);
        }

        return (string.Join(' ', words).Trim(), tag);
    }
}
