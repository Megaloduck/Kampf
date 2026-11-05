using System.Text;

namespace Kampf.Application.Services;

/// <summary>
/// Renders the markdown the vault uses to HTML for a static, published copy of
/// the notes. It supports the same subset the in-app preview does — headings,
/// lists, quotes, fenced code, emphasis, inline code and <c>[[wikilinks]]</c> —
/// and escapes everything else, so publishing a note can never inject markup.
/// </summary>
public static class MarkdownHtml
{
    /// <param name="resolveLink">
    /// Turns a wikilink target into a URL, or null when the target is not part
    /// of the published set (the link is then shown as plain text).
    /// </param>
    public static string ToHtml(string? markdown, Func<string, string?>? resolveLink = null)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        var builder = new StringBuilder();
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
                index++;

                builder.Append("<pre><code>")
                    .Append(Escape(string.Join('\n', code)))
                    .AppendLine("</code></pre>");
                continue;
            }

            if (trimmed is "---" or "***" or "___")
            {
                builder.AppendLine("<hr/>");
                index++;
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                var level = 0;
                while (level < trimmed.Length && trimmed[level] == '#')
                    level++;

                var text = trimmed[level..].Trim();
                builder.Append($"<h{level}>").Append(Inline(text, resolveLink)).AppendLine($"</h{level}>");
                index++;
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                var quote = new List<string>();
                while (index < lines.Length && lines[index].TrimStart().StartsWith('>'))
                    quote.Add(lines[index++].TrimStart().TrimStart('>').Trim());

                builder.Append("<blockquote><p>")
                    .Append(Inline(string.Join(' ', quote), resolveLink))
                    .AppendLine("</p></blockquote>");
                continue;
            }

            if (IsListLine(trimmed))
            {
                var ordered = char.IsDigit(trimmed[0]);
                builder.AppendLine(ordered ? "<ol>" : "<ul>");

                while (index < lines.Length && IsListLine(lines[index].TrimStart()))
                {
                    var item = lines[index].TrimStart();
                    var content = ordered
                        ? item[(item.IndexOf('.') + 1)..].Trim()
                        : item[1..].Trim();

                    builder.Append("<li>").Append(Inline(content, resolveLink)).AppendLine("</li>");
                    index++;
                }

                builder.AppendLine(ordered ? "</ol>" : "</ul>");
                continue;
            }

            var paragraph = new List<string> { line.Trim() };
            index++;

            while (index < lines.Length)
            {
                var next = lines[index].TrimStart();
                if (next.Length == 0 || next.StartsWith('#') || next.StartsWith("```") ||
                    next.StartsWith('>') || IsListLine(next) || next is "---" or "***" or "___")
                    break;

                paragraph.Add(lines[index].Trim());
                index++;
            }

            builder.Append("<p>").Append(Inline(string.Join(' ', paragraph), resolveLink)).AppendLine("</p>");
        }

        return builder.ToString();
    }

    /// <summary>Escapes text and turns the markdown the vault writes into inline HTML.</summary>
    public static string Inline(string text, Func<string, string?>? resolveLink = null)
    {
        var builder = new StringBuilder();
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
                    var href = resolveLink?.Invoke(target);

                    builder.Append(href is null
                        ? $"<span class=\"unresolved\">{Escape(label)}</span>"
                        : $"<a class=\"wiki\" href=\"{Escape(href)}\">{Escape(label)}</a>");

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
                    builder.Append("<strong>").Append(Escape(remaining[2..close])).Append("</strong>");
                    index += close + 2;
                    continue;
                }
            }

            if (remaining.StartsWith('`'))
            {
                var close = remaining.IndexOf('`', 1);
                if (close > 1)
                {
                    builder.Append("<code>").Append(Escape(remaining[1..close])).Append("</code>");
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
                    builder.Append("<em>").Append(Escape(remaining[1..close])).Append("</em>");
                    index += close + 1;
                    continue;
                }
            }

            var next = remaining.IndexOfAny(new[] { '[', '*', '_', '`' }, 1);
            if (next < 0)
                next = remaining.Length;

            builder.Append(Escape(remaining[..next]));
            index += next;
        }

        return builder.ToString();
    }

    public static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

    private static bool IsListLine(string line) =>
        line.StartsWith("- ", StringComparison.Ordinal) ||
        line.StartsWith("* ", StringComparison.Ordinal) ||
        (line.Length > 2 && char.IsDigit(line[0]) && line.IndexOf(". ", StringComparison.Ordinal) is > 0 and < 4);
}
