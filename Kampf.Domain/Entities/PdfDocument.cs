using Kampf.Domain.ValueObjects;

namespace Kampf.Domain.Entities;

/// <summary>
/// An imported PDF plus the citation metadata captured on import.
/// The file itself stays on disk; this record keeps provenance.
/// </summary>
public sealed class PdfDocument
{
    public string Id { get; set; } = Ids.NewId();
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Authors { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string? Doi { get; set; }
    public int PageCount { get; set; }

    // Citation detail captured on import; null means the PDF did not carry it.
    public string? ItemType { get; set; }
    public string? Abstract { get; set; }
    public string? Publication { get; set; }
    public string? Volume { get; set; }
    public string? Issue { get; set; }
    public string? PageRange { get; set; }
    public string? Issn { get; set; }
    public string? Url { get; set; }
    public string? Keywords { get; set; }
    public string? Language { get; set; }
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.Now;

    public static PdfDocument Create(string filePath, string title, int pageCount)
    {
        var name = Path.GetFileName(filePath);
        return new PdfDocument
        {
            FilePath = filePath,
            FileName = name,
            Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(name) : title.Trim(),
            PageCount = pageCount,
        };
    }
}

/// <summary>How a source is cited back to the reader.</summary>
public sealed record Citation(string Title, string Authors, int? Year, string? Doi)
{
    /// <summary>Short human readable form used in note headers and the reader pane.</summary>
    public string Format()
    {
        var authors = string.IsNullOrWhiteSpace(Authors) ? "Unknown author" : Authors;
        var year = Year is null ? "n.d." : Year.Value.ToString();
        var doi = string.IsNullOrWhiteSpace(Doi) ? null : $"https://doi.org/{Doi}";
        return doi is null
            ? $"{authors} ({year}). {Title}."
            : $"{authors} ({year}). {Title}. {doi}";
    }
}
