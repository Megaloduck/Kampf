using System.Collections.Generic;

namespace Kampf.Application.Services;

/// <summary>
/// Locates a quoted passage inside a page's stored text, so a highlight or a
/// citation can be drawn exactly where it was quoted from. Stored quotes keep
/// their line breaks while chat excerpts are flattened and may end in an
/// ellipsis, so the match tries the quote as-is, flattened, and then shorter and
/// shorter prefixes of it.
/// </summary>
public static class PageTextSearch
{
    private const int MinimumPrefix = 16;

    /// <summary>Start and length of <paramref name="quote"/> in <paramref name="pageText"/>, if found.</summary>
    public static (int Start, int Length)? Find(string? pageText, string? quote)
    {
        if (string.IsNullOrWhiteSpace(pageText) || string.IsNullOrWhiteSpace(quote))
            return null;

        var raw = string.Join('\n', quote
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0));

        if (raw.Length == 0)
            return null;

        // Same text, with line breaks flattened to single spaces: offsets still
        // line up with the original because it is a one-for-one replacement.
        var flat = pageText.Replace('\n', ' ');
        var flatQuote = Collapse(raw);

        var hit = Locate(pageText, raw) ?? Locate(flat, flatQuote) ?? LocatePrefix(flat, flatQuote);
        return hit;
    }

    /// <summary>
    /// Every occurrence of a query on a page, case-insensitively. Used by the
    /// reader's in-page search, which marks all of them at once.
    /// </summary>
    public static IReadOnlyList<(int Start, int Length)> FindAll(
        string? pageText,
        string? query,
        int limit = 200)
    {
        if (string.IsNullOrWhiteSpace(pageText) || string.IsNullOrWhiteSpace(query))
            return Array.Empty<(int, int)>();

        var needle = query.Trim();
        if (needle.Length < 2)
            return Array.Empty<(int, int)>();

        var found = new List<(int Start, int Length)>();
        var index = 0;

        while (found.Count < limit)
        {
            index = pageText.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                break;

            // Mark whole words where the query starts mid-word, so the drawn
            // box covers the text the reader searched for.
            var start = index;
            while (start > 0 && !char.IsWhiteSpace(pageText[start - 1]) && !char.IsPunctuation(pageText[start - 1]))
                start--;

            var end = index + needle.Length;
            while (end < pageText.Length && !char.IsWhiteSpace(pageText[end]) && !char.IsPunctuation(pageText[end]))
                end++;

            found.Add((start, end - start));
            index += needle.Length;
        }

        return found;
    }

    private static (int Start, int Length)? Locate(string text, string needle)
    {
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        return index < 0 ? null : (index, needle.Length);
    }

    /// <summary>
    /// Chat excerpts are cut at a length and end in "…", so the tail never
    /// matches. Try the whole thing, then successively shorter prefixes.
    /// </summary>
    private static (int Start, int Length)? LocatePrefix(string text, string needle)
    {
        var trimmed = needle.TrimEnd('.', '\u2026', ' ');

        while (trimmed.Length >= MinimumPrefix)
        {
            var index = text.IndexOf(trimmed, StringComparison.Ordinal);
            if (index >= 0)
                return (index, trimmed.Length);

            var shorter = trimmed[..(trimmed.Length * 3 / 4)];
            if (shorter.Length >= trimmed.Length)
                break;

            trimmed = shorter;
        }

        return null;
    }

    private static string Collapse(string quote) =>
        string.Join(' ', quote.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
}
