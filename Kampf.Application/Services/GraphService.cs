using Kampf.Application.Abstractions;
using Kampf.Application.Models;
using Kampf.Domain.Entities;
using Kampf.Domain.ValueObjects;

namespace Kampf.Application.Services;

/// <summary>
/// Builds the graph projection. Every node is a captured note or an imported
/// document; every edge is a stored link with a recorded reason.
/// </summary>
public sealed class GraphService
{
    private readonly IVaultRepository _repo;

    public GraphService(IVaultRepository repo) => _repo = repo;

    public async Task<GraphModel> BuildAsync(CancellationToken ct = default)
    {
        var notes = await _repo.ListNotesAsync(ct);
        var documents = await _repo.ListDocumentsAsync(ct);
        var links = await _repo.ListLinksAsync(ct);
        return Build(notes, documents, links);
    }

    public static GraphModel Build(
        IReadOnlyList<Note> notes,
        IReadOnlyList<PdfDocument> documents,
        IReadOnlyList<Link> links)
    {
        var nodes = new List<GraphNode>(notes.Count + documents.Count);
        var yearByDocument = documents.ToDictionary(d => d.Id, d => d.Year, StringComparer.Ordinal);

        foreach (var note in notes)
        {
            int? year = note.SourceDocumentId is not null
                ? yearByDocument.GetValueOrDefault(note.SourceDocumentId)
                : null;

            nodes.Add(new GraphNode(
                note.Id,
                NodeKind.Note,
                note.Title,
                0,
                note.Tags.FirstOrDefault()?.Name,
                year));
        }

        foreach (var doc in documents)
        {
            nodes.Add(new GraphNode(doc.Id, NodeKind.Document, doc.Title, 0, "source", doc.Year));
        }

        var byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var edges = new List<GraphEdge>();
        var seen = new HashSet<(string, string)>();

        // Highlight-to-note provenance is itself a user action, so the note is
        // connected to the paper it was captured from even without a written link.
        foreach (var note in notes)
        {
            if (note.SourceDocumentId is null || !byId.ContainsKey(note.SourceDocumentId))
                continue;
            if (!seen.Add((note.Id, note.SourceDocumentId)))
                continue;

            edges.Add(new GraphEdge(note.Id, note.SourceDocumentId, EdgeKind.Manual));
        }

        foreach (var link in links)
        {
            if (!byId.ContainsKey(link.SourceId) || !byId.ContainsKey(link.TargetId))
                continue;
            if (link.SourceId == link.TargetId)
                continue;
            if (!seen.Add((link.SourceId, link.TargetId)))
                continue;

            edges.Add(new GraphEdge(link.SourceId, link.TargetId, link.Kind));
        }

        var degree = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            degree[edge.SourceId] = degree.GetValueOrDefault(edge.SourceId) + 1;
            degree[edge.TargetId] = degree.GetValueOrDefault(edge.TargetId) + 1;
        }

        var finalNodes = nodes
            .Select(n => n with { Degree = degree.GetValueOrDefault(n.Id) })
            .ToList();

        return new GraphModel { Nodes = finalNodes, Edges = edges };
    }

    /// <summary>
    /// Connected components of the note-to-note graph. Every note maps to the id of
    /// its component's root; an isolated note is its own component. Document nodes
    /// are ignored on purpose: a paper never makes two notes neighbours, so a note
    /// saved without a link or a highlight anchor counts as an orphan.
    /// </summary>
    public static IReadOnlyDictionary<string, string> NoteComponents(
        IReadOnlyList<Note> notes,
        IReadOnlyList<Link> links)
    {
        var parent = notes.ToDictionary(n => n.Id, n => n.Id, StringComparer.Ordinal);

        string Find(string id)
        {
            while (!string.Equals(parent[id], id, StringComparison.Ordinal))
            {
                parent[id] = parent[parent[id]];
                id = parent[id];
            }

            return id;
        }

        void Union(string a, string b)
        {
            var rootA = Find(a);
            var rootB = Find(b);
            if (string.Equals(rootA, rootB, StringComparison.Ordinal))
                return;

            // Smallest ordinal id wins so the result does not depend on link order.
            if (string.CompareOrdinal(rootA, rootB) <= 0)
                parent[rootB] = rootA;
            else
                parent[rootA] = rootB;
        }

        foreach (var link in links)
        {
            if (!parent.ContainsKey(link.SourceId) || !parent.ContainsKey(link.TargetId))
                continue;

            Union(link.SourceId, link.TargetId);
        }

        // Materialised first: path compression mutates the dictionary.
        var ids = notes.Select(n => n.Id).ToArray();
        return ids.ToDictionary(id => id, Find, StringComparer.Ordinal);
    }
}
