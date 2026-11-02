using Kampf.Domain.Entities;

namespace Kampf.ViewModels;

/// <summary>Display projection for one imported PDF.</summary>
public sealed class DocumentRowViewModel
{
    public DocumentRowViewModel(PdfDocument document, int anchoredNotes)
    {
        Document = document;
        AnchoredNotes = anchoredNotes;
    }

    public PdfDocument Document { get; }

    public int AnchoredNotes { get; }

    public string Id => Document.Id;

    public string Title => Document.Title;

    public string CitationLine => CitationServiceLine();

    public string MetaLine => $"{Document.PageCount} pages \u00b7 {AnchoredNotes} note(s)";

    private string CitationServiceLine()
    {
        var year = Document.Year?.ToString() ?? "n.d.";
        var authors = string.IsNullOrWhiteSpace(Document.Authors) ? "Unknown author" : Document.Authors;
        return $"{authors} \u00b7 {year}";
    }
}
