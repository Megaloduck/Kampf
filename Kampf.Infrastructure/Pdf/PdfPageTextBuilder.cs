using System.Text;
using UglyToad.PdfPig.Content;

namespace Kampf.Infrastructure.Pdf;

/// <summary>One word placed on the page, in points from the page's top-left.</summary>
internal readonly record struct PlacedWord(
    string Text,
    int Start,
    double Left,
    double Top,
    double Width,
    double Height);

/// <summary>A page's text and the words that compose it, in reading order.</summary>
internal sealed record PageTextLayout(string Text, IReadOnlyList<PlacedWord> Words)
{
    public static readonly PageTextLayout Empty = new(string.Empty, Array.Empty<PlacedWord>());
}

/// <summary>
/// Turns PdfPig's words into the text the vault stores, and the boxes the reader
/// draws. Both come from here on purpose: the highlight for a stored quote is a
/// character range, so the stored text and the boxes must be built by the same
/// pass or the marks land on the wrong words.
///
/// PdfPig's own <c>Page.Text</c> concatenates content-stream operations, which on
/// a two-column page interleaves the columns and drops the line breaks entirely
/// ("...modularity.Right column line one..."). This assembler groups words into
/// lines by their baseline, splits a two-column body at its gutter, reads
/// top-to-bottom then left-to-right, joins hyphenated line breaks, and folds
/// ligatures so the text is searchable.
/// </summary>
internal static class PdfPageTextBuilder
{
    /// <summary>Words wider than this share of the page can span columns (titles, rules).</summary>
    private const double SpanningWordRatio = 0.35;

    /// <summary>Each column must hold at least this share of the words to count as one.</summary>
    private const double MinimumColumnShare = 0.2;

    /// <summary>A gutter narrower than this share of the page is just a word gap.</summary>
    private const double MinimumGutterRatio = 0.02;

    public static PageTextLayout Build(Page page)
    {
        var width = (double)page.Width;
        var height = (double)page.Height;

        if (width <= 0 || height <= 0)
            return new PageTextLayout(NormalizeSpacing(page.Text ?? string.Empty), Array.Empty<PlacedWord>());

        List<Placed> words;

        try
        {
            words = page.GetWords()
                .Select(word => new Placed(
                    Clean(word.Text ?? string.Empty),
                    (double)word.BoundingBox.Left,
                    height - (double)word.BoundingBox.Top,
                    (double)word.BoundingBox.Width,
                    (double)word.BoundingBox.Height))
                .Where(word => word.Text.Length > 0 && word.Width > 0)
                .ToList();
        }
        catch (Exception)
        {
            // A page PdfPig cannot read word-by-word still has its plain text.
            return new PageTextLayout(NormalizeSpacing(page.Text ?? string.Empty), Array.Empty<PlacedWord>());
        }

        if (words.Count == 0)
            return new PageTextLayout(NormalizeSpacing(page.Text ?? string.Empty), Array.Empty<PlacedWord>());

        var lines = InReadingOrder(words, width);
        var text = new StringBuilder();
        var placed = new List<PlacedWord>(words.Count);

        for (var l = 0; l < lines.Count; l++)
        {
            var line = lines[l];

            if (l > 0)
                text.Append('\n');

            for (var w = 0; w < line.Count; w++)
            {
                var word = line[w];

                if (w > 0 && !word.Joined)
                    text.Append(' ');

                placed.Add(new PlacedWord(word.Text, text.Length, word.Left, word.Top, word.Width, word.Height));
                text.Append(word.Text);
            }
        }

        return new PageTextLayout(text.ToString(), placed);
    }

    // ------------------------------------------------------------- ordering

    private static List<List<Placed>> InReadingOrder(List<Placed> words, double pageWidth)
    {
        if (Gutter(words, pageWidth) is not { } gutter)
            return GroupByLine(words);

        var left = words.Where(word => Center(word) < gutter).ToList();
        var right = words.Where(word => Center(word) >= gutter).ToList();

        if (left.Count < words.Count * MinimumColumnShare ||
            right.Count < words.Count * MinimumColumnShare)
            return GroupByLine(words);

        var ordered = GroupByLine(left);
        ordered.AddRange(GroupByLine(right));
        return ordered;
    }

    private static double Center(Placed word) => word.Left + word.Width / 2;

    /// <summary>
    /// The x position of the gap between two columns, or null for a one-column
    /// page. Only words narrow enough to belong to a column are considered, so a
    /// full-width title does not bridge the gutter and hide it.
    /// </summary>
    private static double? Gutter(List<Placed> words, double pageWidth)
    {
        var narrow = words
            .Where(word => word.Width <= pageWidth * SpanningWordRatio)
            .OrderBy(word => word.Left)
            .ToList();

        if (narrow.Count < 8)
            return null;

        var spans = new List<(double Start, double End)>();

        foreach (var word in narrow)
        {
            var start = word.Left;
            var end = word.Left + word.Width;

            if (spans.Count > 0 && start <= spans[^1].End)
            {
                spans[^1] = (spans[^1].Start, Math.Max(spans[^1].End, end));
                continue;
            }

            spans.Add((start, end));
        }

        double? best = null;
        var bestWidth = 0.0;

        for (var i = 0; i + 1 < spans.Count; i++)
        {
            var gapStart = spans[i].End;
            var gapEnd = spans[i + 1].Start;
            var gap = gapEnd - gapStart;
            var center = (gapStart + gapEnd) / 2;

            // A handle at the far edges of the page is a margin, not a gutter.
            if (center < pageWidth * 0.15 || center > pageWidth * 0.85)
                continue;

            if (gap < pageWidth * MinimumGutterRatio || gap <= bestWidth)
                continue;

            bestWidth = gap;
            best = center;
        }

        return best;
    }

    /// <summary>
    /// Groups words into lines by baseline, top line first and left to right
    /// within a line. Hyphenated breaks are joined: "exam-" + "ple" reads
    /// "example", which is also the word a search looks for.
    /// </summary>
    private static List<List<Placed>> GroupByLine(List<Placed> words)
    {
        var lines = new List<List<Placed>>();

        var ordered = words
            .OrderBy(word => word.Top)
            .ThenBy(word => word.Left)
            .ToList();

        var tolerance = LineTolerance(words);

        foreach (var word in ordered)
        {
            if (lines.Count > 0 && Math.Abs(lines[^1][0].Top - word.Top) <= tolerance)
            {
                lines[^1].Add(word);
                continue;
            }

            lines.Add(new List<Placed> { word });
        }

        foreach (var line in lines)
            line.Sort((a, b) => a.Left.CompareTo(b.Left));

        return JoinHyphenatedBreaks(lines);
    }

    private static double LineTolerance(List<Placed> words)
    {
        var heights = words.Select(word => word.Height).Where(h => h > 0).OrderBy(h => h).ToList();

        if (heights.Count == 0)
            return 1.5;

        var median = heights[heights.Count / 2];
        return Math.Max(1.5, median * 0.5);
    }

    /// <summary>Folds a line ending in a hyphen into the next one when a word was split.</summary>
    private static List<List<Placed>> JoinHyphenatedBreaks(List<List<Placed>> lines)
    {
        var joined = new List<List<Placed>>();

        foreach (var line in lines)
        {
            if (joined.Count > 0 && !EndsSentence(joined[^1]) && TryUnhyphenate(joined[^1][^1], line[0]))
            {
                var previous = joined[^1];
                previous[^1] = previous[^1] with { Text = previous[^1].Text[..^1] };

                // The rest of the split word continues the one before it, so no
                // space goes between them: "exam-" + "ple" reads "example".
                line[0] = line[0] with { Joined = true };
                previous.AddRange(line);
                continue;
            }

            joined.Add(line);
        }

        return joined;
    }

    private static bool EndsSentence(List<Placed> line) =>
        line.Count > 0 && line[^1].Text is { Length: > 0 } text && text[^1] is '.' or ':' or ';' or '!' or '?';

    /// <summary>
    /// True when <paramref name="first"/> of the next line is the rest of the
    /// word that <paramref name="last"/> started: a trailing hyphen with letters
    /// on both sides of the break.
    /// </summary>
    private static bool TryUnhyphenate(Placed last, Placed first)
    {
        var text = last.Text;

        if (text.Length < 2 || text[^1] != '-' || !char.IsLetter(text[^2]))
            return false;

        return first.Text.Length > 0 && char.IsLower(first.Text[0]);
    }

    // ----------------------------------------------------------- normalising

    /// <summary>
    /// One word, with the ligatures and soft hyphens that make search and quotes
    /// miss the obvious spelling folded away.
    /// </summary>
    private static string Clean(string word)
    {
        if (word.Length == 0)
            return word;

        var builder = new StringBuilder(word.Length);

        foreach (var character in word)
        {
            switch (character)
            {
                case '\uFB00': builder.Append("ff"); break;
                case '\uFB01': builder.Append("fi"); break;
                case '\uFB02': builder.Append("fl"); break;
                case '\uFB03': builder.Append("ffi"); break;
                case '\uFB04': builder.Append("ffl"); break;
                case '\uFB05':
                case '\uFB06': builder.Append("st"); break;
                case '\u00AD':
                case '\u200B':
                case '\uFEFF': break;
                case '\u00A0': builder.Append(' '); break;
                default: builder.Append(character); break;
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>The fallback for pages without usable word boxes.</summary>
    private static string NormalizeSpacing(string text)
    {
        var lines = text
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => Clean(line).Trim())
            .Where(line => line.Length > 0);

        return string.Join('\n', lines);
    }

    private readonly record struct Placed(
        string Text,
        double Left,
        double Top,
        double Width,
        double Height,
        bool Joined = false);
}
