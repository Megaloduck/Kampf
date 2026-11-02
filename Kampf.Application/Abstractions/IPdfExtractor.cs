using Kampf.Application.Models;

namespace Kampf.Application.Abstractions;

/// <summary>Port for PDF text + citation metadata extraction (implemented with PdfPig).</summary>
public interface IPdfExtractor
{
    Task<PdfExtraction> ExtractAsync(string filePath, CancellationToken ct = default);
}
