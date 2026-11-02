using Kampf.Application.Services;
using Kampf.Infrastructure;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Kampf.Infrastructure.Tests;

public sealed class DocumentDeletionTests
{
    [Fact]
    public async Task Deleting_a_paper_removes_the_row_highlights_and_stored_file()
    {
        await WithVaultAsync(async vault =>
        {
            var importer = new ImportService(vault.Repository, vault.PdfExtractor, vault.Files);
            var notes = new NoteService(vault.Repository);

            var imported = await importer.ImportAsync(await WriteSamplePdfAsync(vault.RootPath));
            var document = imported.Document;

            var note = await notes.CreateFromHighlightAsync(document, 1, "Modularity measures community structure.");
            await notes.DeleteAsync(note.Id); // the anchor must go before the paper may

            Assert.True(File.Exists(document.FilePath));

            await importer.DeleteDocumentAsync(document);

            Assert.Empty(await vault.Repository.ListDocumentsAsync());
            Assert.Empty(await vault.Repository.ListHighlightsAsync(document.Id));
            Assert.False(File.Exists(document.FilePath));

            var audit = await vault.Repository.ListAuditAsync();
            Assert.Contains(audit, a => a.Action == "document.deleted");
        });
    }

    [Fact]
    public async Task A_paper_with_anchored_notes_cannot_be_deleted()
    {
        await WithVaultAsync(async vault =>
        {
            var importer = new ImportService(vault.Repository, vault.PdfExtractor, vault.Files);
            var notes = new NoteService(vault.Repository);

            var imported = await importer.ImportAsync(await WriteSamplePdfAsync(vault.RootPath));
            var document = imported.Document;
            await notes.CreateFromHighlightAsync(document, 1, "Modularity measures community structure.");

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => importer.DeleteDocumentAsync(document));

            Assert.Contains("note(s) were captured", error.Message);
            Assert.Single(await vault.Repository.ListDocumentsAsync());
            Assert.True(File.Exists(document.FilePath));
        });
    }

    [Fact]
    public async Task Stored_files_outside_the_vault_are_never_deleted()
    {
        await WithVaultAsync(async vault =>
        {
            var outside = Path.Combine(vault.RootPath, "notes", "keep-me.md");
            await File.WriteAllTextAsync(outside, "keep");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => vault.Files.DeleteAsync(outside));

            Assert.True(File.Exists(outside));
        });
    }

    private static async Task WithVaultAsync(Func<Vault, Task> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "kampf-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var vault = await Vault.OpenAsync(Path.Combine(root, "vault"));
            await body(vault);
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

    private static async Task<string> WriteSamplePdfAsync(string root)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddText("Modularity measures community structure.", 12, new PdfPoint(50, 700), font);

        var path = Path.Combine(root, "sample.pdf");
        await File.WriteAllBytesAsync(path, builder.Build());
        return path;
    }
}
