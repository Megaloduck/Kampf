namespace Kampf.Domain.ValueObjects;

/// <summary>Stable opaque identifiers. Stored as TEXT in SQLite.</summary>
public static class Ids
{
    public static string NewId() => Guid.NewGuid().ToString("n");
}

/// <summary>What kind of node a graph vertex represents.</summary>
public enum NodeKind
{
    Note = 0,
    Document = 1,
}

/// <summary>
/// Every edge in the graph must be traceable to a user action or an explicitly
/// accepted suggestion, so the kind is recorded on each link.
/// </summary>
public enum EdgeKind
{
    /// <summary>Link created by the user directly.</summary>
    Manual = 0,

    /// <summary>Link declared with [[wikilink]] syntax in a note body.</summary>
    Wiki = 1,

    /// <summary>Link created by accepting an AI/assist suggestion.</summary>
    AcceptedSuggestion = 2,
}

public enum SuggestionKind
{
    /// <summary>A [[wikilink]] points at a note that does not exist yet.</summary>
    CreateMissingNote = 0,

    /// <summary>Two notes look related (Phase 2: AI generated).</summary>
    LinkNotes = 1,

    /// <summary>A tag should be applied to a note (Phase 2: AI generated).</summary>
    ApplyTag = 2,

    /// <summary>
    /// Two notes in separate clusters of the graph would connect, or an isolated
    /// note would join the web (Phase 3: connect the dots / orphan recovery).
    /// </summary>
    ConnectDots = 3,
}

public enum SuggestionStatus
{
    Pending = 0,
    Accepted = 1,
    Rejected = 2,
}

/// <summary>A page anchor keeps provenance: document + 1-based page.</summary>
public readonly record struct PageAnchor(string DocumentId, int Page)
{
    public override string ToString() => $"page {Page}";
}
