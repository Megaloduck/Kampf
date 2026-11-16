using Kampf.Application.Models;
using Kampf.Infrastructure.Pdf;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Kampf.Infrastructure.Tests;

/// <summary>
/// What the vault stores as a page's text, and what the reader draws its
/// highlights against, come from the same assembler — so these tests cover both.
/// </summary>
public sealed class PdfTextExtractionTests
{
    [Fact]
    public async Task A_two_column_page_reads_the_left_column_then_the_right()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(595, 842);

        page.AddText("A Full Width Title", 14, new PdfPoint(50, 790), font);

        var rows = new[]
        {
            ("Left column line one about modularity.", "Right column line one about clustering."),
            ("Left column line two about partitions.", "Right column line two about resolution."),
            ("Left column line three about the graph.", "Right column line three about the dendrogram."),
        };

        var y = 740.0;
        foreach (var (left, right) in rows)
        {
            page.AddText(left, 10, new PdfPoint(50, y), font);
            page.AddText(right, 10, new PdfPoint(320, y), font);
            y -= 16;
        }

        var text = await ExtractTextAsync(builder);

        Assert.Equal(
            string.Join(
                '\n',
                "A Full Width Title",
                "Left column line one about modularity.",
                "Left column line two about partitions.",
                "Left column line three about the graph.",
                "Right column line one about clustering.",
                "Right column line two about resolution.",
                "Right column line three about the dendrogram."),
            text);
    }

    [Fact]
    public async Task A_word_broken_over_a_line_break_is_joined_back_up()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(595, 842);

        page.AddText("A hyphenation exam-", 11, new PdfPoint(50, 760), font);
        page.AddText("ple continues here.", 11, new PdfPoint(50, 744), font);

        Assert.Equal("A hyphenation example continues here.", await ExtractTextAsync(builder));
    }

    [Fact]
    public async Task Separate_lines_stay_separate_lines()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(595, 842);

        page.AddText("Modularity compares the observed edge density of a partition.", 12, new PdfPoint(50, 760), font);
        page.AddText("A second line is its own line.", 12, new PdfPoint(50, 744), font);

        Assert.Equal(
            "Modularity compares the observed edge density of a partition.\nA second line is its own line.",
            await ExtractTextAsync(builder));
    }

    /// <summary>
    /// The stored text and the boxes the reader draws over it must be the same
    /// text: a highlight is a character range into one applied to the other.
    /// </summary>
    [Fact]
    public async Task The_word_boxes_quote_the_stored_text_they_are_drawn_over()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(595, 842);

        page.AddText("Modularity compares the observed edge density of a partition.", 12, new PdfPoint(50, 760), font);
        page.AddText("A second line is its own line.", 12, new PdfPoint(50, 744), font);

        var (stored, layout) = await ExtractBothAsync(builder);

        Assert.Equal(stored, layout.Text);
        Assert.NotEmpty(layout.Words);

        var cursor = 0;

        foreach (var word in layout.Words)
        {
            Assert.InRange(word.Start, cursor, layout.Text.Length);
            Assert.Equal(word.Text, layout.Text.Substring(word.Start, word.Text.Length));
            cursor = word.Start + word.Text.Length;
        }

        Assert.Contains(layout.Words, word => word.Text == "edge");
    }

    // ------------------------------------------------------------------ setup

    private static async Task<string> ExtractTextAsync(PdfDocumentBuilder builder) =>
        (await ExtractBothAsync(builder)).Stored;

    private static async Task<(string Stored, PdfPageLayout Layout)> ExtractBothAsync(PdfDocumentBuilder builder)
    {
        var path = Path.Combine(Path.GetTempPath(), "kampf-extract-" + Guid.NewGuid().ToString("n") + ".pdf");
        File.WriteAllBytes(path, builder.Build());

        try
        {
            var extraction = await new PdfPigExtractor().ExtractAsync(path);
            var stored = Assert.Single(extraction.Pages).Text;
            var layout = await new PdfPageRenderer().GetLayoutAsync(path, 1);
            return (stored, layout);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
