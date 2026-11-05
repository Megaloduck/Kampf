using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Kampf.Application.Models;
using Kampf.ViewModels;

namespace Kampf.Controls;

/// <summary>
/// A real PDF page: the rendered bitmap, the passages already captured as notes,
/// the passage a citation points at, and a drag selection the reader can turn
/// into a note. Selection is a character range in the page's stored text, which
/// is why the note it produces keeps an exact quote.
/// </summary>
public sealed class PdfPageView : Control
{
    public static readonly StyledProperty<Bitmap?> PageImageProperty =
        AvaloniaProperty.Register<PdfPageView, Bitmap?>(nameof(PageImage));

    public static readonly StyledProperty<PdfPageLayout?> LayoutProperty =
        AvaloniaProperty.Register<PdfPageView, PdfPageLayout?>(nameof(Layout));

    public static readonly StyledProperty<IReadOnlyList<PageSpan>?> SpansProperty =
        AvaloniaProperty.Register<PdfPageView, IReadOnlyList<PageSpan>?>(nameof(Spans));

    private static readonly IBrush NoteBrush = new SolidColorBrush(Color.FromArgb(96, 250, 204, 21));
    private static readonly IBrush CitationBrush = new SolidColorBrush(Color.FromArgb(104, 96, 165, 250));
    private static readonly IBrush MatchBrush = new SolidColorBrush(Color.FromArgb(88, 74, 222, 128));
    private static readonly IBrush ActiveMatchBrush = new SolidColorBrush(Color.FromArgb(150, 251, 146, 60));
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(120, 59, 130, 246));

    private int _anchor = -1;
    private int _focus = -1;
    private bool _dragging;

    public PdfPageView()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public Bitmap? PageImage
    {
        get => GetValue(PageImageProperty);
        set => SetValue(PageImageProperty, value);
    }

    public PdfPageLayout? Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public IReadOnlyList<PageSpan>? Spans
    {
        get => GetValue(SpansProperty);
        set => SetValue(SpansProperty, value);
    }

    /// <summary>The text selected by dragging, exactly as it appears in the page text.</summary>
    public string SelectedText { get; private set; } = string.Empty;

    /// <summary>Raised when a drag finishes, so the view can remember the quote.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Raised on double-click over a captured passage, with the note it backs.</summary>
    public event EventHandler<string>? NoteRequested;

    /// <summary>The selection as offsets into <see cref="Layout"/>'s text.</summary>
    public (int Start, int Length) SelectionRange
    {
        get
        {
            if (_anchor < 0 || _focus < 0)
                return (0, 0);

            var start = Math.Min(_anchor, _focus);
            var end = Math.Max(_anchor, _focus);
            return (start, end - start);
        }
    }

    public void ClearSelection()
    {
        _anchor = -1;
        _focus = -1;
        SelectedText = string.Empty;
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == PageImageProperty)
            InvalidateMeasure();

        if (change.Property == PageImageProperty ||
            change.Property == LayoutProperty ||
            change.Property == SpansProperty)
            InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 620 : availableSize.Width;
        width = Math.Max(width, 120);

        var image = PageImage;
        if (image is null || image.PixelSize.Width <= 0)
            return new Size(width, 120);

        var height = width * image.PixelSize.Height / image.PixelSize.Width;
        return new Size(width, height);
    }

    public override void Render(DrawingContext context)
    {
        var page = PageRect();
        if (page.Width <= 0 || page.Height <= 0)
            return;

        if (PageImage is not { } image)
        {
            DrawPlaceholder(context, page);
            return;
        }

        context.DrawImage(
            image,
            new Rect(0, 0, image.PixelSize.Width, image.PixelSize.Height),
            page);

        var words = Layout?.Words;
        if (words is null || words.Count == 0)
            return;

        foreach (var span in Spans ?? Array.Empty<PageSpan>())
        {
            var brush = span.Kind switch
            {
                PageSpanKind.Citation => CitationBrush,
                PageSpanKind.Match => MatchBrush,
                PageSpanKind.ActiveMatch => ActiveMatchBrush,
                _ => NoteBrush,
            };
            foreach (var rect in SpanRects(words, span.Start, span.Start + span.Length, page))
                context.DrawRectangle(brush, null, rect);
        }

        var (start, length) = SelectionRange;
        if (length > 0)
        {
            foreach (var rect in SpanRects(words, start, start + length, page))
                context.DrawRectangle(SelectionBrush, null, rect);
        }
    }

    private void DrawPlaceholder(DrawingContext context, Rect page)
    {
        var background = new SolidColorBrush(Color.FromArgb(18, 140, 148, 172));
        context.DrawRectangle(background, null, page, 4, 4);

        var text = new FormattedText(
            "Rendering page\u2026",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            12,
            ThemePalette.Brush("MutedBrush"));

        context.DrawText(text, new Point(12, Math.Max(8, page.Height / 2 - 8)));
    }

    // ------------------------------------------------------------- geometry

    /// <summary>Where the bitmap lands: full width, aspect preserved.</summary>
    private Rect PageRect()
    {
        var width = Bounds.Width;
        var image = PageImage;

        if (width <= 0 || image is null || image.PixelSize.Width <= 0)
            return new Rect(0, 0, Math.Max(width, 0), Math.Max(Bounds.Height, 0));

        var height = width * image.PixelSize.Height / image.PixelSize.Width;
        return new Rect(0, 0, width, height);
    }

    private static Rect ToRect(PdfWordBox word, Rect page) =>
        new(
            word.X * page.Width - 0.5,
            word.Y * page.Height,
            Math.Max(1, word.Width * page.Width + 1),
            Math.Max(1, word.Height * page.Height));

    /// <summary>Merges the words of a character range into one box per line.</summary>
    private static List<Rect> SpanRects(
        IReadOnlyList<PdfWordBox> words,
        int start,
        int end,
        Rect page)
    {
        var rects = new List<Rect>();
        Rect? line = null;

        foreach (var word in words)
        {
            if (word.Start >= end)
                break;

            if (word.Start + word.Length <= start)
                continue;

            var rect = ToRect(word, page);

            if (line is { } current && Math.Abs(rect.Y - current.Y) <= rect.Height * 0.6)
            {
                var top = Math.Min(current.Y, rect.Y);
                var bottom = Math.Max(current.Bottom, rect.Bottom);
                var left = Math.Min(current.X, rect.X);
                var right = Math.Max(current.Right, rect.Right);
                line = new Rect(left, top, right - left, bottom - top);
                continue;
            }

            if (line is { } done)
                rects.Add(done);

            line = rect;
        }

        if (line is { } last)
            rects.Add(last);

        return rects;
    }

    // ----------------------------------------------------------- interaction

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var point = e.GetPosition(this);

        if (e.ClickCount == 2 && NoteAt(point) is { } noteId)
        {
            NoteRequested?.Invoke(this, noteId);
            e.Handled = true;
            return;
        }

        var offset = OffsetAt(point);
        if (offset < 0)
            return;

        _anchor = offset;
        _focus = offset;
        _dragging = true;
        SelectedText = string.Empty;
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!_dragging)
            return;

        var offset = OffsetAt(e.GetPosition(this));
        if (offset < 0 || offset == _focus)
            return;

        _focus = offset;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (!_dragging)
            return;

        _dragging = false;
        e.Pointer.Capture(null);

        var (start, length) = SelectionRange;
        SelectedText = Layout is { } layout && length > 0 && start + length <= layout.Text.Length
            ? layout.Text.Substring(start, length).Trim()
            : string.Empty;

        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>The nearest character offset to a point, or -1 when there is no layout yet.</summary>
    private int OffsetAt(Point point)
    {
        var layout = Layout;
        var page = PageRect();

        if (layout is null || layout.Words.Count == 0 || page.Width <= 0 || page.Height <= 0)
            return -1;

        var x = point.X / page.Width;
        var y = point.Y / page.Height;

        PdfWordBox? nearest = null;
        var best = double.MaxValue;

        foreach (var word in layout.Words)
        {
            if (word.Contains(x, y))
                return x < word.X + word.Width / 2 ? word.Start : word.Start + word.Length;

            var dx = Math.Max(0, Math.Max(word.X - x, x - word.Right));
            var dy = Math.Max(0, Math.Max(word.Y - y, y - word.Bottom));

            // Vertical distance matters far more than horizontal: dragging into
            // the margin should stay on the line the pointer is over.
            var score = dy * 12 + dx;

            if (score < best)
            {
                best = score;
                nearest = word;
            }
        }

        if (nearest is not { } target)
            return -1;

        return x > target.X + target.Width / 2 ? target.Start + target.Length : target.Start;
    }

    /// <summary>Double-click support: the captured passage under the pointer.</summary>
    private string? NoteAt(Point point)
    {
        var layout = Layout;
        var page = PageRect();

        if (layout is null || Spans is null || page.Width <= 0)
            return null;

        var x = point.X / page.Width;
        var y = point.Y / page.Height;

        foreach (var word in layout.Words)
        {
            if (!word.Contains(x, y))
                continue;

            var span = Spans.FirstOrDefault(s =>
                s.NoteId is not null &&
                s.Kind == PageSpanKind.Note &&
                word.Start >= s.Start &&
                word.Start + word.Length <= s.Start + s.Length);

            return span?.NoteId;
        }

        return null;
    }
}
