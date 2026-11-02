using Kampf.Domain.ValueObjects;

namespace Kampf.Domain.Entities;

/// <summary>
/// A quoted passage from a PDF page. A highlight may back one note; the note
/// keeps the page anchor so the claim can always be traced back.
/// </summary>
public sealed class Highlight
{
    public string Id { get; set; } = Ids.NewId();
    public string DocumentId { get; set; } = string.Empty;
    public int Page { get; set; }
    public string Text { get; set; } = string.Empty;
    public string? NoteId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public static Highlight Create(string documentId, int page, string text)
    {
        if (page < 1)
            throw new ArgumentOutOfRangeException(nameof(page), "Pages are 1-based.");
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("A highlight needs text.", nameof(text));

        return new Highlight
        {
            DocumentId = documentId,
            Page = page,
            Text = text.Trim(),
        };
    }
}
