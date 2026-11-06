using System.Text.RegularExpressions;

namespace Kampf.Application.Services;

/// <summary>
/// Pulls the two subjects out of a linking question — "what connects modularity
/// and homophily?" — so retrieval can look for each of them on its own. Sent as
/// one blended query, whichever half ranks higher answers the question alone.
/// </summary>
public static class ChatSubjects
{
    private const int MaxSubjectLength = 80;

    private static readonly Regex Quoted = new(
        @"[\u201c""']([^\u201d""']{3,80})[\u201d""']",
        RegexOptions.Compiled);

    private static readonly Regex LeadingArticle = new(
        @"^(?:the|a|an|my|our|this|that|these|those)\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TrailingPadding = new(
        @"\s+(?:in\s+my\s+(?:notes|vault)|in\s+the\s+vault|for\s+me|together|really)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    private static readonly Regex[] Pairs =
    {
        new(@"\bconnect(?:s|ed|ing)?\s+(?<a>.+?)\s+(?:and|with|to)\s+(?<b>.+?)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),

        new(@"\b(?:connect|link|links|relation|relations|relationship|relationships|connection|connections|difference|differences)s?\s+between\s+(?<a>.+?)\s+and\s+(?<b>.+?)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),

        new(@"\bhow\s+(?:does|do|is|are|did)\s+(?<a>.+?)\s+(?:relate|relates|related|compare|compares|compared|differ|differs|differed)\s+(?:to|from|with)\s+(?<b>.+?)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),

        new(@"^(?<a>.+?)\s+(?:vs\.?|versus)\s+(?<b>.+?)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    /// <summary>
    /// The subjects of a linking question, in the order they were asked. Empty
    /// when the question has no two-sided form, in which case one search is right.
    /// </summary>
    public static IReadOnlyList<string> Extract(string? question)
    {
        if (string.IsNullOrWhiteSpace(question))
            return Array.Empty<string>();

        var text = question.Trim();

        // "“modularity” and “homophily”" — the quotes are the researcher's own
        // way of saying these are the two things, so trust them over the wording.
        var quoted = Quoted.Matches(text)
            .Select(match => Clean(match.Groups[1].Value))
            .Where(Usable)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (quoted.Count >= 2)
            return quoted.Take(2).ToList();

        foreach (var pattern in Pairs)
        {
            var match = pattern.Match(text);
            if (!match.Success)
                continue;

            var first = Clean(match.Groups["a"].Value);
            var second = Clean(match.Groups["b"].Value);

            if (Usable(first) && Usable(second) && !first.Equals(second, StringComparison.OrdinalIgnoreCase))
                return new[] { first, second };
        }

        return Array.Empty<string>();
    }

    private static string Clean(string value)
    {
        var text = Spaces.Replace(value, " ").Trim();
        text = text.Trim(' ', '"', '\u201c', '\u201d', '\'', '?', '.', '!', ',', ';', ':');
        text = LeadingArticle.Replace(text, string.Empty);
        text = TrailingPadding.Replace(text, string.Empty);
        return text.Trim(' ', '?', '.', '!', ',', ';', ':');
    }

    private static bool Usable(string text) =>
        text.Length >= 3 &&
        text.Length <= MaxSubjectLength &&
        text.Any(char.IsLetter);
}
