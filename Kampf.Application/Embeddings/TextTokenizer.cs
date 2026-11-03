using System.Text;

namespace Kampf.Application.Embeddings;

/// <summary>
/// Minimal lexical tokenizer shared by the offline embedder and the suggestion
/// engine, so a proposed link can quote the words two notes actually share.
/// Lowercased, letters/digits only, stopwords removed.
/// </summary>
public static class TextTokenizer
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "are", "but", "not", "you", "your", "with", "this", "that",
        "from", "have", "has", "had", "was", "were", "will", "would", "can", "could",
        "should", "than", "then", "them", "they", "their", "there", "these", "those",
        "into", "about", "also", "any", "all", "one", "two", "our", "out", "use", "used",
        "using", "how", "why", "what", "when", "where", "which", "who", "whom", "its",
        "it's", "been", "being", "more", "most", "much", "some", "such", "only", "very",
        "just", "over", "under", "between", "each", "other", "same", "both", "may",
        "might", "must", "shall", "does", "did", "doing", "done", "here", "now", "new",
        "via", "per", "etc", "e.g", "i.e", "http", "https", "www", "com", "org", "page",
        "pages", "note", "notes", "see", "like", "well", "make", "made", "way", "even",
    };

    private const int MinTokenLength = 3;

    /// <summary>Splits free text (including markdown) into significant tokens.</summary>
    public static IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        var tokens = new List<string>();
        var current = new StringBuilder();

        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(char.ToLowerInvariant(c));
                continue;
            }

            Flush(current, tokens);
        }

        Flush(current, tokens);
        return tokens;
    }

    /// <summary>Distinct significant tokens, in first-seen order.</summary>
    public static IReadOnlyList<string> DistinctTokens(string? text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var token in Tokenize(text))
        {
            if (seen.Add(token))
                result.Add(token);
        }

        return result;
    }

    private static void Flush(StringBuilder current, List<string> tokens)
    {
        if (current.Length == 0)
            return;

        var token = current.ToString();
        current.Clear();

        if (token.Length < MinTokenLength || StopWords.Contains(token))
            return;

        tokens.Add(token);
    }
}
