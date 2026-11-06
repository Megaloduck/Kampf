using Kampf.Domain.Entities;
using Kampf.Domain.ValueObjects;
using Kampf.Application.Models;

namespace Kampf.Application.Abstractions;

/// <summary>
/// Persistence boundary for the vault. The application layer never talks to
/// SQLite directly; infrastructure implements this port.
/// </summary>
public interface IVaultRepository
{
    Task InitializeAsync(CancellationToken ct = default);

    // Notes
    Task<IReadOnlyList<Note>> ListNotesAsync(CancellationToken ct = default);
    Task<Note?> GetNoteAsync(string id, CancellationToken ct = default);
    Task SaveNoteAsync(Note note, CancellationToken ct = default);
    Task DeleteNoteAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<Note>> ListNotesForDocumentAsync(string documentId, CancellationToken ct = default);

    // Tags
    Task<IReadOnlyList<Tag>> ListTagsAsync(CancellationToken ct = default);
    Task<Tag> EnsureTagAsync(string name, CancellationToken ct = default);
    Task SetNoteTagsAsync(string noteId, IEnumerable<string> tagIds, CancellationToken ct = default);

    // Links / graph edges
    Task<IReadOnlyList<Link>> ListLinksAsync(CancellationToken ct = default);
    Task AddLinkAsync(Link link, CancellationToken ct = default);
    Task DeleteLinkAsync(string linkId, CancellationToken ct = default);

    /// <summary>Replaces the wiki-derived outgoing edges of a note (idempotent).</summary>
    Task ReplaceWikiLinksAsync(string sourceNoteId, IEnumerable<string> targetNoteIds, CancellationToken ct = default);

    // Documents
    Task<IReadOnlyList<PdfDocument>> ListDocumentsAsync(CancellationToken ct = default);
    Task<PdfDocument?> GetDocumentAsync(string id, CancellationToken ct = default);
    Task SaveDocumentAsync(PdfDocument document, CancellationToken ct = default);

    /// <summary>Removes a document together with its highlights and graph edges.</summary>
    Task DeleteDocumentAsync(string id, CancellationToken ct = default);

    // Extracted page text, so a paper's body can be searched and cited
    Task<IReadOnlyList<DocumentPage>> ListDocumentPagesAsync(string documentId, CancellationToken ct = default);
    Task SaveDocumentPagesAsync(IEnumerable<DocumentPage> pages, CancellationToken ct = default);

    /// <summary>Papers imported before page text was stored, so they can be backfilled.</summary>
    Task<IReadOnlyList<string>> ListDocumentIdsWithoutPagesAsync(CancellationToken ct = default);

    /// <summary>Full-text search over stored page text (one hit per page).</summary>
    Task<IReadOnlyList<SearchHit>> SearchPagesAsync(string query, int limit = 20, CancellationToken ct = default);

    // Reading state

    Task<ReadingState?> GetReadingStateAsync(string documentId, CancellationToken ct = default);

    Task SaveReadingStateAsync(ReadingState state, CancellationToken ct = default);

    // Highlights
    Task<IReadOnlyList<Highlight>> ListHighlightsAsync(string documentId, CancellationToken ct = default);
    Task SaveHighlightAsync(Highlight highlight, CancellationToken ct = default);

    // Suggestions / review queue
    Task<IReadOnlyList<Suggestion>> ListSuggestionsAsync(SuggestionStatus? status = null, CancellationToken ct = default);
    Task SaveSuggestionAsync(Suggestion suggestion, CancellationToken ct = default);

    // Idea-Craft chat history
    Task<IReadOnlyList<ChatMessage>> ListChatMessagesAsync(int limit = 200, CancellationToken ct = default);
    Task SaveChatMessageAsync(ChatMessage message, CancellationToken ct = default);

    /// <summary>Forgets the conversation; notes, papers and links are untouched.</summary>
    Task DeleteChatMessagesAsync(CancellationToken ct = default);

    // Audit trail
    Task<IReadOnlyList<AuditEntry>> ListAuditAsync(int limit = 100, CancellationToken ct = default);
    Task AppendAuditAsync(AuditEntry entry, CancellationToken ct = default);

    // Full-text search
    Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit = 50, CancellationToken ct = default);

    // Cached note vectors for the suggestion engine (one row per note and model)
    Task<IReadOnlyList<NoteEmbedding>> ListEmbeddingsAsync(string model, CancellationToken ct = default);
    Task SaveEmbeddingsAsync(IEnumerable<NoteEmbedding> embeddings, CancellationToken ct = default);

    // Cached page vectors, one row per page; the model column tags the vector space
    Task<IReadOnlyList<PageEmbedding>> ListPageEmbeddingsAsync(string model, CancellationToken ct = default);
    Task SavePageEmbeddingsAsync(IEnumerable<PageEmbedding> embeddings, CancellationToken ct = default);
}
