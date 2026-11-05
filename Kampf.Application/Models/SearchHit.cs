namespace Kampf.Application.Models;

/// <summary>
/// A full-text search hit. <see cref="Kind"/> is <c>note</c> or <c>document</c>;
/// a document hit with a <see cref="Page"/> matched inside the paper's text, so
/// the reader can open it at the page that matched.
/// </summary>
public sealed record SearchHit(string Id, string Kind, string Title, string Snippet, int? Page = null)
{
    public bool IsNote => Kind == "note";
}
