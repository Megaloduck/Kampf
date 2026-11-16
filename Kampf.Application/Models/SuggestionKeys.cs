using Kampf.Domain.Entities;

namespace Kampf.Application.Models;

/// <summary>
/// Canonical keys for settling suggestions, deliberately independent of how a
/// payload happens to be serialized: a link is the <em>unordered</em> pair of
/// note ids (reason text and direction are presentation, not identity), and a
/// tag is a note plus its normalized name.
/// </summary>
public static class SuggestionKeys
{
    public static (string, string) Pair(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

    public static (string, string) TagKey(string noteId, string tag) =>
        (noteId, Tag.Normalize(tag));
}
