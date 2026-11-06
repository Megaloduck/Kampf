using Kampf.Application.Services;
using Kampf.Infrastructure;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Kampf.Infrastructure.Tests;

public sealed class PdfImportTests
{
    [Fact]
    public async Task Importing_a_pdf_extracts_text_and_highlight_notes_join_the_graph()
    {
        var root = Path.Combine(Path.GetTempPath(), "kampf-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var source = Path.Combine(root, "source.pdf");
            await File.WriteAllBytesAsync(source, BuildPdf(
                "Modularity measures the strength of community structure."));

            var vault = await Vault.OpenAsync(Path.Combine(root, "vault"));
            var importer = new ImportService(vault.Repository, vault.PdfExtractor, vault.Files);

            var result = await importer.ImportAsync(source);

            Assert.Equal(1, result.Document.PageCount);
            Assert.True(File.Exists(result.Document.FilePath));
            Assert.NotEqual(source, result.Document.FilePath);
            Assert.Contains("Modularity", result.Extraction!.Pages[0].Text);

            var notes = new NoteService(vault.Repository);
            var note = await notes.CreateFromHighlightAsync(
                result.Document,
                1,
                "Modularity measures the strength of community structure.");

            Assert.Equal(result.Document.Id, note.SourceDocumentId);
            Assert.Equal(1, note.SourcePage);

            var graph = await new GraphService(vault.Repository).BuildAsync();
            Assert.Equal(2, graph.Nodes.Count);
            var edge = Assert.Single(graph.Edges);
            Assert.Equal(note.Id, edge.SourceId);
            Assert.Equal(result.Document.Id, edge.TargetId);

            // The extracted pages are readable back out of the vault copy.
            var extraction = await vault.PdfExtractor.ExtractAsync(result.Document.FilePath);
            Assert.Single(extraction.Pages);
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
