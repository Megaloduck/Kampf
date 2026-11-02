using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Kampf.Controls;

/// <summary>
/// Small markdown-to-controls renderer for the preview pane. It deliberately
/// supports the subset the vault uses (headings, lists, quotes, code, emphasis
/// and [[wikilinks]]); the markdown text stays the source of truth.
/// </summary>
public static class MarkdownPreview
{
    private static IBrush TextBrush => ThemePalette.Brush("TextBrush");
    private static IBrush MutedBrush => ThemePalette.Brush("MutedBrush");
    private static IBrush AccentBrush => ThemePalette.Brush("AccentBrush");
    private static IBrush CodeBrush => ThemePalette.Brush("CodeBrush");

    public static Control Build(string? markdown, Action<string>? onWikiLink)
    {
        var stack = new StackPanel { Spacing = 6 };

        if (string.IsNullOrWhiteSpace(markdown))
        {
            stack.Children.Add(new TextBlock
            {
                Text = "Empty note. Markdown is supported, and [[wikilinks]] create connections.",
                Foreground = MutedBrush,
                FontStyle = FontStyle.Italic,
                TextWrapping = TextWrapping.Wrap,
            });
            return stack;
        }

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var index = 0;

        while (index < lines.Length)
        {
            var line = lines[index];
            var trimmed = line.TrimStart();

            if (trimmed.Length == 0)
            {
                index++;
                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                index++;
                var code = new List<string>();
                while (index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                    code.Add(lines[index++]);
                index++; // closing fence

                stack.Children.Add(new Border
                {
                    Background = ThemePalette.Brush("CodeBackgroundBrush"),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 8),
                    Child = new TextBlock
                    {
                        Text = string.Join('\n', code),
                        FontFamily = new FontFamily("Consolas, Menlo, monospace"),
                        FontSize = 12.5,
                        Foreground = CodeBrush,
                        TextWrapping = TextWrapping.NoWrap,
                    },
                });
                continue;
            }

            if (trimmed is "---" or "***" or "___")
            {
                stack.Children.Add(new Border
                {
                    Height = 1,
                    Background = ThemePalette.Brush("RuleBrush"),
                    Margin = new Thickness(0, 6),
                });
                index++;
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                var level = 0;
                while (level < trimmed.Length && trimmed[level] == '#')
                    level++;

                var heading = trimmed[level..].Trim();
                stack.Children.Add(BuildBlock(heading, level switch
                {
                    1 => 22,
                    2 => 18,
                    3 => 15.5,
                    _ => 14,
                }, FontWeight.SemiBold, onWikiLink, 16, 4));
                index++;
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                var quote = new List<string>();
                while (index < lines.Length && lines[index].TrimStart().StartsWith('>'))
                {
                    quote.Add(lines[index].TrimStart().TrimStart('>').TrimStart());
                    index++;
                }

                stack.Children.Add(new Border
                {
                    BorderBrush = ThemePalette.Brush("QuoteBorderBrush"),
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    Padding = new Thickness(10, 2, 0, 2),
                    Child = BuildBlock(string.Join(' ', quote), 13.5, FontWeight.Normal, onWikiLink, 0, 0, MutedBrush),
                });
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                while (index < lines.Length)
                {
                    var item = lines[index].TrimStart();
                    if (!item.StartsWith("- ", StringComparison.Ordinal) && !item.StartsWith("* ", StringComparison.Ordinal))
                        break;

                    var row = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("16,*"),
                        Margin = new Thickness(4, 1, 0, 1),
                    };

                    var bullet = new TextBlock { Text = "\u2022", Foreground = MutedBrush, FontSize = 13 };
                    Grid.SetColumn(bullet, 0);
                    row.Children.Add(bullet);

                    var content = BuildBlock(item[2..], 13.5, FontWeight.Normal, onWikiLink, 0, 0);
                    Grid.SetColumn(content, 1);
                    row.Children.Add(content);

                    stack.Children.Add(row);
                    index++;
                }

                continue;
            }

            // Paragraph: gather until a blank line or a new block marker.
            var paragraph = new List<string> { line };
            index++;
            while (index < lines.Length)
            {
                var next = lines[index].TrimStart();
                if (next.Length == 0 || next.StartsWith('#') || next.StartsWith("```") ||
                    next.StartsWith('>') || next.StartsWith("- ", StringComparison.Ordinal) ||
                    next.StartsWith("* ", StringComparison.Ordinal))
                    break;

                paragraph.Add(lines[index]);
                index++;
            }

            stack.Children.Add(BuildBlock(
                string.Join(' ', paragraph.Select(p => p.Trim())),
                13.5,
                FontWeight.Normal,
                onWikiLink,
                0,
                0));
        }

        return stack;
    }

    private static TextBlock BuildBlock(
        string text,
        double fontSize,
        FontWeight weight,
        Action<string>? onWikiLink,
        double topMargin,
        double bottomMargin,
        IBrush? brush = null)
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = fontSize,
            FontWeight = weight,
            Foreground = brush ?? TextBrush,
            Margin = new Thickness(0, topMargin, 0, bottomMargin),
            LineHeight = fontSize * 1.5,
        };

        var links = new List<(int Start, int End, string Target)>();
        var offset = 0;
        AppendInlines(block.Inlines!, text, links, ref offset);

        if (links.Count > 0 && onWikiLink is not null)
        {
            block.Cursor = new Cursor(StandardCursorType.Hand);
            block.PointerPressed += (_, e) =>
            {
                var layout = block.TextLayout;
                if (layout is null)
                    return;

                var position = layout.HitTestPoint(e.GetPosition(block)).TextPosition;

                foreach (var link in links)
                {
                    if (position < link.Start || position >= link.End)
                        continue;

                    onWikiLink(link.Target);
                    e.Handled = true;
                    break;
                }
            };
        }

        return block;
    }

    private static void AppendInlines(
        InlineCollection inlines,
        string text,
        List<(int Start, int End, string Target)> links,
        ref int offset)
    {
        var index = 0;

        while (index < text.Length)
        {
            var remaining = text[index..];

            if (remaining.StartsWith("[[", StringComparison.Ordinal))
            {
                var close = remaining.IndexOf("]]", StringComparison.Ordinal);
                if (close > 2)
                {
                    var inner = remaining[2..close];
                    var parts = inner.Split('|', 2);
                    var target = parts[0].Trim();
                    var label = parts.Length > 1 ? parts[1] : target;

                    inlines.Add(new Run(label)
                    {
                        Foreground = AccentBrush,
                        TextDecorations = TextDecorations.Underline,
                    });

                    links.Add((offset, offset + label.Length, target));
                    offset += label.Length;
                    index += close + 2;
                    continue;
                }
            }

            if (remaining.StartsWith("**", StringComparison.Ordinal) || remaining.StartsWith("__", StringComparison.Ordinal))
            {
                var marker = remaining[..2];
                var close = remaining.IndexOf(marker, 2, StringComparison.Ordinal);
                if (close > 2)
                {
                    var inner = remaining[2..close];
                    inlines.Add(new Run(inner) { FontWeight = FontWeight.Bold });
                    offset += inner.Length;
                    index += close + 2;
                    continue;
                }
            }

            if (remaining.StartsWith('`'))
            {
                var close = remaining.IndexOf('`', 1);
                if (close > 1)
                {
                    var inner = remaining[1..close];
                    inlines.Add(new Run(inner)
                    {
                        FontFamily = new FontFamily("Consolas, Menlo, monospace"),
                        Foreground = CodeBrush,
                        FontSize = 12.5,
                    });
                    offset += inner.Length;
                    index += close + 1;
                    continue;
                }
            }

            if (remaining.StartsWith('*') || remaining.StartsWith('_'))
            {
                var marker = remaining[0];
                var close = remaining.IndexOf(marker, 1);
                if (close > 1)
                {
                    var inner = remaining[1..close];
                    inlines.Add(new Run(inner) { FontStyle = FontStyle.Italic });
                    offset += inner.Length;
                    index += close + 1;
                    continue;
                }
            }

            // Plain run up to the next potential marker.
            var nextMarker = remaining.IndexOfAny(new[] { '[', '*', '_', '`' }, 1);
            if (nextMarker < 0)
                nextMarker = remaining.Length;

            var plain = remaining[..nextMarker];
            inlines.Add(new Run(plain));
            offset += plain.Length;
            index += nextMarker;
        }
    }
}
