using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Kampf.Application.Models;
using Kampf.ViewModels;

namespace Kampf.Controls;

/// <summary>
/// Renders an Idea-Craft answer: the headings and bullets a model writes come out
/// as layout rather than literal asterisks, and each <c>[2]</c> in the prose is
/// clickable, opening source 2 the same way the chips under the answer do.
///
/// Only the emphasis a research answer actually uses is understood; anything else
/// stays exactly as the model wrote it.
/// </summary>
public sealed class AnswerText : StackPanel
{
    public static readonly StyledProperty<string?> AnswerProperty =
        AvaloniaProperty.Register<AnswerText, string?>(nameof(Answer));

    public static readonly StyledProperty<IReadOnlyList<ChatCitationViewModel>?> CitationsProperty =
        AvaloniaProperty.Register<AnswerText, IReadOnlyList<ChatCitationViewModel>?>(nameof(Citations));

    static AnswerText()
    {
        AnswerProperty.Changed.AddClassHandler<AnswerText>((control, _) => control.Rebuild());
        CitationsProperty.Changed.AddClassHandler<AnswerText>((control, _) => control.Rebuild());
    }

    public AnswerText()
    {
        Spacing = 5;
    }

    /// <summary>The answer, markdown-ish, as the model wrote it.</summary>
    public string? Answer
    {
        get => GetValue(AnswerProperty);
        set => SetValue(AnswerProperty, value);
    }

    /// <summary>The sources behind this answer, for making its markers clickable.</summary>
    public IReadOnlyList<ChatCitationViewModel>? Citations
    {
        get => GetValue(CitationsProperty);
        set => SetValue(CitationsProperty, value);
    }

    private void Rebuild()
    {
        Children.Clear();

        foreach (var block in AnswerMarkup.Parse(Answer))
            Children.Add(Build(block));
    }

    private Control Build(AnswerBlock block)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = block.Kind == AnswerBlockKind.Heading ? 13.5 : 12.5,
            FontWeight = block.Kind == AnswerBlockKind.Heading ? FontWeight.SemiBold : FontWeight.Normal,
            LineHeight = block.Kind == AnswerBlockKind.Heading ? 20 : 19.5,
            Margin = block.Kind == AnswerBlockKind.Heading ? new Thickness(0, 4, 0, 1) : default,
        };

        var citations = new List<(int Start, int End, ChatCitationViewModel Source)>();
        var offset = 0;

        foreach (var segment in block.Segments)
        {
            Append(text.Inlines!, segment, citations, ref offset);
        }

        if (citations.Count > 0)
        {
            text.Cursor = new Cursor(StandardCursorType.Hand);
            text.PointerPressed += (_, e) => Follow(e, text, citations);
        }

        if (block.Kind != AnswerBlockKind.Bullet)
            return text;

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("14,*"),
            Margin = new Thickness(2, 0, 0, 0),
        };

        var bullet = new TextBlock
        {
            Text = "\u2022",
            Foreground = ThemePalette.Brush("MutedBrush"),
            FontSize = 13,
        };

        Grid.SetColumn(bullet, 0);
        row.Children.Add(bullet);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private void Append(
        InlineCollection inlines,
        AnswerSegment segment,
        List<(int Start, int End, ChatCitationViewModel Source)> citations,
        ref int offset)
    {
        if (!segment.IsCitation)
        {
            var run = new Run(segment.Text);

            switch (segment.Kind)
            {
                case AnswerSegmentKind.Bold:
                    run.FontWeight = FontWeight.SemiBold;
                    break;

                case AnswerSegmentKind.Italic:
                    run.FontStyle = FontStyle.Italic;
                    break;

                case AnswerSegmentKind.Code:
                    run.FontFamily = new FontFamily("Consolas, Menlo, monospace");
                    run.FontSize = 12;
                    run.Foreground = ThemePalette.Brush("CodeBrush");
                    break;
            }

            inlines.Add(run);
            offset += segment.Text.Length;
            return;
        }

        var source = Source(segment.Citation);
        var marker = segment.Text;

        inlines.Add(new Run(marker)
        {
            Foreground = source is null
                ? ThemePalette.Brush("MutedBrush")
                : ThemePalette.Brush("AccentBrush"),
            TextDecorations = source is null ? null : TextDecorations.Underline,
        });

        if (source is not null)
            citations.Add((offset, offset + marker.Length, source));

        offset += marker.Length;
    }

    /// <summary>
    /// Finds the source a marker points at. Answers written before citations kept
    /// their number fall back to their position, so old transcripts still work.
    /// </summary>
    private ChatCitationViewModel? Source(int number)
    {
        var available = Citations;
        if (available is null || available.Count == 0)
            return null;

        var match = available.FirstOrDefault(citation => citation.Citation.Number == number);
        if (match is not null)
            return match;

        return number >= 1 && number <= available.Count ? available[number - 1] : null;
    }

    private static void Follow(
        PointerPressedEventArgs e,
        TextBlock block,
        IReadOnlyList<(int Start, int End, ChatCitationViewModel Source)> citations)
    {
        var layout = block.TextLayout;
        if (layout is null)
            return;

        var position = layout.HitTestPoint(e.GetPosition(block)).TextPosition;

        foreach (var (start, end, source) in citations)
        {
            if (position < start || position >= end)
                continue;

            if (source.OpenCommand.CanExecute(null))
                source.OpenCommand.Execute(null);

            e.Handled = true;
            return;
        }
    }

}
