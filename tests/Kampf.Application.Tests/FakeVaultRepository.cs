using Kampf.Application.Abstractions;
using Kampf.Application.Models;
using Kampf.Domain.Entities;
using Kampf.Domain.ValueObjects;

namespace Kampf.Application.Tests;

/// <summary>Small in-memory vault used to test the application services.</summary>
public sealed class FakeVaultRepository : IVaultRepository
{
    public Dictionary<string, Note> Notes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, PdfDocument> Documents { get; } = new(StringComparer.Ordinal);
    public List<Link> Links { get; } = new();
    public List<Tag> Tags { get; } = new();
    public Dictionary<string, List<string>> NoteTags { get; } = new(StringComparer.Ordinal);
    public List<Highlight> Highlights { get; } = new();
    public List<Suggestion> Suggestions { get; } = new();
    public List<AuditEntry> Audit { get; } = new();
    public List<ChatMessage> Chat { get; } = new();
    public Dictionary<string, NoteEmbedding> Embeddings { get; } = new(StringComparer.Ordinal);

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<Note>> ListNotesAsync(CancellationToken ct = default)
    {
        foreach (var note in Notes.Values)
        {
            note.Tags.Clear();
            if (NoteTags.TryGetValue(note.Id, out var ids))
                note.Tags.AddRange(Tags.Where(t => ids.Contains(t.Id)));
        }

        return Task.FromResult<IReadOnlyList<Note>>(Notes.Values
            .OrderByDescending(n => n.UpdatedAt)
            .ToList());
    }

    public Task<Note?> GetNoteAsync(string id, CancellationToken ct = default)
    {
        if (!Notes.TryGetValue(id, out var note))
            return Task.FromResult<Note?>(null);

        note.Tags.Clear();
        if (NoteTags.TryGetValue(id, out var ids))
            note.Tags.AddRange(Tags.Where(t => ids.Contains(t.Id)));

        return Task.FromResult<Note?>(note);
    }

    public Task SaveNoteAsync(Note note, CancellationToken ct = default)
    {
        Notes[note.Id] = note;
        return Task.CompletedTask;
    }

    public Task DeleteNoteAsync(string id, CancellationToken ct = default)
    {
        Notes.Remove(id);
        Links.RemoveAll(l => l.SourceId == id || l.TargetId == id);
        NoteTags.Remove(id);
        Embeddings.Remove(id);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Note>> ListNotesForDocumentAsync(string documentId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Note>>(Notes.Values.Where(n => n.SourceDocumentId == documentId).ToList());

    public Task<IReadOnlyList<Tag>> ListTagsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Tag>>(Tags.ToList());

    public Task<Tag> EnsureTagAsync(string name, CancellationToken ct = default)
    {
        var normalized = Tag.Normalize(name);
        var existing = Tags.FirstOrDefault(t => t.Name == normalized);
        if (existing is not null)
            return Task.FromResult(existing);

        var tag = Tag.Create(normalized);
        Tags.Add(tag);
        return Task.FromResult(tag);
    }

    public Task SetNoteTagsAsync(string noteId, IEnumerable<string> tagIds, CancellationToken ct = default)
    {
        NoteTags[noteId] = tagIds.Distinct().ToList();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Link>> ListLinksAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Link>>(Links.ToList());

    public Task AddLinkAsync(Link link, CancellationToken ct = default)
    {
        if (!Links.Any(l => l.SourceId == link.SourceId && l.TargetId == link.TargetId && l.Kind == link.Kind))
            Links.Add(link);
        return Task.CompletedTask;
    }

    public Task DeleteLinkAsync(string linkId, CancellationToken ct = default)
    {
        Links.RemoveAll(l => l.Id == linkId);
        return Task.CompletedTask;
    }

    public Task ReplaceWikiLinksAsync(string sourceNoteId, IEnumerable<string> targetNoteIds, CancellationToken ct = default)
    {
        Links.RemoveAll(l => l.SourceId == sourceNoteId && l.Kind == EdgeKind.Wiki);
        foreach (var target in targetNoteIds.Where(t => t != sourceNoteId).Distinct())
            Links.Add(Link.Create(sourceNoteId, target, EdgeKind.Wiki));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PdfDocument>> ListDocumentsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PdfDocument>>(Documents.Values.ToList());

    public Task<PdfDocument?> GetDocumentAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(Documents.GetValueOrDefault(id));

    public Task SaveDocumentAsync(PdfDocument document, CancellationToken ct = default)
    {
        Documents[document.Id] = document;
        return Task.CompletedTask;
    }

    public Task DeleteDocumentAsync(string id, CancellationToken ct = default)
    {
        Documents.Remove(id);
        ReadingStates.Remove(id);
        Highlights.RemoveAll(h => h.DocumentId == id);
        Links.RemoveAll(l => l.SourceId == id || l.TargetId == id);
        return Task.CompletedTask;
    }

    public Dictionary<string, ReadingState> ReadingStates { get; } = new(StringComparer.Ordinal);

    public Task<ReadingState?> GetReadingStateAsync(string documentId, CancellationToken ct = default) =>
        Task.FromResult(ReadingStates.TryGetValue(documentId, out var state) ? state : null);

    public Task SaveReadingStateAsync(ReadingState state, CancellationToken ct = default)
    {
        ReadingStates[state.DocumentId] = state;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Highlight>> ListHighlightsAsync(string documentId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Highlight>>(Highlights.Where(h => h.DocumentId == documentId).ToList());

    public Task SaveHighlightAsync(Highlight highlight, CancellationToken ct = default)
    {
        Highlights.Add(highlight);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Suggestion>> ListSuggestionsAsync(SuggestionStatus? status = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Suggestion>>(
            Suggestions.Where(s => status is null || s.Status == status).ToList());

    public Task SaveSuggestionAsync(Suggestion suggestion, CancellationToken ct = default)
    {
        var index = Suggestions.FindIndex(s => s.Id == suggestion.Id);
        if (index >= 0)
            Suggestions[index] = suggestion;
        else
            Suggestions.Add(suggestion);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatMessage>> ListChatMessagesAsync(int limit = 200, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ChatMessage>>(
            Chat.OrderBy(m => m.CreatedAt).Take(limit).ToList());

    public Task SaveChatMessageAsync(ChatMessage message, CancellationToken ct = default)
    {
        Chat.Add(message);
        return Task.CompletedTask;
    }

    public Task DeleteChatMessagesAsync(CancellationToken ct = default)
    {
        Chat.Clear();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEntry>> ListAuditAsync(int limit = 100, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuditEntry>>(Audit.Take(limit).ToList());

    public Task AppendAuditAsync(AuditEntry entry, CancellationToken ct = default)
    {
        Audit.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit = 50, CancellationToken ct = default)
    {
        var hits = Notes.Values
            .Where(n => n.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || n.Body.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(n => new SearchHit(n.Id, "note", n.Title, n.Body))
            .ToList();

        return Task.FromResult<IReadOnlyList<SearchHit>>(hits);
    }

    public Task<IReadOnlyList<NoteEmbedding>> ListEmbeddingsAsync(string model, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<NoteEmbedding>>(
            Embeddings.Values.Where(e => e.Model == model).ToList());

    public Task SaveEmbeddingsAsync(IEnumerable<NoteEmbedding> embeddings, CancellationToken ct = default)
    {
        foreach (var embedding in embeddings)
            Embeddings[embedding.NoteId] = embedding;

        return Task.CompletedTask;
    }

    public Dictionary<string, List<DocumentPage>> Pages { get; } = new(StringComparer.Ordinal);
    public Dictionary<(string DocumentId, int Page), PageEmbedding> PageVectors { get; } = new();

    public Task<IReadOnlyList<DocumentPage>> ListDocumentPagesAsync(string documentId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DocumentPage>>(
            Pages.TryGetValue(documentId, out var pages) ? pages.OrderBy(p => p.Page).ToList() : new List<DocumentPage>());

    public Task SaveDocumentPagesAsync(IEnumerable<DocumentPage> pages, CancellationToken ct = default)
    {
        foreach (var group in pages.GroupBy(p => p.DocumentId))
            Pages[group.Key] = group.OrderBy(p => p.Page).ToList();

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListDocumentIdsWithoutPagesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(
            Documents.Keys.Where(id => !Pages.ContainsKey(id) || Pages[id].Count == 0).ToList());

    public Task<IReadOnlyList<SearchHit>> SearchPagesAsync(string query, int limit = 20, CancellationToken ct = default)
    {
        var hits = Pages
            .Where(p => Documents.ContainsKey(p.Key))
            .SelectMany(p => p.Value)
            .Where(p => p.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .Select(p => new SearchHit(
                p.DocumentId,
                "document",
                Documents[p.DocumentId].Title,
                $"p. {p.Page} \u00b7 {p.Text}",
                p.Page))
            .ToList();

        return Task.FromResult<IReadOnlyList<SearchHit>>(hits);
    }

    public Task<IReadOnlyList<PageEmbedding>> ListPageEmbeddingsAsync(string model, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PageEmbedding>>(
            PageVectors.Values.Where(e => e.Model == model).ToList());

    public Task SavePageEmbeddingsAsync(IEnumerable<PageEmbedding> embeddings, CancellationToken ct = default)
    {
        foreach (var embedding in embeddings)
            PageVectors[(embedding.DocumentId, embedding.Page)] = embedding;

        return Task.CompletedTask;
    }
}
