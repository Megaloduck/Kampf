using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kampf.Application.Models;
using Kampf.Application.Services;
using Kampf.Controls;
using Kampf.Infrastructure;
using Kampf.ViewModels;
using Kampf.Views;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Kampf.Ui.Tests;

/// <summary>
/// The reader end to end, headless: a real vault, a real PDF, the real view.
/// A page has to render to an image, and dragging across words has to produce
/// the text a note will quote.
/// </summary>
public sealed class ReaderViewTests : IDisposable
{
    private const string Sentence =
        "Modularity maximisation compares the observed edge density of a partition.";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "kampf-tests", Guid.NewGuid().ToString("n"));

    public ReaderViewTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [AvaloniaFact]
    public async Task A_paper_renders_and_a_drag_selects_the_page_text()
    {
        var vault = await Vault.OpenAsync(Path.Combine(_root, "vault"));
        var importer = new ImportService(vault.Repository, vault.PdfExtractor, vault.Files, vault.Pages);
        var source = WritePdf();
        await importer.ImportAsync(source);

        var viewModel = new MainViewModel(vault);
        await viewModel.RefreshCommand.ExecuteAsync(null);

        var document = Assert.Single(viewModel.Documents);
        viewModel.SelectedDocument = document;

        var view = new ReaderView { DataContext = viewModel };
        var window = new Window { Width = 1100, Height = 900, Content = view };
        window.Show();

        // Background rendering finishes when the page's bitmap is set.
        await PumpAsync(() => viewModel.Pages.Count > 0 && viewModel.Pages[0].Image is not null);

        var page = viewModel.Pages[0];
        Assert.NotNull(page.Layout);
        Assert.True(page.HasImage);
        Assert.False(page.ShowTextBlock, "the page image replaces the text box");
        Assert.Contains("edge density", page.Text);

        window.UpdateLayout();

        var pageView = view.GetVisualDescendants().OfType<PdfPageView>().FirstOrDefault();
        Assert.NotNull(pageView);
        Assert.True(pageView!.Bounds.Width > 100, "the page control lays out at the reader's width");

        // Drag from the middle of one word to the middle of a later word.
        var words = page.Layout!.Words;
        var from = words.First(w => w.Text == "observed");
        var to = words.First(w => w.Text == "density");

        var origin = pageView.TranslatePoint(new Point(0, 0), window) ?? new Point(0, 0);
        var width = pageView.Bounds.Width;
        var height = width * page.Image!.PixelSize.Height / page.Image.PixelSize.Width;

        // Press in the first quarter of the first word and release in the last
        // quarter of the last one: the selection is unambiguous either way.
        Point At(PdfWordBox word, double fraction) => new(
            origin.X + (word.X + word.Width * fraction) * width,
            origin.Y + (word.Y + word.Height / 2) * height);

        window.MouseDown(At(from, 0.25), MouseButton.Left);
        window.MouseMove(At(to, 0.75));
        window.MouseUp(At(to, 0.75), MouseButton.Left);

        Assert.Equal("observed edge density", pageView.SelectedText);
        Assert.True(pageView.SelectionRange.Length > 0);

        // Capturing that selection writes a note and marks the page where it came from.
        await viewModel.CreateNoteFromHighlightAsync(1, pageView.SelectedText, pageView.SelectionRange.Start,
            pageView.SelectionRange.Length);

        Assert.Contains(page.Spans, span => span.Kind == PageSpanKind.Note);
    }

    [AvaloniaFact]
    public async Task A_captured_quote_is_marked_when_the_paper_is_reopened()
    {
        var vault = await Vault.OpenAsync(Path.Combine(_root, "vault"));
        var importer = new ImportService(vault.Repository, vault.PdfExtractor, vault.Files, vault.Pages);
        var imported = await importer.ImportAsync(WritePdf());

        var notes = new NoteService(vault.Repository);
        await notes.CreateFromHighlightAsync(imported.Document, 1, "observed edge density");

        var viewModel = new MainViewModel(vault);
        await viewModel.RefreshCommand.ExecuteAsync(null);
        viewModel.SelectedDocument = Assert.Single(viewModel.Documents);

        await PumpAsync(() => viewModel.Pages.Count > 0 &&
                              viewModel.Pages[0].Spans.Any(span => span.Kind == PageSpanKind.Note));

        var span = Assert.Single(viewModel.Pages[0].Spans);
        Assert.NotNull(span.NoteId);
        Assert.Equal("observed edge density",
            viewModel.Pages[0].Text.Substring(span.Start, span.Length));
    }

    private static async Task PumpAsync(Func<bool> complete)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            if (complete())
                return;

            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }

        throw new TimeoutException("the reader never finished rendering the page");
    }

    private string WritePdf(params string[]? lines)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(595, 842);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        var y = 760.0;
        foreach (var line in lines is { Length: > 0 } ? lines : new[] { Sentence })
        {
            page.AddText(line, 12, new PdfPoint(50, y), font);
            y -= 20;
        }

        var path = Path.Combine(_root, "paper.pdf");
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    [AvaloniaFact]
    public async Task Searching_a_paper_marks_every_hit_and_steps_through_them()
    {
        var vault = await Vault.OpenAsync(Path.Combine(_root, "vault"));
        var importer = new ImportService(vault.Repository, vault.PdfExtractor, vault.Files, vault.Pages);
        await importer.ImportAsync(WritePdf(
            "Modularity compares the observed edge density of a partition.",
            "A second line repeats the phrase edge density for the search."));

        var viewModel = new MainViewModel(vault);
        await viewModel.RefreshCommand.ExecuteAsync(null);
        viewModel.SelectedDocument = Assert.Single(viewModel.Documents);

        var view = new ReaderView { DataContext = viewModel };
        var window = new Window { Width = 1100, Height = 900, Content = view };
        window.Show();

        await PumpAsync(() => viewModel.Pages.Count > 0 && viewModel.Pages[0].Image is not null);

        viewModel.ReaderSearchQuery = "edge density";

        var page = viewModel.Pages[0];
        Assert.Equal(2, page.Spans.Count(span => span.Kind == PageSpanKind.Match));
        Assert.Single(page.Spans, span => span.Kind == PageSpanKind.ActiveMatch);
        Assert.True(viewModel.HasReaderSearch);
        Assert.Equal("1 of 2", viewModel.ReaderSearchLabel);

        viewModel.NextReaderMatchCommand.Execute(null);
        Assert.Equal("2 of 2", viewModel.ReaderSearchLabel);

        // A query that matches nothing says so and marks nothing.
        viewModel.ReaderSearchQuery = "crystallography";
        Assert.Equal("No matches", viewModel.ReaderSearchLabel);
        Assert.DoesNotContain(page.Spans, span => span.Kind == PageSpanKind.Match);
    }

    [AvaloniaFact]
    public async Task Zoom_and_position_are_remembered_for_the_paper()
    {
        var vault = await Vault.OpenAsync(Path.Combine(_root, "vault"));
        var importer = new ImportService(vault.Repository, vault.PdfExtractor, vault.Files, vault.Pages);
        await importer.ImportAsync(WritePdf());

        var first = new MainViewModel(vault);
        await first.RefreshCommand.ExecuteAsync(null);
        first.SelectedDocument = Assert.Single(first.Documents);

        var view = new ReaderView { DataContext = first };
        var window = new Window { Width = 1100, Height = 900, Content = view };
        window.Show();

        await PumpAsync(() => first.Pages.Count > 0 && first.Pages[0].Image is not null);

        var defaultWidth = first.Pages[0].ZoomWidth;
        first.ZoomInCommand.Execute(null);

        Assert.True(first.Pages[0].ZoomWidth > defaultWidth, "the page card grows with the zoom");
        Assert.Equal("110%", first.ZoomLabel);

        first.UpdateReadingPosition(1, 0.4);
        await first.SaveReadingStateAsync();

        var second = new MainViewModel(vault);
        await second.RefreshCommand.ExecuteAsync(null);

        PageJump? jump = null;
        second.PageJumpRequested += (_, requested) => jump ??= requested;
        second.SelectedDocument = Assert.Single(second.Documents);

        await PumpAsync(() => jump is not null);

        Assert.Equal(1.1, second.ReaderZoom, 3);
        Assert.Equal(1, jump!.Page);
        Assert.Equal(0.4, jump.Offset, 3);
    }
}
