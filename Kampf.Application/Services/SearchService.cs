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
    private readonly DocumentPageIndex? _pages;

    public SearchService(
        IVaultRepository repo,
        NoteEmbeddingIndex? index = null,
        DocumentPageIndex? pages = null)
    {
        _repo = repo;
        _index = index;
        _pages = pages;
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

        // The paper side: metadata matches from the document table plus the
        // pages whose text matched. One row per document, preferring the page
        // hit because it names where the match is.
        var pageHits = await _repo.SearchPagesAsync(text, limit);
        lexical.AddRange(pageHits);

        if (tag is not null)
        {
            var allowed = (await _repo.ListNotesAsync(ct))
                .Where(n => n.Tags.Any(t => t.Name.Equals(tag, StringComparison.OrdinalIgnoreCase)))
                .Select(n => n.Id)
                .ToHashSet(StringComparer.Ordinal);

            // A tag filter narrows notes; papers carry no tags, so they stay.
            lexical = lexical
                .Where(h => h.Kind != "note" || allowed.Contains(h.Id))
                .ToList();
        }

        var results = DedupeDocuments(lexical).Take(limit).ToList();

        if (_index is null && _pages is null)
            return new SearchOutcome(results, 0);

        try
        {
            var semantic = _index is null
                ? Array.Empty<SearchHit>()
                : await SemanticNotesAsync(text, tag, results, ct);

            var semanticPages = Array.Empty<SearchHit>();

            if (_pages is not null)
            {
                var documents = (await _repo.ListDocumentsAsync(ct))
                    .ToDictionary(d => d.Id, StringComparer.Ordinal);

                var seenDocuments = results
                    .Where(h => h.Kind == "document")
                    .Select(h => h.Id)
                    .ToHashSet(StringComparer.Ordinal);

                semanticPages = (await _pages.SemanticAsync(text, documents, seenDocuments, ct)).ToArray();
            }

            var merged = (IReadOnlyList<SearchHit>)results.Concat(semantic).Concat(semanticPages).ToList();
            return new SearchOutcome(merged, semantic.Count + semanticPages.Length);
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

    /// <summary>
    /// Collapses several hits of the same paper into the most specific one, so a
    /// paper that matched by title and by page text appears once, at its page.
    /// </summary>
    private static IEnumerable<SearchHit> DedupeDocuments(IEnumerable<SearchHit> hits)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SearchHit>();
        var byDocument = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var hit in hits)
        {
            if (hit.Kind == "note")
            {
                if (seen.Add(hit.Id))
                    result.Add(hit);
                continue;
            }

            if (byDocument.TryGetValue(hit.Id, out var index))
            {
                var existing = result[index];
                if (existing.Page is null && hit.Page is not null)
                    result[index] = hit; // a page match is the more useful hit
                continue;
            }

            byDocument[hit.Id] = result.Count;
            result.Add(hit);
        }

        return result;
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
