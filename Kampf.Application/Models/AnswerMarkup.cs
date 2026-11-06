using System.Text.RegularExpressions;

namespace Kampf.Application.Models;

/// <summary>How a line of an answer should be laid out.</summary>
public enum AnswerBlockKind
{
    Paragraph,
    Bullet,
    Heading,
}

/// <summary>What a piece of a line is: prose, emphasis, code, or a citation marker.</summary>
public enum AnswerSegmentKind
{
    Text,
    Bold,
    Italic,
    Code,
    Citation,
}

public sealed record AnswerSegment(
    string Text,
    AnswerSegmentKind Kind = AnswerSegmentKind.Text,
    int Citation = 0)
{
    public bool IsCitation => Kind == AnswerSegmentKind.Citation;
}

public sealed record AnswerBlock(AnswerBlockKind Kind, IReadOnlyList<AnswerSegment> Segments);

/// <summary>
/// Turns a model's markdown-ish answer into something a text control can render:
/// headings, bullets, bold/italic/code runs, and <c>[n]</c> markers kept as
/// separate segments so they can be clicked through to their source.
///
/// This is deliberately not a markdown parser. It understands the handful of
/// forms a language model actually emits in a 200-word research answer, and
/// leaves anything else as prose rather than guessing.
/// </summary>
public static class AnswerMarkup
{
    private static readonly Regex NumberedBullet =
        new(@"^(?<number>\d{1,2})[.)]\s+", RegexOptions.Compiled);

    private static readonly Regex Inline = new(
        @"\*\*(?<strong>.+?)\*\*" +
        @"|(?<![\w*])\*(?<em>[^*\s][^*]*?)\*(?![\w*])" +
        @"|(?<![\w])_(?<under>[^_\s][^_]*?)_(?![\w])" +
        @"|`(?<code>[^`]+?)`" +
        @"|\[(?<cite>\d{1,2})\]",
        RegexOptions.Compiled);

    public static IReadOnlyList<AnswerBlock> Parse(string? text)
    {
        var blocks = new List<AnswerBlock>();

        if (string.IsNullOrWhiteSpace(text))
            return blocks;

        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var content = raw.Trim();

            if (content.Length == 0)
                continue;

            // A rule carries no words, so it becomes a blank line instead.
            if (content is "---" or "***" or "___")
                continue;

            var kind = AnswerBlockKind.Paragraph;

            if (content[0] == '#')
            {
                kind = AnswerBlockKind.Heading;
                content = content.TrimStart('#').Trim();
            }
            else if (content.StartsWith("- ", StringComparison.Ordinal) ||
                     content.StartsWith("* ", StringComparison.Ordinal) ||
                     content.StartsWith("\u2022 ", StringComparison.Ordinal))
            {
                kind = AnswerBlockKind.Bullet;
                content = content[1..].TrimStart();
            }
            else
            {
                var numbered = NumberedBullet.Match(content);
                if (numbered.Success)
                {
                    kind = AnswerBlockKind.Bullet;
                    content = content[numbered.Length..].TrimStart();
                }
            }

            if (content.Length == 0)
                continue;

            var segments = Segments(content);
            if (segments.Count > 0)
                blocks.Add(new AnswerBlock(kind, segments));
        }

        return blocks;
    }

    private static List<AnswerSegment> Segments(string content)
    {
        var segments = new List<AnswerSegment>();
        var at = 0;

        foreach (Match match in Inline.Matches(content))
        {
            if (match.Index > at)
                segments.Add(new AnswerSegment(content[at..match.Index]));

            if (match.Groups["cite"].Success &&
                int.TryParse(match.Groups["cite"].Value, out var number))
            {
                segments.Add(new AnswerSegment(match.Value, AnswerSegmentKind.Citation, number));
            }
            else if (match.Groups["strong"].Success)
            {
                segments.Add(new AnswerSegment(match.Groups["strong"].Value, AnswerSegmentKind.Bold));
            }
            else if (match.Groups["em"].Success || match.Groups["under"].Success)
            {
                var value = match.Groups["em"].Success ? match.Groups["em"].Value : match.Groups["under"].Value;
                segments.Add(new AnswerSegment(value, AnswerSegmentKind.Italic));
            }
            else if (match.Groups["code"].Success)
            {
                segments.Add(new AnswerSegment(match.Groups["code"].Value, AnswerSegmentKind.Code));
            }
            else
            {
                segments.Add(new AnswerSegment(match.Value));
            }

            at = match.Index + match.Length;
        }

        if (at < content.Length)
            segments.Add(new AnswerSegment(content[at..]));

        // Leading and trailing whitespace is layout, not content.
        if (segments.Count > 0)
            segments[0] = segments[0] with { Text = segments[0].Text.TrimStart() };

        if (segments.Count > 0)
            segments[^1] = segments[^1] with { Text = segments[^1].Text.TrimEnd() };

        return segments.Where(s => s.IsCitation || s.Text.Length > 0).ToList();
    }
}
