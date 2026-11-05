using Kampf.Application.Embeddings;
using Kampf.Application.Services;
using Kampf.Infrastructure;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Kampf.Infrastructure.Tests;

/// <summary>
/// End to end over a real vault: importing a PDF stores its page text, that text
/// is searchable and citable without any note, and reopening the paper does not
/// re-embed pages that did not change.
/// </summary>
public sealed class PaperRetrievalTests
{
    [Fact]
    public async Task An_imported_paper_answers_from_its_own_pages()
    {
        var root = Path.Combine(Path.GetTempPath(), "kampf-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var source = Path.Combine(root, "source.pdf");
            await File.WriteAllBytesAsync(source, BuildPdf(
                "Modularity maximisation compares the observed edge density of a partition " +
                "with the density expected from a random null model of the same degrees."));

            var vault = await Vault.OpenAsync(Path.Combine(root, "vault"));
            var importer = new ImportService(vault.Repository, vault.PdfExtractor, vault.Files, vault.Pages);

            var imported = await importer.ImportAsync(source);
            var document = imported.Document;

            // The page text survives the import, so nothing has to re-parse the PDF.
            var pages = await vault.Repository.ListDocumentPagesAsync(document.Id);
            var page = Assert.Single(pages);
            Assert.Equal(1, page.Page);
            Assert.Contains("edge density", page.Text);

            // A page vector exists for the active model.
            var vectors = await vault.Repository.ListPageEmbeddingsAsync(vault.Embeddings.Model);
            Assert.Contains(vectors, v => v.DocumentId == document.Id && v.Page == 1);

            // Reindexing an unchanged paper embeds nothing.
            Assert.Equal(0, await vault.Pages.IndexAsync(new[] { document }));

            var search = new SearchService(
                vault.Repository,
                new NoteEmbeddingIndex(vault.Repository, vault.Embeddings),
                vault.Pages);

            var outcome = await search.SearchAsync("edge density");
            var hit = Assert.Single(outcome.Hits);
            Assert.Equal(document.Id, hit.Id);
            Assert.Equal(1, hit.Page);

            // No note was ever written, and the paper still answers with a citation.
            var answer = await new ChatService(vault.Repository, search, null).AskAsync("edge density");
            var citation = Assert.Single(answer.Citations);
            Assert.True(citation.IsPage);
            Assert.Equal(document.Id, citation.DocumentId);
            Assert.Equal(1, citation.Page);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // best effort cleanup
            }
        }
    }

    private static byte[] BuildPdf(string text)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddText(text, 12, new PdfPoint(50, 700), font);
        return builder.Build();
    }
}
