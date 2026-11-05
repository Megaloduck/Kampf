using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Kampf.Application.Models;

namespace Kampf.ViewModels;

/// <summary>Why a passage is marked on the page.</summary>
public enum PageSpanKind
{
    /// <summary>A note captured from this passage.</summary>
    Note,

    /// <summary>The passage a chat citation or search hit pointed at.</summary>
    Citation,

    /// <summary>An in-page search hit.</summary>
    Match,

    /// <summary>The search hit the reader is currently on.</summary>
    ActiveMatch,
}

/// <summary>
/// A request to bring one page of the open paper into view. <see cref="Quote"/>
/// is the passage a citation pointed at, when there is one, so the reader can
/// mark it on the page instead of only scrolling there.
/// </summary>
public sealed record PageJump(int Page, string? Quote = null, double Offset = 0);

/// <summary>A character range in <see cref="PageViewModel.Text"/> to mark on the page.</summary>
public sealed record PageSpan(int Start, int Length, PageSpanKind Kind, string? NoteId = null);

/// <summary>
/// One page of a paper: its stored text, the rendered page image, the word boxes
/// that place the text on that image, and the passages to mark on it.
/// </summary>
public sealed partial class PageViewModel : ObservableObject
{
    public PageViewModel(int number, string text)
    {
        Number = number;
        Text = text;
    }

    public int Number { get; }

    public string Text { get; }

    public string HeaderLabel => $"Page {Number}";

    /// <summary>Word boxes and page size for the rendered image; null until loaded.</summary>
    [ObservableProperty]
    private PdfPageLayout? _layout;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage), nameof(ShowTextBlock))]
    private Bitmap? _image;

    public bool HasImage => Image is not null;

    [ObservableProperty]
    private bool _isRendering;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRenderError))]
    private string? _renderError;

    public bool HasRenderError => !string.IsNullOrEmpty(RenderError);

    /// <summary>Page scale, shared by every page of the open paper.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomPercent))]
    private double _zoom = ReadingState.DefaultZoom;

    public string ZoomPercent => $"{Math.Round(Zoom * 100):0}%";

    /// <summary>Width the page card takes at the current zoom, set by the reader.</summary>
    [ObservableProperty] private double _zoomWidth = 640;

    /// <summary>The reader's "show the extracted text" toggle.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTextBlock))]
    private bool _showText;

    /// <summary>
    /// The text box is the fallback: shown while the page renders, when
    /// rendering is unavailable, and whenever the reader asks for it.
    /// </summary>
    public bool ShowTextBlock => ShowText || !HasImage;

    [ObservableProperty]
    private IReadOnlyList<PageSpan> _spans = Array.Empty<PageSpan>();

    /// <summary>
    /// The last text the reader selected on this page. Kept because the capture
    /// button takes focus away from the page control.
    /// </summary>
    public string SelectedText { get; set; } = string.Empty;

    /// <summary>Adds a highlight, replacing one that already covers the same range.</summary>
    public void AddSpan(int start, int length, PageSpanKind kind, string? noteId = null)
    {
        if (length <= 0 || start < 0 || start + length > Text.Length)
            return;

        var kept = Spans
            .Where(span => !(span.Kind == kind && span.Start == start && span.Length == length))
            .ToList();

        kept.Add(new PageSpan(start, length, kind, noteId));
        Spans = kept;
    }

    /// <summary>Replaces the cited passage; every other page's is cleared by the reader.</summary>
    public void SetCitation(int? start, int? length)
    {
        var kept = Spans.Where(span => span.Kind != PageSpanKind.Citation).ToList();

        if (start is { } s && length is { } l && l > 0 && s + l <= Text.Length)
            kept.Add(new PageSpan(s, l, PageSpanKind.Citation));

        Spans = kept;
    }

    public void ClearCitation() => SetCitation(null, null);

    /// <summary>Drops every span of one kind, e.g. the previous search's hits.</summary>
    public void ClearSpans(PageSpanKind kind)
    {
        if (Spans.All(span => span.Kind != kind))
            return;

        Spans = Spans.Where(span => span.Kind != kind).ToList();
    }
}
