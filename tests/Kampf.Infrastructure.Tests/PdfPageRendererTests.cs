using Kampf.Application.Services;
using Kampf.Infrastructure;
using Kampf.Infrastructure.Pdf;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Kampf.Infrastructure.Tests;

/// <summary>
/// The visual reader depends on two engines agreeing: PDFium for pixels, PdfPig
/// for word boxes. These tests prove both run on a real file and that the boxes
/// address the same text the vault stored.
/// </summary>
public sealed class PdfPageRendererTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "kampf-tests", Guid.NewGuid().ToString("n"));

    public PdfPageRendererTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A locked temp file must not fail the test run.
        }
    }

    [Fact]
    public async Task Word_boxes_address_the_stored_page_text()
    {
        var pdf = Write("Modularity maximisation compares observed edge density with a null model.");
        var renderer = new PdfPageRenderer();

        var layout = await renderer.GetLayoutAsync(pdf, 1);

        Assert.Equal("Modularity maximisation compares observed edge density with a null model.", layout.Text);
        Assert.NotEmpty(layout.Words);

        foreach (var word in layout.Words)
        {
            Assert.InRange(word.Start, 0, layout.Text.Length - 1);
            Assert.Equal(word.Text, layout.Text.Substring(word.Start, word.Length));
            Assert.InRange(word.X, 0, 1);
            Assert.InRange(word.Y, 0, 1);
            Assert.True(word.Width > 0 && word.Height > 0);
        }

        // Reading order, left to right on one line.
        var first = layout.Words[0];
        var last = layout.Words[^1];
        Assert.Equal("Modularity", first.Text);
        Assert.Equal("model.", last.Text);
        Assert.True(last.X > first.X);
    }

    [Fact]
    public async Task A_page_renders_to_opaque_pixels_with_ink()
    {
        var pdf = Write("Modularity maximisation compares observed edge density.");
        var renderer = new PdfPageRenderer();

        var image = await renderer.RenderAsync(pdf, 1, widthPixels: 600);

        Assert.Equal(1, image.Page);
        Assert.True(image.Width > 100);
        Assert.True(image.Height > 100);
        Assert.Equal(image.Width * image.Height * 4, image.Pixels.Length);

        var dark = 0;
        for (var i = 0; i < image.Pixels.Length; i += 4)
        {
            Assert.Equal(255, image.Pixels[i + 3]);
            if (image.Pixels[i] < 128 && image.Pixels[i + 1] < 128 && image.Pixels[i + 2] < 128)
                dark++;
        }

        Assert.True(dark > 0, "the rendered page should contain text pixels");
    }

    [Fact]
    public async Task Cached_layouts_are_dropped_on_forget()
    {
        var pdf = Write("Caching keeps the reader from re-parsing every scroll.");
        var renderer = new PdfPageRenderer();

        var first = await renderer.GetLayoutAsync(pdf, 1);
        var second = await renderer.GetLayoutAsync(pdf, 1);
        Assert.Same(first, second);

        renderer.Forget(pdf);
        var third = await renderer.GetLayoutAsync(pdf, 1);
        Assert.NotSame(first, third);
        Assert.Equal(first.Text, third.Text);
    }

    [Fact]
    public async Task A_vault_stores_papers_the_renderer_can_open()
    {
        var source = Write("Modularity maximisation compares observed edge density.");
        var vault = await Vault.OpenAsync(Path.Combine(_root, "vault"));
        var importer = new ImportService(vault.Repository, vault.PdfExtractor, vault.Files, vault.Pages);

        var imported = await importer.ImportAsync(source);
        var document = imported.Document;

        // The imported copy lives under assets/, not at the path it came from.
        Assert.NotEqual(source, document.FilePath);
        Assert.True(File.Exists(document.FilePath));

        var image = await vault.PageRenderer.RenderAsync(document.FilePath, 1, widthPixels: 500);
        Assert.True(image.Width > 100);
        Assert.Equal(image.Width * image.Height * 4, image.Pixels.Length);

        var layout = await vault.PageRenderer.GetLayoutAsync(document.FilePath, 1);
        Assert.Contains("edge density", layout.Text);
        Assert.NotEmpty(layout.Words);
    }

    private string Write(params string[] lines)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(595, 842);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        var y = 760.0;
        foreach (var line in lines)
        {
            page.AddText(line, 12, new PdfPoint(50, y), font);
            y -= 20;
        }

        var path = Path.Combine(_root, "page.pdf");
        File.WriteAllBytes(path, builder.Build());
        return path;
    }
}
