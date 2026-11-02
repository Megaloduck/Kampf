using Kampf.Application.Abstractions;
using Kampf.Application.Models;
using Kampf.Domain.Entities;
using Kampf.Domain.ValueObjects;

namespace Kampf.Application.Services;

public sealed record SuggestionDecision(Suggestion Suggestion, string? CreatedNoteId);

/// <summary>
/// The approval gate. Nothing here mutates the graph until the user accepts a
/// suggestion, and every decision is written to the audit trail.
/// </summary>
public sealed class SuggestionService
{
    private readonly IVaultRepository _repo;
    private readonly NoteService _notes;

    public SuggestionService(IVaultRepository repo, NoteService notes)
    {
        _repo = repo;
        _notes = notes;
    }

    public Task<IReadOnlyList<Suggestion>> ListPendingAsync(CancellationToken ct = default) =>
        _repo.ListSuggestionsAsync(SuggestionStatus.Pending, ct);

    public async Task<SuggestionDecision> DecideAsync(string suggestionId, bool accept, CancellationToken ct = default)
    {
        var pending = await _repo.ListSuggestionsAsync(SuggestionStatus.Pending, ct);
        var suggestion = pending.FirstOrDefault(s => s.Id == suggestionId)
            ?? throw new InvalidOperationException("Suggestion is no longer pending.");

        string? createdNoteId = null;
        var detail = suggestion.Title;

        if (accept)
            createdNoteId = await ApplyAsync(suggestion, ct);

        if (accept)
            suggestion.Accept();
        else
            suggestion.Reject();

        await _repo.SaveSuggestionAsync(suggestion, ct);
        await _repo.AppendAuditAsync(
            AuditEntry.Create(accept ? "suggestion.accepted" : "suggestion.rejected", detail),
            ct);

        return new SuggestionDecision(suggestion, createdNoteId);
    }

    private async Task<string?> ApplyAsync(Suggestion suggestion, CancellationToken ct)
    {
        switch (suggestion.Kind)
        {
            case SuggestionKind.CreateMissingNote:
            {
                var payload = SuggestionJson.Deserialize<CreateMissingNotePayload>(suggestion.PayloadJson);
                if (payload is null)
                    return null;

                var created = await _notes.CreateAsync(payload.TargetTitle, string.Empty, ct);

                // Re-sync the source note so the [[link]] becomes a real wiki edge.
                var source = await _repo.GetNoteAsync(payload.SourceNoteId, ct);
                if (source is not null)
                    await _notes.SyncWikiLinksAsync(source, ct);

                return created.Id;
            }

            case SuggestionKind.LinkNotes:
            {
                var payload = SuggestionJson.Deserialize<LinkNotesPayload>(suggestion.PayloadJson);
                if (payload is null)
                    return null;

                await _repo.AddLinkAsync(
                    Link.Create(payload.SourceNoteId, payload.TargetNoteId, EdgeKind.AcceptedSuggestion, payload.Reason),
                    ct);
                return null;
            }

            case SuggestionKind.ApplyTag:
            {
                var payload = SuggestionJson.Deserialize<ApplyTagPayload>(suggestion.PayloadJson);
                if (payload is null)
                    return null;

                var note = await _repo.GetNoteAsync(payload.NoteId, ct);
                if (note is null)
                    return null;

                var existing = note.Tags.Select(t => t.Name).ToList();
                existing.Add(payload.TagName);
                await _notes.SetTagsAsync(note, existing, ct);
                return null;
            }

            default:
                return null;
        }
    }

    /// <summary>Used by import/review flows (and Phase 2 AI) to enqueue a proposal.</summary>
    public async Task<Suggestion> ProposeLinkAsync(
        string sourceNoteId,
        string targetNoteId,
        string reason,
        CancellationToken ct = default)
    {
        var pending = await _repo.ListSuggestionsAsync(SuggestionStatus.Pending, ct);
        var payload = new LinkNotesPayload(sourceNoteId, targetNoteId, reason);
        var json = SuggestionJson.Serialize(payload);

        var duplicate = pending.Any(s =>
            s.Kind == SuggestionKind.LinkNotes && string.Equals(s.PayloadJson, json, StringComparison.Ordinal));
        if (duplicate)
            throw new InvalidOperationException("That link suggestion is already queued.");

        var source = await _repo.GetNoteAsync(sourceNoteId, ct);
        var target = await _repo.GetNoteAsync(targetNoteId, ct);
        var title = $"Link \u201c{source?.Title ?? sourceNoteId}\u201d \u2192 \u201c{target?.Title ?? targetNoteId}\u201d";

        var suggestion = Suggestion.Create(SuggestionKind.LinkNotes, title, json);
        await _repo.SaveSuggestionAsync(suggestion, ct);
        return suggestion;
    }
}
