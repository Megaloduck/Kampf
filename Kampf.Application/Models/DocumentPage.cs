namespace Kampf.Application.Models;

/// <summary>
/// One extracted PDF page. Page text is stored when a paper is imported so the
/// vault can search and cite what a paper actually says, not only the notes
/// written about it.
/// </summary>
public sealed record DocumentPage(string DocumentId, int Page, string Text)
{
    /// <summary>What the page is indexed and embedded by: a flattened page.</summary>
    public string IndexText => Flatten(Text);

    internal static string Flatten(string text) => string.Join(
        ' ',
        text.Replace("\r", " ").Replace("\n", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>Cached vector for one stored page, tagged with the model that made it.</summary>
public sealed record PageEmbedding(
    string DocumentId,
    int Page,
    string Model,
    string ContentHash,
    float[] Vector);
