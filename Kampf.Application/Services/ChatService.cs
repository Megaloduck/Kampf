using System.Text;
using System.Text.RegularExpressions;
using Kampf.Application.Abstractions;
using Kampf.Application.Models;
using Kampf.Domain.Entities;

namespace Kampf.Application.Services;

/// <summary>
/// One answer from Idea-Craft. <see cref="Citations"/> is what makes the claim
/// checkable; an answer with none is flagged as unverified rather than trusted.
/// </summary>
public sealed record ChatAnswer(
    string Text,
    IReadOnlyList<ChatCitation> Citations,
    bool UsedLocalModel,
    string? Warning = null)
{
    public bool IsGrounded => Citations.Count > 0;
}

/// <summary>
/// RAG over the vault: retrieve the closest notes (hybrid text + meaning),
/// answer from them only, and keep the citations. With no local model
/// configured the answer is the retrieved passages themselves, never a
/// synthesised guess, and every turn is written to the chat history.
/// </summary>
public sealed class ChatService
{
    private const int MaxContextNotes = 6;
    private const int ExcerptLength = 600;

    private static readonly Regex CitationMarker = new(@"\[(\d{1,2})\]", RegexOptions.Compiled);

    private const string SystemPrompt =
        "You are Idea-Craft, a research assistant inside a private local vault. " +
        "Answer ONLY from the numbered sources in the prompt. " +
        "Cite every claim with its source number in square brackets, like [2]. " +
        "If the sources do not answer the question, say so plainly instead of guessing. " +
        "Never invent citations, note titles, authors or page numbers. Keep the answer under 200 words.";

    private readonly IVaultRepository _repo;
    private readonly SearchService _search;
    private readonly IChatModel? _model;

    public ChatService(IVaultRepository repo, SearchService search, IChatModel? model = null)
    {
        _repo = repo;
        _search = search;
        _model = model;
    }

    /// <summary>Shown in the chat header so the user knows what is answering.</summary>
    public string ModelLabel => _model?.Name ?? "no local model \u2014 passages only";

    public bool HasLocalModel => _model is not null;

    public async Task<ChatAnswer> AskAsync(string question, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Ask a question first.", nameof(question));

        question = question.Trim();

        var (citations, searchWarning) = await RetrieveAsync(question, ct);

        var answer = _model is null
            ? BuildExtractive(citations)
            : await AskModelAsync(question, citations, ct);

        if (searchWarning is not null)
            answer = answer with { Warning = answer.Warning ?? searchWarning };

        await _repo.SaveChatMessageAsync(ChatMessage.Create(ChatMessage.UserRole, question), ct);
        await _repo.SaveChatMessageAsync(
            ChatMessage.Create(
                ChatMessage.AssistantRole,
                answer.Text,
                SuggestionJson.Serialize(answer.Citations)),
            ct);

        return answer;
    }

    public Task<IReadOnlyList<ChatMessage>> HistoryAsync(int limit = 200, CancellationToken ct = default) =>
        _repo.ListChatMessagesAsync(limit, ct);

    // --------------------------------------------------------------- retrieval

    private async Task<(List<ChatCitation> Citations, string? Warning)> RetrieveAsync(
        string question,
        CancellationToken ct)
    {
        var outcome = await _search.SearchAsync(question, MaxContextNotes * 2, ct);

        var noteIds = outcome.Hits
            .Where(h => h.Kind == "note")
            .Select(h => h.Id)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxContextNotes)
            .ToList();

        if (noteIds.Count == 0)
            return (new List<ChatCitation>(), outcome.Warning);

        var notes = (await _repo.ListNotesAsync(ct)).ToDictionary(n => n.Id, StringComparer.Ordinal);
        var documents = (await _repo.ListDocumentsAsync(ct)).ToDictionary(d => d.Id, StringComparer.Ordinal);

        var citations = new List<ChatCitation>(noteIds.Count);
        foreach (var id in noteIds)
        {
            if (!notes.TryGetValue(id, out var note))
                continue;

            var source = note.SourceDocumentId is null
                ? null
                : documents.GetValueOrDefault(note.SourceDocumentId);

            citations.Add(new ChatCitation(
                note.Id,
                note.Title,
                source?.Title,
                note.SourcePage,
                Excerpt(note.Body)));
        }

        return (citations, outcome.Warning);
    }

    private static string Excerpt(string body)
    {
        var flat = string.Join(
            ' ',
            body.Replace("\r", " ").Replace("\n", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return flat.Length > ExcerptLength ? flat[..ExcerptLength] + "\u2026" : flat;
    }

    // ---------------------------------------------------------------- answers

    private static ChatAnswer BuildExtractive(IReadOnlyList<ChatCitation> citations)
    {
        if (citations.Count == 0)
        {
            return new ChatAnswer(
                "Nothing in this vault matches that question yet. Import a paper or write a note about it, then ask again.",
                Array.Empty<ChatCitation>(),
                UsedLocalModel: false);
        }

        var builder = new StringBuilder();
        builder.AppendLine("No local model is configured, so these are the matching passages from your vault:");
        builder.AppendLine();

        for (var i = 0; i < citations.Count; i++)
        {
            var citation = citations[i];
            builder.Append('[').Append(i + 1).Append("] ").Append(citation.Label).AppendLine();
            builder.AppendLine("\u201c" + citation.Excerpt + "\u201d");
            builder.AppendLine();
        }

        return new ChatAnswer(builder.ToString().TrimEnd(), citations, UsedLocalModel: false);
    }

    private async Task<ChatAnswer> AskModelAsync(
        string question,
        IReadOnlyList<ChatCitation> citations,
        CancellationToken ct)
    {
        if (citations.Count == 0)
        {
            return new ChatAnswer(
                "Nothing in this vault matches that question yet. Import a paper or write a note about it, then ask again.",
                Array.Empty<ChatCitation>(),
                UsedLocalModel: true);
        }

        string text;
        try
        {
            text = (await _model!.CompleteAsync(SystemPrompt, BuildPrompt(question, citations), ct)).Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The vault still has an answer; the model just is not reachable.
            var fallback = BuildExtractive(citations);
            return fallback with { Warning = "local model unavailable: " + ex.Message };
        }

        if (text.Length == 0)
        {
            return new ChatAnswer(
                "The local model returned an empty answer.",
                Array.Empty<ChatCitation>(),
                UsedLocalModel: true);
        }

        var used = new List<ChatCitation>();
        foreach (Match match in CitationMarker.Matches(text))
        {
            if (!int.TryParse(match.Groups[1].Value, out var index))
                continue;

            if (index < 1 || index > citations.Count)
                continue; // a citation outside the provided sources is not evidence

            var citation = citations[index - 1];
            if (!used.Contains(citation))
                used.Add(citation);
        }

        var warning = used.Count == 0
            ? "This answer cites no source \u2014 treat it as unverified."
            : null;

        return new ChatAnswer(text, used, UsedLocalModel: true, warning);
    }

    private static string BuildPrompt(string question, IReadOnlyList<ChatCitation> citations)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Question: " + question);
        builder.AppendLine();
        builder.AppendLine("Sources:");

        for (var i = 0; i < citations.Count; i++)
        {
            var citation = citations[i];
            builder.Append('[').Append(i + 1).Append("] ");
            builder.Append(citation.NoteTitle);
            if (citation.DocumentTitle is not null)
                builder.Append(" (from \u201c").Append(citation.DocumentTitle).Append('\u201d');
            if (citation.Page is not null)
                builder.Append(citation.DocumentTitle is null ? " (p. " : ", p. ").Append(citation.Page);
            if (citation.DocumentTitle is not null || citation.Page is not null)
                builder.Append(')');
            builder.AppendLine();
            builder.AppendLine(citation.Excerpt);
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }
}
