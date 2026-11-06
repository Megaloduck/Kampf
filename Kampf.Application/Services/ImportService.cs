using System.Security.Cryptography;
using Kampf.Application.Abstractions;
using Kampf.Application.Models;
using Kampf.Domain.Entities;

namespace Kampf.Application.Services;

/// <summary>
/// The paper an import landed on. <see cref="AlreadyPresent"/> means the vault
/// already had it, so nothing was copied, parsed or indexed: the caller should
/// open the existing paper rather than treat this as a fresh import.
/// </summary>
public sealed record ImportResult(
    PdfDocument Document,
    PdfExtraction? Extraction,
    bool AlreadyPresent = false);

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

        var known = await _repo.ListDocumentsAsync(ct);
        var contentHash = await HashAsync(filePath, ct);

        // The same file imported twice would duplicate the paper, its page text,
        // its vectors and its graph node — and the notes anchored to the two
        // copies would quietly drift apart. Checked before anything is copied.
        var duplicate = FindDuplicate(known, contentHash, doi: null);
        if (duplicate is not null)
            return new ImportResult(duplicate, Extraction: null, AlreadyPresent: true);

        var extraction = await _extractor.ExtractAsync(filePath, ct);
        var citation = CitationService.FromExtraction(extraction);

        var doi = citation.Doi
                  ?? CitationService.FindDoi(string.Concat(extraction.Pages.Take(2).Select(p => p.Text)));

        // A re-download or a re-saved file differs byte for byte but is the same
        // paper, so the DOI is the second chance at recognising it.
        duplicate = FindDuplicate(known, contentHash, doi);
        if (duplicate is not null)
            return new ImportResult(duplicate, extraction, AlreadyPresent: true);

        var storedPath = await _files.StoreDocumentAsync(filePath, ct);

        var document = PdfDocument.Create(storedPath, citation.Title, extraction.PageCount);
        document.ContentHash = contentHash;
        document.Authors = citation.Authors;
        document.Year = citation.Year;
        document.Doi = doi;
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

    /// <summary>SHA-256 of the file, as hex, recorded so re-imports are recognised.</summary>
    private static async Task<string> HashAsync(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var digest = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(digest);
    }

    /// <summary>
    /// The paper this file already is, by content hash or by DOI. Either match is
    /// conclusive: two copies of one paper cannot be told apart in the graph.
    /// </summary>
    private static PdfDocument? FindDuplicate(
        IReadOnlyList<PdfDocument> known,
        string contentHash,
        string? doi)
    {
        foreach (var document in known)
        {
            if (!string.IsNullOrWhiteSpace(document.ContentHash) &&
                document.ContentHash.Equals(contentHash, StringComparison.OrdinalIgnoreCase))
            {
                return document;
            }

            if (!string.IsNullOrWhiteSpace(doi) &&
                !string.IsNullOrWhiteSpace(document.Doi) &&
                document.Doi.Trim().Equals(doi.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return document;
            }
        }

        return null;
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
