using Kampf.Domain.ValueObjects;

namespace Kampf.Domain.Entities;

/// <summary>An explicit label on a note; also drives graph colouring/clusters.</summary>
public sealed class Tag
{
    public string Id { get; set; } = Ids.NewId();
    public string Name { get; set; } = string.Empty;

    public static Tag Create(string name) => new()
    {
        Name = Normalize(name),
    };

    public static string Normalize(string name) => name.Trim().TrimStart('#').ToLowerInvariant();
}
