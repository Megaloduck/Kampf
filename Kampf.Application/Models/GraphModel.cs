using Kampf.Domain.ValueObjects;

namespace Kampf.Application.Models;

/// <summary>A graph vertex ready for the view (no storage concerns).</summary>
public sealed record GraphNode(
    string Id,
    NodeKind Kind,
    string Label,
    int Degree,
    string? Tag,
    int? Year,
    string? Author = null);

public sealed record GraphEdge(string SourceId, string TargetId, EdgeKind Kind);

public sealed class GraphModel
{
    public IReadOnlyList<GraphNode> Nodes { get; init; } = Array.Empty<GraphNode>();
    public IReadOnlyList<GraphEdge> Edges { get; init; } = Array.Empty<GraphEdge>();

    public bool IsEmpty => Nodes.Count == 0;
}
