using Kampf.Application.Models;

namespace Kampf.Application.Abstractions;

/// <summary>
/// Port for the visual side of a PDF: the page as pixels, and where each word
/// sits, so highlights and selections can be drawn on the page image itself.
/// </summary>
public interface IPdfPageRenderer
{
    /// <summary>Text plus word boxes for one page (1-based).</summary>
    Task<PdfPageLayout> GetLayoutAsync(string filePath, int page, CancellationToken ct = default);

    /// <summary>The page rendered to BGRA pixels, scaled so it is about <paramref name="widthPixels"/> wide.</summary>
    Task<PdfPageImage> RenderAsync(string filePath, int page, int widthPixels, CancellationToken ct = default);

    /// <summary>Drops cached layouts and file bytes, e.g. when the paper is closed or deleted.</summary>
    void Forget(string filePath);
}
