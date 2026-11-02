using Kampf.Domain.ValueObjects;

namespace Kampf.Domain.Entities;

/// <summary>
/// Append-only trail of accepted/rejected suggestions and structural changes,
/// so every edge can be explained after the fact.
/// </summary>
public sealed class AuditEntry
{
    public string Id { get; set; } = Ids.NewId();
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    public string Action { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;

    public static AuditEntry Create(string action, string detail) => new()
    {
        Action = action,
        Detail = detail,
    };
}
