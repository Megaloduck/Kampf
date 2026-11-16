using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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
/// The rendered page surface: a marked range has to be painted over the bitmap
/// at the word boxes, not only tinted in the fallback text view.
/// </summary>
public sealed class PdfPageHighlightTests
{
    [AvaloniaFact]
    public void A_search_hit_is_painted_over_the_page_image()
    {
        var page = new RenderTargetBitmap(new PixelSize(200, 100), new Vector(96, 96));
        using (var context = page.CreateDrawingContext())
            context.FillRectangle(Brushes.White, new Rect(0, 0, 200, 100));

        var layout = new PdfPageLayout(1, "alpha beta", 200, 100, new[]
        {
            new PdfWordBox("alpha", 0, 5, 0.10, 0.10, 0.20, 0.10),
            new PdfWordBox("beta", 6, 4, 0.40, 0.10, 0.15, 0.10),
        });

        var view = new PdfPageView
        {
            PageImage = page,
            Layout = layout,
            Spans = new[] { new PageSpan(6, 4, PageSpanKind.Match) },
        };

        view.Measure(new Size(200, 100));
        view.Arrange(new Rect(0, 0, 200, 100));

        var frame = new RenderTargetBitmap(new PixelSize(200, 100), new Vector(96, 96));
        frame.Render(view);

        var pixels = new byte[200 * 100 * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);

        try
        {
            frame.CopyPixels(
                new PixelRect(0, 0, 200, 100),
                handle.AddrOfPinnedObject(),
                pixels.Length,
                200 * 4);
        }
        finally
        {
            handle.Free();
        }

        // "beta" sits at x 0.40..0.55, y 0.10..0.20 of a 200x100 page.
        var offset = (15 * 200 + 95) * 4;
        var (b, g, r) = (pixels[offset], pixels[offset + 1], pixels[offset + 2]);

        Assert.True(
            g > r + 20 && g > b + 20,
            $"expected a green mark over the matched word, got rgb({r},{g},{b})");
    }

    [AvaloniaFact]
    public void A_search_hit_is_tinted_in_the_text_surface()
    {
        var view = new PageTextView
        {
            PageText = "alpha beta gamma",
            Spans = new[] { new PageSpan(6, 4, PageSpanKind.Match) },
        };

        view.Measure(new Size(200, 100));
        view.Arrange(new Rect(0, 0, 200, 100));

        var frame = new RenderTargetBitmap(new PixelSize(200, 100), new Vector(96, 96));
        frame.Render(view);

        var pixels = new byte[200 * 100 * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);

        try
        {
            frame.CopyPixels(
                new PixelRect(0, 0, 200, 100),
                handle.AddrOfPinnedObject(),
                pixels.Length,
                200 * 4);
        }
        finally
        {
            handle.Free();
        }

        var marked = MarkedPixels(pixels, 200 * 4, 200, 100);

        Assert.True(marked > 20, $"the text surface should tint the match, found {marked} marked pixel(s)");
    }

    /// <summary>
    /// The whole path a reader takes: a real PDF, the real reader view, a typed
    /// search, and the pixels that end up on screen.
    /// </summary>
    [AvaloniaFact]
    public async Task Typing_a_search_puts_green_marks_on_the_page_the_reader_shows()
    {
        var root = Path.Combine(Path.GetTempPath(), "kampf-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var vault = await Vault.OpenAsync(Path.Combine(root, "vault"));
            var importer = new ImportService(vault.Repository, vault.PdfExtractor, vault.Files, vault.Pages);

            var builder = new PdfDocumentBuilder();
            var pdfPage = builder.AddPage(595, 842);
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            pdfPage.AddText(
                "Modularity compares the observed edge density of a partition.",
                12,
                new PdfPoint(50, 760),
                font);

            var path = Path.Combine(root, "paper.pdf");
            File.WriteAllBytes(path, builder.Build());
            await importer.ImportAsync(path);

            var viewModel = new MainViewModel(vault);
            await viewModel.RefreshCommand.ExecuteAsync(null);
            viewModel.SelectedDocument = Assert.Single(viewModel.Documents);

            var window = new Window
            {
                Width = 1100,
                Height = 900,
                Content = new ReaderView { DataContext = viewModel },
            };

            window.Show();

            await PumpUntilAsync(() =>
                viewModel.Pages.Count > 0 &&
                viewModel.Pages[0].Image is not null &&
                viewModel.Pages[0].Layout is { Words.Count: > 0 });

            window.UpdateLayout();
            var before = MarkedPixels(window.CaptureRenderedFrame());

            viewModel.ReaderSearchQuery = "edge density";

            await PumpUntilAsync(() =>
                viewModel.Pages[0].Spans.Any(span => span.Kind == PageSpanKind.Match));

            await Task.Delay(120);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            // Capture twice: the headless frame can lag one pass behind the change.
            var firstCapture = window.CaptureRenderedFrame();
            var secondCapture = window.CaptureRenderedFrame();

            var after = Math.Max(MarkedPixels(firstCapture), MarkedPixels(secondCapture));

            var pageView = window.GetVisualDescendants().OfType<PdfPageView>().FirstOrDefault();
            var state = pageView is null
                ? "no PdfPageView in the visual tree"
                : $"control spans={pageView.Spans?.Count ?? -1}, matches=" +
                  $"{pageView.Spans?.Count(span => span.Kind == PageSpanKind.Match) ?? -1}, " +
                  $"size={pageView.Bounds.Width:0}x{pageView.Bounds.Height:0}, " +
                  $"visible={pageView.IsVisible}, words={pageView.Layout?.Words.Count ?? -1}";

            // A headless capture can repaint only dirty regions, so the measured
            // delta is a lower bound; the isolated control test pins the geometry.
            Assert.Equal("1 of 1", viewModel.ReaderSearchLabel);
            Assert.True(
                after > before + 40,
                $"the matched words should be marked on the page: {before} green pixel(s) before, " +
                $"{after} after ({state})");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task PumpUntilAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();

            if (condition())
                return;

            await Task.Delay(20);
        }

        Assert.Fail("timed out waiting for the page to render");
    }

    /// <summary>Pixels tinted by either match colour — green for a hit, amber for the current one.</summary>
    private static int MarkedPixels(WriteableBitmap? frame)
    {
        if (frame is null)
            return 0;

        using var buffer = frame.Lock();

        return MarkedPixels(buffer);
    }

    private static int MarkedPixels(ILockedFramebuffer buffer)
    {
        var width = buffer.Size.Width;
        var height = buffer.Size.Height;
        var bytes = new byte[buffer.RowBytes * height];
        Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);

        return MarkedPixels(bytes, buffer.RowBytes, width, height);
    }

    private static int MarkedPixels(byte[] bytes, int rowBytes, int width, int height)
    {
        var count = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * rowBytes + x * 4;
                var b = bytes[i];
                var g = bytes[i + 1];
                var r = bytes[i + 2];

                // Green mark: g well above both others. Amber mark: r and g well
                // above b, with r high enough to rule out pale greys.
                var green = g > r + 15 && g > b + 15;
                var amber = r > 200 && g > 120 && b < g - 25;

                if (green || amber)
                    count++;
            }
        }

        return count;
    }
}
