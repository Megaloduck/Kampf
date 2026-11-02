using System.Text.Json;

namespace Kampf.Application.Models;

/// <summary>Payload of a "this [[link]] has no note yet" suggestion.</summary>
public sealed record CreateMissingNotePayload(string TargetTitle, string SourceNoteId);

/// <summary>Payload of a "these two notes look related" suggestion.</summary>
public sealed record LinkNotesPayload(string SourceNoteId, string TargetNoteId, string Reason);

/// <summary>Payload of a "apply this tag" suggestion.</summary>
public sealed record ApplyTagPayload(string NoteId, string TagName);

public static class SuggestionJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return default;
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
