namespace Kampf.Application.Models;

/// <summary>
/// Provenance for one chat answer: the note it came from, the paper that note
/// was captured from (when it has one) and the page anchor.
/// </summary>
public sealed record ChatCitation(
    string NoteId,
    string NoteTitle,
    string? DocumentTitle,
    int? Page,
    string Excerpt)
{
    /// <summary>The label shown next to an answer, e.g. <c>“Graph notes” · p. 4</c>.</summary>
    public string Label => Page is null
        ? $"\u201c{NoteTitle}\u201d"
        : $"\u201c{NoteTitle}\u201d \u00b7 p. {Page}";

    public string? SourceLabel => DocumentTitle is null
        ? null
        : $"from \u201c{DocumentTitle}\u201d";
}
