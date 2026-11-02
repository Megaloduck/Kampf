using System.Text;

namespace Kampf.Application.Markdown;

/// <summary>A parsed [[Target|alias]] reference.</summary>
public sealed record WikiLink(string Target, string? Alias, int Start, int Length);

/// <summary>
/// Extracts [[wikilinks]] from markdown, deliberately ignoring anything inside
/// fenced or inline code so quotes of syntax are not treated as edges.
/// Pure and dependency-free so it can be unit tested directly.
/// </summary>
public static class WikiLinkParser
{
    public static IReadOnlyList<WikiLink> Parse(string? markdown)
    {
        var result = new List<WikiLink>();
        if (string.IsNullOrEmpty(markdown))
            return result;

        var text = StripCode(markdown);
        var index = 0;
        while (index < text.Length - 3)
        {
            var open = text.IndexOf("[[", index, StringComparison.Ordinal);
            if (open < 0)
                break;

            var close = text.IndexOf("]]", open + 2, StringComparison.Ordinal);
            if (close < 0)
                break;

            var inner = text.Substring(open + 2, close - open - 2);
            // No nested brackets, no newlines: a wiki link is a single token-ish phrase.
            if (inner.Length > 0 && !inner.Contains('[') && !inner.Contains('\n'))
            {
                var parts = inner.Split('|', 2, StringSplitOptions.TrimEntries);
                var target = parts[0].Trim();
                if (target.Length > 0)
                {
                    var alias = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
                    result.Add(new WikiLink(target, alias, open, close + 2 - open));
                }
            }

            index = close + 2;
        }

        return result;
    }

    public static IReadOnlyList<string> ParseTargets(string? markdown) =>
        Parse(markdown)
            .Select(l => l.Target)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Replaces code spans/fences with equal-length spaces, preserving offsets.</summary>
    private static string StripCode(string text)
    {
        var buffer = new StringBuilder(text);
        var inFence = false;
        var lineStart = 0;

        for (var i = 0; i <= buffer.Length; i++)
        {
            var atEnd = i == buffer.Length;
            if (!atEnd && buffer[i] != '\n')
                continue;

            var lineEnd = i;
            var line = buffer.ToString(lineStart, lineEnd - lineStart);
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                Blank(buffer, lineStart, lineEnd);
                inFence = !inFence;
            }
            else if (inFence)
            {
                Blank(buffer, lineStart, lineEnd);
            }
            else
            {
                BlankInlineCode(buffer, lineStart, lineEnd);
            }

            lineStart = i + 1;
        }

        return buffer.ToString();
    }

    private static void Blank(StringBuilder buffer, int start, int end)
    {
        for (var i = start; i < end; i++)
            buffer[i] = ' ';
    }

    private static void BlankInlineCode(StringBuilder buffer, int start, int end)
    {
        var i = start;
        while (i < end)
        {
            if (buffer[i] != '`')
            {
                i++;
                continue;
            }

            var runStart = i;
            while (i < end && buffer[i] == '`')
                i++;
            var runLength = i - runStart;

            // find a closing run of the same length on this line
            var j = i;
            var closed = -1;
            while (j < end)
            {
                if (buffer[j] != '`')
                {
                    j++;
                    continue;
                }

                var closeStart = j;
                while (j < end && buffer[j] == '`')
                    j++;
                if (j - closeStart == runLength)
                {
                    closed = closeStart;
                    break;
                }
            }

            if (closed < 0)
                continue; // unbalanced backtick: leave it alone

            Blank(buffer, runStart, j);
        }
    }
}
