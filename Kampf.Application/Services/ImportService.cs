using Kampf.Application.Abstractions;
using Kampf.Application.Models;
using Kampf.Domain.Entities;

namespace Kampf.Application.Services;

public sealed record ImportResult(PdfDocument Document, PdfExtraction Extraction);

/// <summary>
/// Import a PDF: copy it into the vault, extract text + citation metadata, and
/// persist the document node. Enrichment suggestions are Phase 2; capture is
/// facts only.
/// </summary>
public sealed class ImportService
{
    private readonly IVaultRepository _repo;
    private readonly IPdfExtractor _extractor;
    private readonly IVaultFileStore _files;
    private readonly DocumentPageIndex? _pages;

    public ImportService(
        IVaultRepository repo,
        IPdfExtractor extractor,
        IVaultFileStore files,
        DocumentPageIndex? pages = null)
    {
        _repo = repo;
        _extractor = extractor;
        _files = files;
        _pages = pages;
    }

    public async Task<ImportResult> ImportAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("PDF not found.", filePath);

        var storedPath = await _files.StoreDocumentAsync(filePath, ct);
        var extraction = await _extractor.ExtractAsync(storedPath, ct);
        var citation = CitationService.FromExtraction(extraction);

        var document = PdfDocument.Create(storedPath, citation.Title, extraction.PageCount);
        document.Authors = citation.Authors;
        document.Year = citation.Year;
        document.Doi = citation.Doi
                       ?? CitationService.FindDoi(string.Concat(extraction.Pages.Take(2).Select(p => p.Text)));
        document.ItemType = extraction.ItemType;
        document.Abstract = extraction.Abstract;
        document.Publication = extraction.Publication;
        document.Volume = extraction.Volume;
        document.Issue = extraction.Issue;
        document.PageRange = extraction.PageRange;
        document.Issn = extraction.Issn;
        document.Url = extraction.Url;
        document.Keywords = extraction.Keywords;
        document.Language = extraction.Language;

        await _repo.SaveDocumentAsync(document, ct);

        if (_pages is not null)
        {
            // Store the page text once, here, so the paper's body is searchable
            // without re-parsing the PDF on every query. A failing embedder must
            // not lose an import that already succeeded.
            try
            {
                await _pages.EnsureTextAsync(document, extraction.Pages, ct);
                await _pages.IndexAsync(new[] { document }, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Text search still works; vectors are rebuilt on the next open.
            }
        }

        await _repo.AppendAuditAsync(
            AuditEntry.Create("document.imported", $"{document.Title} ({document.PageCount} pages)"),
            ct);

        return new ImportResult(document, extraction);
    }

    /// <summary>
    /// Removes an imported paper from the vault: its database row, highlights,
    /// graph edges and the stored PDF. Refuses while notes still carry a page
    /// anchor into it, so provenance never points at a file that is gone.
    /// </summary>
    public async Task DeleteDocumentAsync(PdfDocument document, CancellationToken ct = default)
    {
        var anchored = await _repo.ListNotesForDocumentAsync(document.Id, ct);
        if (anchored.Count > 0)
        {
            throw new InvalidOperationException(
                $"{anchored.Count} note(s) were captured from this paper. Delete those notes first, " +
                "so their page anchors still point at a real source.");
        }

        await _repo.DeleteDocumentAsync(document.Id, ct);
        await _files.DeleteAsync(document.FilePath, ct);
        await _repo.AppendAuditAsync(
            AuditEntry.Create("document.deleted", $"{document.Title} ({document.FileName})"),
            ct);
    }
}
