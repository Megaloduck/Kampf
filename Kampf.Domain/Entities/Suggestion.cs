using Kampf.Domain.ValueObjects;

namespace Kampf.Domain.Entities;

/// <summary>
/// A proposed change to the vault. Suggestions never mutate the graph on their
/// own: only an explicit accept (or edit-then-accept) does, which keeps the
/// "no auto-committed AI edits" rule in the domain layer.
/// </summary>
public sealed class Suggestion
{
    public string Id { get; set; } = Ids.NewId();
    public SuggestionKind Kind { get; set; }
    public string Title { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public SuggestionStatus Status { get; set; } = SuggestionStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? DecidedAt { get; set; }

    public bool IsPending => Status == SuggestionStatus.Pending;

    public static Suggestion Create(SuggestionKind kind, string title, string payloadJson) => new()
    {
        Kind = kind,
        Title = title,
        PayloadJson = payloadJson,
    };

    public void Accept()
    {
        EnsurePending();
        Status = SuggestionStatus.Accepted;
        DecidedAt = DateTimeOffset.Now;
    }

    public void Reject()
    {
        EnsurePending();
        Status = SuggestionStatus.Rejected;
        DecidedAt = DateTimeOffset.Now;
    }

    private void EnsurePending()
    {
        if (!IsPending)
            throw new InvalidOperationException("This suggestion has already been decided.");
    }
}
