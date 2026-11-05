using System.Security.Cryptography;
using System.Text;
using Kampf.Application.Abstractions;
using Kampf.Application.Embeddings;
using Kampf.Application.Models;
using Kampf.Domain.Entities;

namespace Kampf.Application.Services;

/// <summary>
/// Stores and embeds the text of imported papers. A paper's body is what a
/// researcher wants to search, but only the notes written about it used to be
/// indexed; this closes that gap for search and for Idea-Craft.
///
/// Text is extracted once per paper and kept in the database. Vectors are
/// rebuilt only for pages whose text changed (or that have none for the active
/// model), and a single run is capped so a large import does not block the UI
/// for minutes.
/// </summary>
public sealed class DocumentPageIndex
{
    private const int EmbeddingBatchSize = 24;
    private const int MaxEmbeddedPagesPerRun = 240;
    private const int MinPageCharacters = 80;
    private const int MaxSemanticPages = 6;

    private readonly IVaultRepository _repo;
    private readonly IPdfExtractor _extractor;
    private readonly IEmbeddingProvider _embeddings;

    public DocumentPageIndex(
        IVaultRepository repo,
        IPdfExtractor extractor,
        IEmbeddingProvider embeddings)
    {
        _repo = repo;
        _extractor = extractor;
        _embeddings = embeddings;
    }

    public IEmbeddingProvider Provider => _embeddings;

    /// <summary>
    /// Stores one paper's page text the first time it is read. Pass
    /// <paramref name="pages"/> when they were just extracted, so the PDF is not
    /// parsed twice. Returns true when something was stored.
    /// </summary>
    public async Task<bool> EnsureTextAsync(
        PdfDocument document,
        IReadOnlyList<PdfPageText>? pages = null,
        CancellationToken ct = default)
    {
        var stored = await _repo.ListDocumentPagesAsync(document.Id, ct);
        if (stored.Count > 0)
            return false;

        pages ??= await ExtractAsync(document, ct);
        if (pages is null || pages.Count == 0)
            return false;

        await _repo.SaveDocumentPagesAsync(
            pages.Select(p => new DocumentPage(document.Id, p.Page, p.Text)),
            ct);

        return true;
    }

    /// <summary>
    /// Brings page text and vectors up to date for the given papers: papers
    /// imported before page text was stored are extracted, and pages that are
    /// new, edited or unindexed for the active model are embedded. Returns the
    /// number of pages embedded, so the caller can report progress honestly.
    /// </summary>
    public async Task<int> IndexAsync(IReadOnlyList<PdfDocument> documents, CancellationToken ct = default)
    {
        foreach (var document in documents)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await EnsureTextAsync(document, null, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // A PDF that moved or cannot be parsed must not stop the backfill.
            }
        }

        var stored = (await _repo.ListPageEmbeddingsAsync(_embeddings.Model, ct))
            .ToDictionary(e => (e.DocumentId, e.Page));

        var stale = new List<(DocumentPage Page, string Hash)>();

        foreach (var document in documents)
        {
            if (stale.Count >= MaxEmbeddedPagesPerRun)
                break;

            foreach (var page in await _repo.ListDocumentPagesAsync(document.Id, ct))
            {
                if (stale.Count >= MaxEmbeddedPagesPerRun)
                    break;

                var text = page.IndexText;
                if (text.Length < MinPageCharacters)
                    continue; // cover pages, blank pages, page-number only pages

                var hash = ContentHash(text);
                if (stored.TryGetValue((page.DocumentId, page.Page), out var existing) &&
                    existing.ContentHash == hash)
                {
                    continue;
                }

                stale.Add((page, hash));
            }
        }

        var embedded = 0;

        foreach (var batch in stale.Chunk(EmbeddingBatchSize))
        {
            var vectors = await _embeddings.EmbedAsync(
                batch.Select(b => b.Page.IndexText).ToList(),
                ct);

            var rows = new List<PageEmbedding>(batch.Length);
            for (var i = 0; i < batch.Length; i++)
            {
                var vector = i < vectors.Count ? vectors[i] : Array.Empty<float>();
                if (vector.Length == 0)
                    continue;

                VectorMath.NormalizeInPlace(vector);
                rows.Add(new PageEmbedding(
                    batch[i].Page.DocumentId,
                    batch[i].Page.Page,
                    _embeddings.Model,
                    batch[i].Hash,
                    vector));
            }

            await _repo.SavePageEmbeddingsAsync(rows, ct);
            embedded += rows.Count;
        }

        return embedded;
    }

    /// <summary>
    /// Pages whose vector is close to the query and whose paper the text search
    /// did not already return. Each hit names the page, so the reader can open
    /// the paper where it matched.
    /// </summary>
    public async Task<IReadOnlyList<SearchHit>> SemanticAsync(
        string query,
        IReadOnlyDictionary<string, PdfDocument> documents,
        IReadOnlyCollection<string> alreadyFound,
        CancellationToken ct = default)
    {
        if (documents.Count == 0)
            return Array.Empty<SearchHit>();

        var stored = await _repo.ListPageEmbeddingsAsync(_embeddings.Model, ct);
        if (stored.Count == 0)
            return Array.Empty<SearchHit>();

        var queryVectors = await _embeddings.EmbedAsync(new[] { query }, ct);
        if (queryVectors.Count == 0 || queryVectors[0].Length == 0)
            return Array.Empty<SearchHit>();

        var queryVector = queryVectors[0];
        VectorMath.NormalizeInPlace(queryVector);

        var floor = _embeddings.SimilarityFloor;

        var matches = stored
            .Where(e => documents.ContainsKey(e.DocumentId) && !alreadyFound.Contains(e.DocumentId))
            .Select(e => (Embedding: e, Score: VectorMath.Cosine(queryVector, e.Vector)))
            .Where(candidate => candidate.Score >= floor)
            .OrderByDescending(candidate => candidate.Score)
            .Take(MaxSemanticPages)
            .ToList();

        if (matches.Count == 0)
            return Array.Empty<SearchHit>();

        var snippets = new Dictionary<(string DocumentId, int Page), string>();
        foreach (var group in matches.GroupBy(m => m.Embedding.DocumentId))
        {
            foreach (var page in await _repo.ListDocumentPagesAsync(group.Key, ct))
                snippets[(page.DocumentId, page.Page)] = Preview(page.Text);
        }

        return matches
            .Select(match =>
            {
                var document = documents[match.Embedding.DocumentId];
                var snippet = snippets.GetValueOrDefault((document.Id, match.Embedding.Page)) ?? string.Empty;
                return new SearchHit(
                    document.Id,
                    "document",
                    document.Title,
                    $"p. {match.Embedding.Page} \u00b7 {snippet}",
                    match.Embedding.Page);
            })
            .ToList();
    }

    /// <summary>Raw text of one page, used when a citation points into a paper.</summary>
    public async Task<string?> PageTextAsync(string documentId, int page, CancellationToken ct = default)
    {
        var pages = await _repo.ListDocumentPagesAsync(documentId, ct);
        return pages.FirstOrDefault(p => p.Page == page)?.Text;
    }

    private async Task<IReadOnlyList<PdfPageText>?> ExtractAsync(PdfDocument document, CancellationToken ct)
    {
        if (!File.Exists(document.FilePath))
            return null;

        var extraction = await _extractor.ExtractAsync(document.FilePath, ct);
        return extraction.Pages;
    }

    private static string Preview(string text)
    {
        var flat = DocumentPage.Flatten(text);
        return flat.Length > 140 ? flat[..140] + "\u2026" : flat;
    }

    internal static string ContentHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
