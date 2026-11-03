using Kampf.Domain.ValueObjects;

namespace Kampf.Domain.Entities;

/// <summary>
/// One turn of the Idea-Craft conversation. Assistant turns carry the citations
/// that back the answer, so an answer can always be traced back to a note (and
/// the page it was captured from).
/// </summary>
public sealed class ChatMessage
{
    public const string UserRole = "user";
    public const string AssistantRole = "assistant";

    public string Id { get; set; } = Ids.NewId();
    public string Role { get; set; } = UserRole;
    public string Text { get; set; } = string.Empty;
    public string? CitationsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public bool IsUser => Role == UserRole;

    public static ChatMessage Create(string role, string text, string? citationsJson = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("A chat message needs text.", nameof(text));

        return new ChatMessage
        {
            Role = role,
            Text = text.Trim(),
            CitationsJson = citationsJson,
        };
    }
}
