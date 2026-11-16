using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Kampf.ViewModels;

namespace Kampf.Controls;

/// <summary>
/// A page's extracted text with the passages behind the words that matter:
/// search hits, the hit the reader is on, a cited quote and the notes captured
/// from the page. Drawn as tinted runs, so the marks line up with the wrapped
/// text at any width and in either theme.
///
/// This is the text surface of the reader — what is shown while a page renders,
/// when rendering is unavailable, and whenever the reader switches a page to
/// text. Selection is untouched, so a quote can still be captured from here.
/// </summary>
public sealed class PageTextView : SelectableTextBlock
{
    public static readonly StyledProperty<string?> PageTextProperty =
        AvaloniaProperty.Register<PageTextView, string?>(nameof(PageText));

    public static readonly StyledProperty<IReadOnlyList<PageSpan>?> SpansProperty =
        AvaloniaProperty.Register<PageTextView, IReadOnlyList<PageSpan>?>(nameof(Spans));

    private static readonly IBrush NoteBrush = new SolidColorBrush(Color.FromArgb(96, 250, 204, 21));
    private static readonly IBrush CitationBrush = new SolidColorBrush(Color.FromArgb(104, 96, 165, 250));
    private static readonly IBrush MatchBrush = new SolidColorBrush(Color.FromArgb(104, 74, 222, 128));
    private static readonly IBrush ActiveMatchBrush = new SolidColorBrush(Color.FromArgb(168, 251, 146, 60));

    public string? PageText
    {
        get => GetValue(PageTextProperty);
        set => SetValue(PageTextProperty, value);
    }

    public IReadOnlyList<PageSpan>? Spans
    {
        get => GetValue(SpansProperty);
        set => SetValue(SpansProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == PageTextProperty || change.Property == SpansProperty)
            Rebuild();
    }

    private static IBrush BrushFor(PageSpanKind kind) => kind switch
    {
        PageSpanKind.Citation => CitationBrush,
        PageSpanKind.Match => MatchBrush,
        PageSpanKind.ActiveMatch => ActiveMatchBrush,
        _ => NoteBrush,
    };

    /// <summary>
    /// Cuts the page text into plain and marked runs. Marks are applied in the
    /// order they sit on the page, and one that starts inside an earlier mark
    /// only covers what is left of it.
    /// </summary>
    private void Rebuild()
    {
        Inlines ??= new InlineCollection();
        Inlines.Clear();

        var text = PageText ?? string.Empty;
        if (text.Length == 0)
            return;

        var marks = (Spans ?? Array.Empty<PageSpan>())
            .Where(span => span.Length > 0 && span.Start >= 0 && span.Start < text.Length)
            .Select(span => (
                Start: span.Start,
                End: Math.Min(text.Length, span.Start + span.Length),
                Brush: BrushFor(span.Kind)))
            .OrderBy(span => span.Start)
            .ThenByDescending(span => span.End - span.Start)
            .ToList();

        var cursor = 0;

        foreach (var mark in marks)
        {
            if (mark.End <= cursor)
                continue;

            var start = Math.Max(mark.Start, cursor);

            if (start > cursor)
                Inlines.Add(new Run(text[cursor..start]));

            Inlines.Add(new Run(text[start..mark.End]) { Background = mark.Brush });
            cursor = mark.End;
        }

        if (cursor < text.Length)
            Inlines.Add(new Run(text[cursor..]));
    }
}
