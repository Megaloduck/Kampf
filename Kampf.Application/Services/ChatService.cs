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
/// RAG over the vault: retrieve the closest notes and paper pages (hybrid text +
/// meaning), answer from them only, and keep the citations. With no local model
/// configured the answer is the retrieved passages themselves, never a
/// synthesised guess, and every turn is written to the chat history.
///
/// The previous turns are passed as context so follow-up questions ("and what
/// about the second one?") work, but facts still have to come from the numbered
/// sources of the current question.
/// </summary>
public sealed class ChatService
{
    private const int MaxContextNotes = 6;
    private const int MaxContextPages = 3;
    private const int MaxHistoryExchanges = 3;
    private const int ExcerptLength = 600;

    private static readonly Regex CitationMarker = new(@"\[(\d{1,2})\]", RegexOptions.Compiled);

    private const string SystemPrompt =
        "You are Idea-Craft, a research assistant inside a private local vault. " +
        "Answer ONLY from the numbered sources in the prompt; earlier turns of the " +
        "conversation are context for what is being asked, never a source of facts. " +
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

    public Task<ChatAnswer> AskAsync(string question, CancellationToken ct = default) =>
        AnswerAsync(question, onDelta: null, ct);

    /// <summary>
    /// Answers while forwarding the model's output as it arrives. The returned
    /// answer is the finished one, with citations, so the caller never has to
    /// assemble the stream itself.
    /// </summary>
    public Task<ChatAnswer> AskStreamingAsync(
        string question,
        Func<string, Task> onDelta,
        CancellationToken ct = default) =>
        AnswerAsync(question, onDelta, ct);

    public Task<IReadOnlyList<ChatMessage>> HistoryAsync(int limit = 200, CancellationToken ct = default) =>
        _repo.ListChatMessagesAsync(limit, ct);

    private async Task<ChatAnswer> AnswerAsync(
        string question,
        Func<string, Task>? onDelta,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Ask a question first.", nameof(question));

        question = question.Trim();

        var history = await RecentTurnsAsync(ct);
        var (citations, searchWarning) = await RetrieveAsync(question, ct);

        ChatAnswer answer;

        if (_model is null)
        {
            answer = BuildExtractive(citations);
            if (onDelta is not null)
                await onDelta(answer.Text);
        }
        else if (onDelta is null)
        {
            answer = await AskModelAsync(question, citations, history, ct);
        }
        else
        {
            answer = await AskModelStreamingAsync(question, citations, history, onDelta, ct);
        }

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

    // ---------------------------------------------------------------- history

    private async Task<IReadOnlyList<(string Question, string Answer)>> RecentTurnsAsync(CancellationToken ct)
    {
        var messages = await _repo.ListChatMessagesAsync(40, ct);

        var turns = new List<(string Question, string Answer)>();
        string? pending = null;

        foreach (var message in messages)
        {
            if (message.IsUser)
            {
                pending = message.Text;
                continue;
            }

            if (pending is null)
                continue;

            turns.Add((pending, message.Text));
            pending = null;
        }

        return turns.Count <= MaxHistoryExchanges
            ? turns
            : turns.Skip(turns.Count - MaxHistoryExchanges).ToList();
    }

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

        var pageHits = outcome.Hits
            .Where(h => h.Kind == "document" && h.Page is not null)
            .GroupBy(h => h.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .Take(MaxContextPages)
            .ToList();

        if (noteIds.Count == 0 && pageHits.Count == 0)
            return (new List<ChatCitation>(), outcome.Warning);

        var notes = (await _repo.ListNotesAsync(ct)).ToDictionary(n => n.Id, StringComparer.Ordinal);
        var documents = (await _repo.ListDocumentsAsync(ct)).ToDictionary(d => d.Id, StringComparer.Ordinal);

        var citations = new List<ChatCitation>(noteIds.Count + pageHits.Count);

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

        // A page of a paper is evidence in its own right, even before anyone has
        // written a note about it.
        foreach (var hit in pageHits)
        {
            if (!documents.TryGetValue(hit.Id, out var document))
                continue;

            var pages = await _repo.ListDocumentPagesAsync(document.Id, ct);
            var text = pages.FirstOrDefault(p => p.Page == hit.Page)?.Text;
            if (string.IsNullOrWhiteSpace(text))
                continue;

            citations.Add(new ChatCitation(
                NoteId: string.Empty,
                NoteTitle: document.Title,
                DocumentTitle: null,
                Page: hit.Page,
                Excerpt: Excerpt(text),
                DocumentId: document.Id));
        }

        return (citations, outcome.Warning);
    }

    private static string Excerpt(string body) => Clip(Flatten(body), ExcerptLength);

    private static string Flatten(string body) => string.Join(
        ' ',
        body.Replace("\r", " ").Replace("\n", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string Clip(string text, int length) =>
        text.Length > length ? text[..length] + "\u2026" : text;

    // ---------------------------------------------------------------- answers

    private static ChatAnswer BuildExtractive(IReadOnlyList<ChatCitation> citations)
    {
        if (citations.Count == 0)
            return NothingFound(usedLocalModel: false);

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

    private static ChatAnswer NothingFound(bool usedLocalModel) => new(
        "Nothing in this vault matches that question yet. Import a paper or write a note about it, then ask again.",
        Array.Empty<ChatCitation>(),
        usedLocalModel);

    private async Task<ChatAnswer> AskModelAsync(
        string question,
        IReadOnlyList<ChatCitation> citations,
        IReadOnlyList<(string Question, string Answer)> history,
        CancellationToken ct)
    {
        if (citations.Count == 0)
            return NothingFound(usedLocalModel: true);

        string text;
        try
        {
            text = (await _model!.CompleteAsync(SystemPrompt, BuildPrompt(question, citations, history), ct)).Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The vault still has an answer; the model just is not reachable.
            return BuildExtractive(citations) with { Warning = "local model unavailable: " + ex.Message };
        }

        return Finalize(text, citations);
    }

    private async Task<ChatAnswer> AskModelStreamingAsync(
        string question,
        IReadOnlyList<ChatCitation> citations,
        IReadOnlyList<(string Question, string Answer)> history,
        Func<string, Task> onDelta,
        CancellationToken ct)
    {
        if (citations.Count == 0)
        {
            var none = NothingFound(usedLocalModel: true);
            await onDelta(none.Text);
            return none;
        }

        var builder = new StringBuilder();

        try
        {
            await foreach (var delta in _model!.StreamAsync(
                               SystemPrompt, BuildPrompt(question, citations, history), ct))
            {
                builder.Append(delta);
                await onDelta(delta);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var fallback = BuildExtractive(citations);
            await onDelta(fallback.Text);
            return fallback with { Warning = "local model unavailable: " + ex.Message };
        }

        var text = builder.ToString().Trim();

        // Nothing streamed at all: treat it as an empty answer, not as success.
        if (text.Length == 0)
            return new ChatAnswer("The local model returned an empty answer.", Array.Empty<ChatCitation>(), true);

        return Finalize(text, citations);
    }

    /// <summary>
    /// Keeps only the citations the answer actually used. A marker outside the
    /// provided sources is not evidence, and an answer with no valid marker is
    /// labelled unverified instead of being trusted.
    /// </summary>
    private static ChatAnswer Finalize(string text, IReadOnlyList<ChatCitation> citations)
    {
        if (text.Length == 0)
            return new ChatAnswer("The local model returned an empty answer.", Array.Empty<ChatCitation>(), true);

        var used = new List<ChatCitation>();
        foreach (Match match in CitationMarker.Matches(text))
        {
            if (!int.TryParse(match.Groups[1].Value, out var index))
                continue;

            if (index < 1 || index > citations.Count)
                continue;

            var citation = citations[index - 1];
            if (!used.Contains(citation))
                used.Add(citation);
        }

        var warning = used.Count == 0
            ? "This answer cites no source \u2014 treat it as unverified."
            : null;

        return new ChatAnswer(text, used, UsedLocalModel: true, warning);
    }

    private static string BuildPrompt(
        string question,
        IReadOnlyList<ChatCitation> citations,
        IReadOnlyList<(string Question, string Answer)> history)
    {
        var builder = new StringBuilder();

        if (history.Count > 0)
        {
            builder.AppendLine("Earlier in this conversation (context only \u2014 facts still come from the sources):");
            foreach (var (asked, answered) in history)
            {
                builder.Append("Q: ").AppendLine(Clip(Flatten(asked), 200));
                builder.Append("A: ").AppendLine(Clip(Flatten(answered), 320));
            }

            builder.AppendLine();
        }

        builder.AppendLine("Question: " + question);
        builder.AppendLine();
        builder.AppendLine("Sources:");

        for (var i = 0; i < citations.Count; i++)
        {
            var citation = citations[i];
            builder.Append('[').Append(i + 1).Append("] ");
            builder.Append(citation.NoteTitle);

            if (citation.IsPage)
            {
                builder.Append(" \u2014 paper page ").Append(citation.Page);
            }
            else
            {
                if (citation.DocumentTitle is not null)
                    builder.Append(" (from \u201c").Append(citation.DocumentTitle).Append('\u201d');
                if (citation.Page is not null)
                    builder.Append(citation.DocumentTitle is null ? " (p. " : ", p. ").Append(citation.Page);
                if (citation.DocumentTitle is not null || citation.Page is not null)
                    builder.Append(')');
            }

            builder.AppendLine();
            builder.AppendLine(citation.Excerpt);
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }
}
