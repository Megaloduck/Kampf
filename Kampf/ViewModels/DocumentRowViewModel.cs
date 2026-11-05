using Kampf.Domain.Entities;

namespace Kampf.ViewModels;

/// <summary>Display projection for one imported PDF.</summary>
public sealed class DocumentRowViewModel
{
    public DocumentRowViewModel(PdfDocument document, int anchoredNotes, int? matchPage = null)
    {
        Document = document;
        AnchoredNotes = anchoredNotes;
        MatchPage = matchPage;
    }

    public PdfDocument Document { get; }

    public int AnchoredNotes { get; }

    /// <summary>
    /// Set when a search matched inside the paper rather than its metadata, so
    /// opening the row lands on the page that matched.
    /// </summary>
    public int? MatchPage { get; }

    public string Id => Document.Id;

    public string Title => Document.Title;

    public string CitationLine => CitationServiceLine();

    public string MetaLine => (MatchPage is { } page ? $"p. {page} match \u00b7 " : string.Empty) +
                              $"{Document.PageCount} pages \u00b7 {AnchoredNotes} note(s)";

    private string CitationServiceLine()
    {
        var year = Document.Year?.ToString() ?? "n.d.";
        var authors = string.IsNullOrWhiteSpace(Document.Authors) ? "Unknown author" : Document.Authors;
        return $"{authors} \u00b7 {year}";
    }
}
