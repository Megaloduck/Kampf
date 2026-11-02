namespace Kampf.Application.Models;

/// <summary>A full-text search hit that can point at either a note or a document.</summary>
public sealed record SearchHit(string Id, string Kind, string Title, string Snippet);
