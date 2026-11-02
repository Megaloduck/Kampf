using Kampf.Domain.ValueObjects;

namespace Kampf.Domain.Entities;

/// <summary>A markdown note — the primary unit of captured knowledge.</summary>
public sealed class Note
{
    public string Id { get; set; } = Ids.NewId();
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Set when the note was created from a PDF highlight.</summary>
    public string? SourceDocumentId { get; set; }

    /// <summary>1-based page the note was anchored to, if any.</summary>
    public int? SourcePage { get; set; }

    /// <summary>Tags are always explicit: a user (or an accepted suggestion) put them here.</summary>
    public List<Tag> Tags { get; } = new();

    public bool HasProvenance => SourceDocumentId is not null;

    public PageAnchor? Anchor =>
        SourceDocumentId is not null && SourcePage is not null
            ? new PageAnchor(SourceDocumentId, SourcePage.Value)
            : null;

    public static Note Create(string title, string body = "")
    {
        var now = DateTimeOffset.Now;
        return new Note
        {
            Title = string.IsNullOrWhiteSpace(title) ? "Untitled note" : title.Trim(),
            Body = body,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void Touch() => UpdatedAt = DateTimeOffset.Now;
}
