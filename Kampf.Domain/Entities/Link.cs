using Kampf.Domain.ValueObjects;

namespace Kampf.Domain.Entities;

/// <summary>
/// A directed edge between two nodes. <see cref="Kind"/> records why the edge
/// exists so the graph stays auditable.
/// </summary>
public sealed class Link
{
    public string Id { get; set; } = Ids.NewId();
    public string SourceId { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public EdgeKind Kind { get; set; } = EdgeKind.Manual;
    public string? Rationale { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public static Link Create(string sourceId, string targetId, EdgeKind kind, string? rationale = null)
    {
        if (string.Equals(sourceId, targetId, StringComparison.Ordinal))
            throw new InvalidOperationException("A note cannot link to itself.");

        return new Link
        {
            SourceId = sourceId,
            TargetId = targetId,
            Kind = kind,
            Rationale = rationale,
        };
    }
}
