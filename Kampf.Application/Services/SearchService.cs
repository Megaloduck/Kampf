using Kampf.Application.Abstractions;
using Kampf.Application.Models;
using Kampf.Domain.Entities;

namespace Kampf.Application.Services;

/// <summary>
/// Full-text search over notes and imported documents, with a simple
/// "tag:foo" filter that runs in memory on top of the FTS results.
/// </summary>
public sealed class SearchService
{
    private readonly IVaultRepository _repo;

    public SearchService(IVaultRepository repo) => _repo = repo;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        string? query,
        int limit = 50,
        CancellationToken ct = default)
    {
        var (text, tag) = ParseQuery(query);

        if (text.Length == 0 && tag is null)
            return Array.Empty<SearchHit>();

        IReadOnlyList<SearchHit> hits;
        if (text.Length == 0)
        {
            // Tag-only query: list what carries that tag.
            var notes = await _repo.ListNotesAsync(ct);
            hits = notes
                .Where(n => n.Tags.Any(t => t.Name.Equals(tag, StringComparison.OrdinalIgnoreCase)))
                .Select(n => new SearchHit(n.Id, "note", n.Title, n.Body.Length > 140 ? n.Body[..140] + "\u2026" : n.Body))
                .Take(limit)
                .ToList();
            return hits;
        }

        hits = await _repo.SearchAsync(text, limit * 3, ct);

        if (tag is null)
            return hits.Take(limit).ToList();

        var allNotes = await _repo.ListNotesAsync(ct);
        var allowed = allNotes
            .Where(n => n.Tags.Any(t => t.Name.Equals(tag, StringComparison.OrdinalIgnoreCase)))
            .Select(n => n.Id)
            .ToHashSet(StringComparer.Ordinal);

        return hits
            .Where(h => h.Kind != "note" || allowed.Contains(h.Id))
            .Take(limit)
            .ToList();
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
