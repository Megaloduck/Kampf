using Kampf.Application.Abstractions;
using Kampf.Application.Models;
using Kampf.Domain.Entities;
using Kampf.Domain.ValueObjects;

namespace Kampf.Application.Services;

public sealed record SuggestionDecision(Suggestion Suggestion, string? CreatedNoteId)
{
    /// <summary>
    /// True when the edge the suggestion proposed was already in the graph: the
    /// decision is recorded, but accepting it changed nothing.
    /// </summary>
    public bool AlreadyApplied { get; init; }

    /// <summary>
    /// Why the suggestion could not be applied. When set, the suggestion has been
    /// closed as stale (Rejected) and nothing was written to the graph.
    /// </summary>
    public string? StaleReason { get; init; }

    public bool IsStale => StaleReason is not null;
}

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

        var detail = suggestion.Title;

        if (!accept)
        {
            suggestion.Reject();
            await _repo.SaveSuggestionAsync(suggestion, ct);
            await _repo.AppendAuditAsync(
                AuditEntry.Create("suggestion.rejected", detail),
                ct);

            return new SuggestionDecision(suggestion, null);
        }

        var outcome = await ApplyAsync(suggestion, ct);

        if (outcome.StaleReason is not null)
        {
            // The vault moved after the proposal was made (a note it points at was
            // deleted). Applying it would write a dangling edge, so close it honestly
            // instead of recording an accept that changed nothing.
            suggestion.Reject();
            await _repo.SaveSuggestionAsync(suggestion, ct);
            await _repo.AppendAuditAsync(
                AuditEntry.Create("suggestion.stale", $"{detail} \u2014 {outcome.StaleReason}"),
                ct);

            return new SuggestionDecision(suggestion, null) { StaleReason = outcome.StaleReason };
        }

        suggestion.Accept();
        await _repo.SaveSuggestionAsync(suggestion, ct);
        await _repo.AppendAuditAsync(
            AuditEntry.Create(
                "suggestion.accepted",
                outcome.AlreadyApplied ? $"{detail} \u2014 edge already present, graph unchanged" : detail),
            ct);

        return new SuggestionDecision(suggestion, outcome.CreatedNoteId)
        {
            AlreadyApplied = outcome.AlreadyApplied,
        };
    }

    /// <summary>
    /// What one accept did: the note it created (if any), whether the graph was
    /// already in the proposed state, or why it could not be applied at all.
    /// </summary>
    private readonly record struct ApplyOutcome(string? CreatedNoteId, bool AlreadyApplied, string? StaleReason)
    {
        public static ApplyOutcome Applied(string? createdNoteId = null, bool alreadyApplied = false) =>
            new(createdNoteId, alreadyApplied, null);

        public static ApplyOutcome Stale(string reason) => new(null, false, reason);
    }

    private async Task<ApplyOutcome> ApplyAsync(Suggestion suggestion, CancellationToken ct)
    {
        switch (suggestion.Kind)
        {
            case SuggestionKind.CreateMissingNote:
            {
                var payload = SuggestionJson.Deserialize<CreateMissingNotePayload>(suggestion.PayloadJson);
                if (payload is null)
                    return ApplyOutcome.Stale("its details could not be read");

                // The note existed when the proposal was made; if it is gone the
                // proposal has nothing left to resolve, and creating the target
                // anyway would only add an orphan.
                var source = await _repo.GetNoteAsync(payload.SourceNoteId, ct);
                if (source is null)
                    return ApplyOutcome.Stale($"note {payload.SourceNoteId} no longer exists");

                var created = await _notes.CreateAsync(payload.TargetTitle, string.Empty, ct);

                // Re-sync the source note so the [[link]] becomes a real wiki edge.
                await _notes.SyncWikiLinksAsync(source, ct);

                return ApplyOutcome.Applied(created.Id);
            }

            case SuggestionKind.LinkNotes:
            case SuggestionKind.ConnectDots:
            {
                var payload = SuggestionJson.Deserialize<LinkNotesPayload>(suggestion.PayloadJson);
                if (payload is null)
                    return ApplyOutcome.Stale("its details could not be read");

                // Both ends must still exist. The links table has no foreign key, so
                // an unchecked accept is exactly how a dangling edge gets written.
                var source = await _repo.GetNoteAsync(payload.SourceNoteId, ct);
                if (source is null)
                    return ApplyOutcome.Stale($"note {payload.SourceNoteId} no longer exists");

                var target = await _repo.GetNoteAsync(payload.TargetNoteId, ct);
                if (target is null)
                    return ApplyOutcome.Stale($"note {payload.TargetNoteId} no longer exists");

                var inserted = await _repo.AddLinkAsync(
                    Link.Create(payload.SourceNoteId, payload.TargetNoteId, EdgeKind.AcceptedSuggestion, payload.Reason),
                    ct);

                // An identical edge is absorbed by UNIQUE(source_id, target_id, kind);
                // report that rather than claiming the graph changed.
                return ApplyOutcome.Applied(alreadyApplied: !inserted);
            }

            case SuggestionKind.ApplyTag:
            {
                var payload = SuggestionJson.Deserialize<ApplyTagPayload>(suggestion.PayloadJson);
                if (payload is null)
                    return ApplyOutcome.Stale("its details could not be read");

                var note = await _repo.GetNoteAsync(payload.NoteId, ct);
                if (note is null)
                    return ApplyOutcome.Stale($"note {payload.NoteId} no longer exists");

                var existing = note.Tags.Select(t => t.Name).ToList();
                if (existing.Any(t => string.Equals(t, payload.TagName, StringComparison.OrdinalIgnoreCase)))
                    return ApplyOutcome.Applied(alreadyApplied: true);

                existing.Add(payload.TagName);
                await _notes.SetTagsAsync(note, existing, ct);

                return ApplyOutcome.Applied();
            }

            default:
                return ApplyOutcome.Stale("its kind is not supported");
        }
    }

    /// <summary>Used by import/review flows (and Phase 2 AI) to enqueue a proposal.</summary>
    public async Task<Suggestion> ProposeLinkAsync(
        string sourceNoteId,
        string targetNoteId,
        string reason,
        CancellationToken ct = default)
    {
        // Duplicates are judged on the PAIR, never on the serialized payload: the
        // same two notes with different reason text, or proposed in the other
        // direction, are the same proposal.
        var pair = SuggestionKeys.Pair(sourceNoteId, targetNoteId);

        var pending = await _repo.ListSuggestionsAsync(SuggestionStatus.Pending, ct);
        var duplicate = pending.Any(s =>
            (s.Kind is SuggestionKind.LinkNotes or SuggestionKind.ConnectDots) &&
            SuggestionJson.Deserialize<LinkNotesPayload>(s.PayloadJson) is { } existing &&
            SuggestionKeys.Pair(existing.SourceNoteId, existing.TargetNoteId).Equals(pair));

        if (duplicate)
            throw new InvalidOperationException("That link suggestion is already queued.");

        var source = await _repo.GetNoteAsync(sourceNoteId, ct);
        var target = await _repo.GetNoteAsync(targetNoteId, ct);
        var title = $"Link \u201c{source?.Title ?? sourceNoteId}\u201d \u2192 \u201c{target?.Title ?? targetNoteId}\u201d";

        var suggestion = Suggestion.Create(
            SuggestionKind.LinkNotes,
            title,
            SuggestionJson.Serialize(new LinkNotesPayload(sourceNoteId, targetNoteId, reason)));

        await _repo.SaveSuggestionAsync(suggestion, ct);
        return suggestion;
    }
}
