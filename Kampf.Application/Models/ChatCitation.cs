namespace Kampf.Application.Models;

/// <summary>
/// Provenance for one chat answer. Normally the note it came from, the paper
/// that note was captured from (when it has one) and the page anchor. A
/// citation of a paper's own page has no note: <see cref="DocumentId"/> is set
/// and <see cref="NoteId"/> is empty, so the UI can open the paper instead.
///
/// <see cref="Number"/> is the marker the answer uses for this source (<c>[2]</c>).
/// It is kept when unused citations are filtered out, so a marker in the prose
/// still points at the source it was written for.
/// </summary>
public sealed record ChatCitation(
    string NoteId,
    string NoteTitle,
    string? DocumentTitle,
    int? Page,
    string Excerpt,
    string? DocumentId = null,
    int Number = 0)
{
    /// <summary>True when this cites a page of a paper rather than a note.</summary>
    public bool IsPage => !string.IsNullOrEmpty(DocumentId);

    /// <summary>The label shown next to an answer, e.g. <c>“Graph notes” · p. 4</c>.</summary>
    public string Label => Page is null
        ? $"\u201c{NoteTitle}\u201d"
        : $"\u201c{NoteTitle}\u201d \u00b7 p. {Page}";

    public string? SourceLabel => IsPage
        ? "paper text"
        : DocumentTitle is null
            ? null
            : $"from \u201c{DocumentTitle}\u201d";
}

/// <summary>
/// A relationship the vault already records between two cited sources: a written
/// link, a shared tag, or the same paper. These are evidence for "what connects
/// X and Y?" that no amount of retrieval can produce.
/// </summary>
public sealed record ChatConnection(string Label, string Kind, string? Rationale = null)
{
    /// <summary>One line for the prompt and for the passage-only answer.</summary>
    public override string ToString() =>
        Rationale is null ? $"{Label} ({Kind})" : $"{Label} ({Kind}): {Rationale}";
}
