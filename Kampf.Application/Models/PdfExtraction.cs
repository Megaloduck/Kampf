namespace Kampf.Application.Models;

/// <summary>One extracted page of text, 1-based page number.</summary>
public sealed record PdfPageText(int Page, string Text);

/// <summary>Everything the importer needs from a PDF, without any PDF library types.</summary>
public sealed record PdfExtraction(
    string? Title,
    string? Authors,
    int? Year,
    string? Doi,
    int PageCount,
    IReadOnlyList<PdfPageText> Pages,
    string? Abstract = null,
    string? Publication = null,
    string? Volume = null,
    string? Issue = null,
    string? PageRange = null,
    string? Issn = null,
    string? Url = null,
    string? Keywords = null,
    string? ItemType = null,
    string? Language = null)
{
    public string FullText => string.Concat(Pages.Select(p => p.Text + "\n"));
}
