using Kampf.Application.Abstractions;
using Kampf.Application.Embeddings;
using Kampf.Application.Models;
using Kampf.Application.Services;
using Kampf.Domain.Entities;
using Xunit;

namespace Kampf.Application.Tests;

/// <summary>
/// Papers imported before the reading-order fix keep their collapsed text in the
/// vault. Opening them replaces exactly that text, and only that text.
/// </summary>
public class DocumentPageRepairTests
{
    private sealed class StubExtractor : IPdfExtractor
    {
        public Task<PdfExtraction> ExtractAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult(new PdfExtraction(
                "Paper", "A. Author", 2024, null, 0, Array.Empty<PdfPageText>()));
    }

    private static PdfDocument Paper() =>
        PdfDocument.Create(Path.Combine(Path.GetTempPath(), "kampf-missing.pdf"), "Community detection", 2);

    private static DocumentPageIndex Index(FakeVaultRepository repo) =>
        new(repo, new StubExtractor(), new HashingEmbeddingProvider());

    [Fact]
    public async Task Collapsed_single_line_text_is_replaced_and_every_page_is_kept()
    {
        var repo = new FakeVaultRepository();
        var document = Paper();
        repo.Documents[document.Id] = document;

        await repo.SaveDocumentPagesAsync(new[]
        {
            new DocumentPage(document.Id, 1, "Left column line one.Right column line one."),
            new DocumentPage(document.Id, 2, "A second page of the same paper."),
        });

        var pages = new[]
        {
            new PdfPageText(1, "Left column line one.\nRight column line one."),
            new PdfPageText(2, "A second page of the same paper."),
        };

        Assert.Equal(2, await Index(repo).RepairTextAsync(document, pages));

        var stored = await repo.ListDocumentPagesAsync(document.Id);
        Assert.Equal(2, stored.Count);
        Assert.Equal("Left column line one.\nRight column line one.", stored[0].Text);
        Assert.Equal("A second page of the same paper.", stored[1].Text);
    }

    [Fact]
    public async Task Text_that_already_reads_properly_is_left_alone()
    {
        var repo = new FakeVaultRepository();
        var document = Paper();
        repo.Documents[document.Id] = document;

        var readable = "Left column line one.\nRight column line one.";

        await repo.SaveDocumentPagesAsync(new[] { new DocumentPage(document.Id, 1, readable) });

        var pages = new[] { new PdfPageText(1, readable + " Revised.") };

        Assert.Equal(0, await Index(repo).RepairTextAsync(document, pages));

        var stored = Assert.Single(await repo.ListDocumentPagesAsync(document.Id));
        Assert.Equal(readable, stored.Text);
    }
}
