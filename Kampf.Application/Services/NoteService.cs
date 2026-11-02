using Kampf.Application.Abstractions;
using Kampf.Application.Markdown;
using Kampf.Application.Models;
using Kampf.Domain.Entities;
using Kampf.Domain.ValueObjects;

namespace Kampf.Application.Services;

/// <summary>
/// Note use cases: creating, saving, deleting, tagging, and turning a PDF
/// highlight into a page-anchored note. Also keeps wiki links in sync.
/// </summary>
public sealed class NoteService
{
    private readonly IVaultRepository _repo;

    public NoteService(IVaultRepository repo) => _repo = repo;

    public async Task<Note> CreateAsync(string title, string body = "", CancellationToken ct = default)
    {
        var note = Note.Create(title, body);
        await _repo.SaveNoteAsync(note, ct);
        await SyncWikiLinksAsync(note, ct);
        await _repo.AppendAuditAsync(AuditEntry.Create("note.created", note.Title), ct);
        return note;
    }

    public async Task<Note> SaveAsync(Note note, CancellationToken ct = default)
    {
        note.Touch();
        await _repo.SaveNoteAsync(note, ct);
        await SyncWikiLinksAsync(note, ct);
        return note;
    }

    public async Task DeleteAsync(string noteId, CancellationToken ct = default)
    {
        await _repo.DeleteNoteAsync(noteId, ct);
        await _repo.AppendAuditAsync(AuditEntry.Create("note.deleted", noteId), ct);
    }

    /// <summary>
    /// Rewrites the note's wiki edges to match its [[links]] and queues a
    /// review-queue suggestion for every link that has no note yet. Links are
    /// never created to notes the reader has not named.
    /// </summary>
    public async Task SyncWikiLinksAsync(Note note, CancellationToken ct = default)
    {
        var targets = WikiLinkParser.ParseTargets(note.Body);
        if (targets.Count == 0)
        {
            await _repo.ReplaceWikiLinksAsync(note.Id, Array.Empty<string>(), ct);
            return;
        }

        var all = await _repo.ListNotesAsync(ct);
        var byTitle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var other in all)
        {
            if (other.Id == note.Id)
                continue;
            byTitle.TryAdd(other.Title.Trim(), other.Id);
        }

        var resolved = new List<string>();
        foreach (var target in targets)
        {
            if (byTitle.TryGetValue(target.Trim(), out var id))
                resolved.Add(id);
            else
                await ProposeMissingNoteAsync(note, target, ct);
        }

        await _repo.ReplaceWikiLinksAsync(note.Id, resolved.Distinct(StringComparer.Ordinal), ct);
    }

    private async Task ProposeMissingNoteAsync(Note source, string targetTitle, CancellationToken ct)
    {
        var payload = new CreateMissingNotePayload(targetTitle, source.Id);
        var json = SuggestionJson.Serialize(payload);
        var pending = await _repo.ListSuggestionsAsync(SuggestionStatus.Pending, ct);

        var duplicate = pending.Any(s =>
            s.Kind == SuggestionKind.CreateMissingNote &&
            string.Equals(s.PayloadJson, json, StringComparison.Ordinal));
        if (duplicate)
            return;

        await _repo.SaveSuggestionAsync(
            Suggestion.Create(
                SuggestionKind.CreateMissingNote,
                $"Create note \u201c{targetTitle}\u201d linked from \u201c{source.Title}\u201d",
                json),
            ct);
    }

    /// <summary>Sets the note's tags, creating any tag that does not exist yet.</summary>
    public async Task SetTagsAsync(Note note, IEnumerable<string> tagNames, CancellationToken ct = default)
    {
        var ids = new List<string>();
        foreach (var name in tagNames.Select(Tag.Normalize).Where(n => n.Length > 0).Distinct())
        {
            var tag = await _repo.EnsureTagAsync(name, ct);
            ids.Add(tag.Id);
        }

        await _repo.SetNoteTagsAsync(note.Id, ids, ct);
    }

    /// <summary>
    /// Highlight-to-note: keeps the quoted passage, the document and the page
    /// so the resulting note is traceable to its source.
    /// </summary>
    public async Task<Note> CreateFromHighlightAsync(
        PdfDocument document,
        int page,
        string quotedText,
        CancellationToken ct = default)
    {
        var note = Note.Create(BuildTitleFromQuote(quotedText));
        note.SourceDocumentId = document.Id;
        note.SourcePage = page;
        note.Body = $"> {quotedText.Replace("\r\n", "\n").Replace("\n", "\n> ")}\n\n" +
                    $"\u2014 {document.Title}, p. {page}\n\n";

        await _repo.SaveNoteAsync(note, ct);

        var highlight = Highlight.Create(document.Id, page, quotedText);
        highlight.NoteId = note.Id;
        await _repo.SaveHighlightAsync(highlight, ct);

        await _repo.AppendAuditAsync(
            AuditEntry.Create("highlight.captured", $"{document.FileName} p.{page} \u2192 {note.Title}"),
            ct);

        return note;
    }

    private static string BuildTitleFromQuote(string quote)
    {
        var flat = string.Join(' ', quote.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length <= 70)
            return flat.Length == 0 ? "Highlight" : flat;

        var cut = flat.LastIndexOf(' ', Math.Min(70, flat.Length - 1));
        return (cut > 30 ? flat[..cut] : flat[..70]).TrimEnd('.', ',', ';', ':') + "\u2026";
    }
}
