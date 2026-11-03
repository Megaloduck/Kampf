namespace Kampf.Application.Models;

/// <summary>
/// A cached note vector. The content hash lets the suggestion engine skip
/// re-embedding notes that have not changed since the last run.
/// </summary>
public sealed record NoteEmbedding(string NoteId, string Model, string ContentHash, float[] Vector);
