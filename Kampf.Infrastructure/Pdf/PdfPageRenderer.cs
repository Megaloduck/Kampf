using System.Text;
using Docnet.Core;
using Docnet.Core.Models;
using Kampf.Application.Abstractions;
using Kampf.Application.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Kampf.Infrastructure.Pdf;

/// <summary>
/// Renders PDF pages with PDFium (Docnet) and places their words with PdfPig.
/// The page bitmap and the word boxes come from two engines on purpose: PdfPig
/// already extracts the text the vault stores, so the boxes line up with the
/// highlights that were stored against that text.
/// </summary>
public sealed class PdfPageRenderer : IPdfPageRenderer
{
    /// <summary>PDFium is not thread-safe across documents; every render goes through here.</summary>
    private static readonly SemaphoreSlim RenderGate = new(1, 1);

    private const int MaxCachedFiles = 4;
    private const int MaxCachedLayouts = 240;

    private readonly object _cacheLock = new();
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _fileOrder = new();
    private readonly Dictionary<(string Path, int Page), PdfPageLayout> _layouts = new();

    public Task<PdfPageLayout> GetLayoutAsync(string filePath, int page, CancellationToken ct = default)
    {
        if (page < 1)
            throw new ArgumentOutOfRangeException(nameof(page), "Pages are 1-based.");

        lock (_cacheLock)
        {
            if (_layouts.TryGetValue((filePath, page), out var cached))
                return Task.FromResult(cached);
        }

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            using var document = PdfDocument.Open(filePath);
            if (page > document.NumberOfPages)
                throw new ArgumentOutOfRangeException(nameof(page), $"The PDF has only {document.NumberOfPages} pages.");

            var layout = BuildLayout(document.GetPage(page), page);

            lock (_cacheLock)
            {
                if (_layouts.Count >= MaxCachedLayouts)
                    _layouts.Clear();

                _layouts[(filePath, page)] = layout;
            }

            return layout;
        }, ct);
    }

    public Task<PdfPageImage> RenderAsync(
        string filePath,
        int page,
        int widthPixels,
        CancellationToken ct = default)
    {
        if (widthPixels < 72)
            widthPixels = 72;

        return Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();

            // The page width in points decides the scale: 72 points is one inch.
            var layout = await GetLayoutAsync(filePath, page, ct).ConfigureAwait(false);
            var scale = Math.Clamp(widthPixels / Math.Max(1, layout.WidthPoints), 0.2, 6.0);
            var bytes = LoadBytes(filePath);

            await RenderGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();

                using var docReader = DocLib.Instance.GetDocReader(bytes, new PageDimensions(scale));
                using var pageReader = docReader.GetPageReader(page - 1);

                var width = pageReader.GetPageWidth();
                var height = pageReader.GetPageHeight();

                // BGRA. PDFium leaves the page background transparent, so the
                // bitmap has to be laid onto white paper before it is opaque.
                var pixels = pageReader.GetImage();
                CompositeOntoPaper(pixels);

                return new PdfPageImage(page, width, height, pixels);
            }
            finally
            {
                RenderGate.Release();
            }
        }, ct);
    }

    /// <summary>
    /// Composites a page onto white paper and makes every pixel opaque.
    ///
    /// An unpainted pixel comes back from PDFium as (0,0,0,0): transparent, not
    /// white. Setting only the alpha byte to 255 turns the entire sheet black,
    /// with black ink on it, which is unreadable under every theme. Blending
    /// each channel towards white by its coverage gives the paper a page is
    /// meant to sit on, and leaves fully opaque ink untouched.
    /// </summary>
    private static void CompositeOntoPaper(byte[] bgra)
    {
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var alpha = bgra[i + 3];

            if (alpha != 255)
            {
                var paper = 255 - alpha;

                bgra[i] = Blend(bgra[i], alpha, paper);
                bgra[i + 1] = Blend(bgra[i + 1], alpha, paper);
                bgra[i + 2] = Blend(bgra[i + 2], alpha, paper);
                bgra[i + 3] = 255;
            }
        }
    }

    private static byte Blend(byte ink, int alpha, int paper) =>
        (byte)((ink * alpha + 255 * paper) / 255);

    public void Forget(string filePath)
    {
        lock (_cacheLock)
        {
            _files.Remove(filePath);
            _fileOrder.Remove(filePath);

            foreach (var key in _layouts.Keys.Where(k => k.Path == filePath).ToList())
                _layouts.Remove(key);
        }
    }

    private byte[] LoadBytes(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("The stored PDF is missing from the vault.", filePath);

        lock (_cacheLock)
        {
            if (_files.TryGetValue(filePath, out var cached))
                return cached;
        }

        var bytes = File.ReadAllBytes(filePath);

        lock (_cacheLock)
        {
            if (!_files.ContainsKey(filePath))
            {
                _files[filePath] = bytes;
                _fileOrder.AddLast(filePath);

                while (_fileOrder.Count > MaxCachedFiles)
                {
                    var oldest = _fileOrder.First!.Value;
                    _fileOrder.RemoveFirst();
                    _files.Remove(oldest);
                }
            }

            return _files[filePath];
        }
    }

    // ----------------------------------------------------------------- layout

    /// <summary>
    /// The page text and the boxes that place its words, both from the same
    /// assembler the importer uses — a stored quote is a character range, so the
    /// text this draws over must be the text that was stored.
    /// </summary>
    private static PdfPageLayout BuildLayout(Page page, int number)
    {
        var widthPoints = (double)page.Width;
        var heightPoints = (double)page.Height;

        if (widthPoints <= 0 || heightPoints <= 0)
            return new PdfPageLayout(number, string.Empty, 612, 792, Array.Empty<PdfWordBox>());

        var layout = PdfPageTextBuilder.Build(page);

        var boxes = layout.Words
            .Select(word => new PdfWordBox(
                word.Text,
                word.Start,
                word.Text.Length,
                Math.Clamp(word.Left / widthPoints, 0, 1),
                Math.Clamp(word.Top / heightPoints, 0, 1),
                Math.Clamp(word.Width / widthPoints, 0, 1),
                Math.Clamp(word.Height / heightPoints, 0, 1)))
            .ToList();

        return new PdfPageLayout(number, layout.Text, widthPoints, heightPoints, boxes);
    }
}
