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
    string? Warning = null,
    IReadOnlyList<string>? Suggestions = null)
{
    public bool IsGrounded => Citations.Count > 0;

    /// <summary>Questions worth asking next, asked after this answer landed.</summary>
    public IReadOnlyList<string> NextQuestions => Suggestions ?? Array.Empty<string>();
}

/// <summary>
/// RAG over the vault: retrieve the closest notes and paper pages (hybrid text +
/// meaning), answer from them only, and keep the citations. With no local model
/// configured the answer is the retrieved passages themselves, never a
/// synthesised guess, and every turn is written to the chat history.
///
/// A linking question ("what connects X and Y?") is retrieved once per subject,
/// so neither half can crowd the other out, and the links, shared tags and shared
/// papers between the retrieved notes are handed to the model as evidence. Those
/// recorded relationships are the only trustworthy answer to "what connects
/// them", and search alone can never produce them.
///
/// The previous turns are passed as context so follow-up questions work, but
/// facts still have to come from the numbered sources of the current question.
/// </summary>
public sealed class ChatService
{
    private const int MaxContextNotes = 8;
    private const int MaxContextPages = 4;
    private const int NotesPerSubject = 3;
    private const int PagesPerSubject = 2;
    private const int MaxHistoryExchanges = 3;
    private const int ExcerptLength = 600;
    private const int MaxConnections = 6;

    /// <summary>Rough ceiling on the sources block, so a small model is not overrun.</summary>
    private const int SourcesBudget = 6000;

    private static readonly Regex CitationMarker = new(@"\[(\d{1,2})\]", RegexOptions.Compiled);

    private static readonly Regex QueryWord = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    /// <summary>Words that say what kind of question it is, not what it is about.</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "and", "are", "between", "can", "connect", "connects", "connection", "connections",
        "could", "difference", "differences", "does", "explain", "for", "from", "have", "how",
        "into", "link", "links", "linked", "me", "my", "note", "notes", "of", "on", "our",
        "paper", "papers", "relate", "related", "relationship", "say", "says", "should", "that",
        "the", "their", "them", "then", "there", "these", "they", "this", "those", "to", "two",
        "vault", "versus", "was", "what", "when", "where", "which", "who", "why", "with", "would",
        "your",
    };

    private const string SystemPrompt =
        "You are Idea-Craft, a research assistant inside a private local vault. " +
        "Answer ONLY from the numbered sources in the prompt; earlier turns of the " +
        "conversation are context for what is being asked, never a source of facts. " +
        "Cite every claim with its source number in square brackets, like [2]. " +
        "When the vault records connections between the sources, use them to explain " +
        "how the sources relate, and cite the sources those connections join. " +
        "If the sources do not answer the question, say so plainly instead of guessing. " +
        "Never invent citations, note titles, authors, page numbers or connections. " +
        "Keep the answer under 200 words.";

    private readonly IVaultRepository _repo;
    private readonly SearchService _search;
    private IChatModel? _model;

    public ChatService(IVaultRepository repo, SearchService search, IChatModel? model = null)
    {
        _repo = repo;
        _search = search;
        _model = model;
    }

    /// <summary>
    /// Swaps the answering model, so a server chosen in Settings takes effect
    /// without restarting the app. Null means the passages are the answer.
    /// </summary>
    public void UseModel(IChatModel? model) => _model = model;

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

    /// <summary>Forgets the whole conversation. The vault itself is untouched.</summary>
    public Task ClearHistoryAsync(CancellationToken ct = default) =>
        _repo.DeleteChatMessagesAsync(ct);

    /// <summary>
    /// Reports whether the configured model can actually answer right now. A
    /// configured-but-unpulled model fails as a 404 at question time, which reads
    /// like a bug rather than a setup step.
    /// </summary>
    public async Task<string> CheckModelAsync(CancellationToken ct = default)
    {
        if (_model is null)
            return "No local model is configured, so answers are the cited passages themselves.";

        try
        {
            var problem = await _model.ProbeAsync(ct);
            return problem ?? $"{_model.Name} is reachable.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"{_model.Name} could not be reached: {ex.Message}";
        }
    }

    private async Task<ChatAnswer> AnswerAsync(
        string question,
        Func<string, Task>? onDelta,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Ask a question first.", nameof(question));

        question = question.Trim();

        // Saved before the answer exists, so stopping mid-answer still leaves the
        // question in the transcript instead of swallowing what was asked.
        await _repo.SaveChatMessageAsync(ChatMessage.Create(ChatMessage.UserRole, question), ct);

        var history = await RecentTurnsAsync(ct);
        var retrieval = await RetrieveAsync(question, ct);
        var citations = retrieval.Citations;

        ChatAnswer answer;

        if (_model is null)
        {
            answer = BuildExtractive(citations, retrieval.Connections);
            if (onDelta is not null)
                await onDelta(answer.Text);
        }
        else if (onDelta is null)
        {
            answer = await AskModelAsync(question, citations, retrieval.Connections, history, ct);
        }
        else
        {
            answer = await AskModelStreamingAsync(question, citations, retrieval.Connections, history, onDelta, ct);
        }

        if (retrieval.Warning is not null)
            answer = answer with { Warning = answer.Warning ?? retrieval.Warning };

        answer = answer with { Suggestions = await SuggestAsync(citations, ct) };

        await SaveAssistantAsync(answer, ct);
        return answer;
    }

    private Task SaveAssistantAsync(ChatAnswer answer, CancellationToken ct) =>
        _repo.SaveChatMessageAsync(
            ChatMessage.Create(
                ChatMessage.AssistantRole,
                answer.Text,
                SuggestionJson.Serialize(answer.Citations),
                answer.Warning),
            ct);

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

    /// <summary>What one question retrieved, and what the vault records about it.</summary>
    private sealed record Retrieval(
        List<ChatCitation> Citations,
        List<ChatConnection> Connections,
        string? Warning);

    private async Task<Retrieval> RetrieveAsync(string question, CancellationToken ct)
    {
        var subjects = ChatSubjects.Extract(question);

        // Subjects first so the round-robin below gives each of them its best hit
        // before the blended question gets a turn.
        var queries = new List<string>(subjects);
        queries.Add(question);

        var outcomes = new List<IReadOnlyList<SearchHit>>(queries.Count);
        string? warning = null;

        foreach (var query in queries)
        {
            var outcome = await _search.SearchAsync(query, MaxContextNotes * 2, ct);
            warning ??= outcome.Warning;
            outcomes.Add(outcome.Hits);
        }

        var notes = Interleave(
            outcomes.Select(hits => hits.Where(hit => hit.Kind == "note").ToList()).ToList(),
            NotesPerSubject,
            MaxContextNotes);

        var pages = Interleave(
            outcomes
                .Select(hits => hits
                    .Where(hit => hit.Kind == "document" && hit.Page is not null)
                    .GroupBy(hit => hit.Id, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToList())
                .ToList(),
            PagesPerSubject,
            MaxContextPages);

        if (notes.Count == 0 && pages.Count == 0)
            return new Retrieval(new List<ChatCitation>(), new List<ChatConnection>(), warning);

        var noteById = (await _repo.ListNotesAsync(ct)).ToDictionary(n => n.Id, StringComparer.Ordinal);
        var documentById = (await _repo.ListDocumentsAsync(ct)).ToDictionary(d => d.Id, StringComparer.Ordinal);

        var terms = Terms(question, subjects);
        var citations = new List<ChatCitation>(notes.Count + pages.Count);

        foreach (var hit in notes)
        {
            if (!noteById.TryGetValue(hit.Id, out var note))
                continue;

            var source = note.SourceDocumentId is null
                ? null
                : documentById.GetValueOrDefault(note.SourceDocumentId);

            citations.Add(new ChatCitation(
                note.Id,
                note.Title,
                source?.Title,
                note.SourcePage,
                Excerpt(note.Body, terms),
                Number: citations.Count + 1));
        }

        // A page of a paper is evidence in its own right, even before anyone has
        // written a note about it.
        foreach (var hit in pages)
        {
            if (!documentById.TryGetValue(hit.Id, out var document))
                continue;

            var stored = await _repo.ListDocumentPagesAsync(document.Id, ct);
            var text = stored.FirstOrDefault(p => p.Page == hit.Page)?.Text;
            if (string.IsNullOrWhiteSpace(text))
                continue;

            citations.Add(new ChatCitation(
                NoteId: string.Empty,
                NoteTitle: document.Title,
                DocumentTitle: null,
                Page: hit.Page,
                Excerpt: Excerpt(text, terms),
                DocumentId: document.Id,
                Number: citations.Count + 1));
        }

        var connections = await ConnectionsAsync(citations, noteById, documentById, ct);

        return new Retrieval(citations, connections, warning);
    }

    /// <summary>
    /// Round-robin across the per-query hit lists: every query contributes its
    /// best hit before any query contributes its second. Caps keep one subject
    /// from taking every slot, and uniqueness stops a note counted twice.
    /// </summary>
    private static List<SearchHit> Interleave(
        IReadOnlyList<IReadOnlyList<SearchHit>> lists,
        int perList,
        int max)
    {
        var picked = new List<SearchHit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var rank = 0; rank < perList; rank++)
        {
            foreach (var list in lists)
            {
                if (rank >= list.Count)
                    continue;

                var hit = list[rank];
                if (!seen.Add(hit.Id))
                    continue;

                picked.Add(hit);

                if (picked.Count >= max)
                    return picked;
            }
        }

        return picked;
    }

    /// <summary>
    /// The relationships the vault already records between the cited sources:
    /// written links, shared tags, and notes captured from the same paper. These
    /// are the evidence a linking question needs, and search cannot invent them.
    /// </summary>
    private async Task<List<ChatConnection>> ConnectionsAsync(
        IReadOnlyList<ChatCitation> citations,
        IReadOnlyDictionary<string, Note> notes,
        IReadOnlyDictionary<string, PdfDocument> documents,
        CancellationToken ct)
    {
        var connections = new List<ChatConnection>();

        var citedNotes = citations
            .Where(citation => !citation.IsPage && notes.ContainsKey(citation.NoteId))
            .Select(citation => notes[citation.NoteId])
            .GroupBy(note => note.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        if (citedNotes.Count < 2)
            return connections;

        var links = await _repo.ListLinksAsync(ct);

        for (var i = 0; i < citedNotes.Count && connections.Count < MaxConnections; i++)
        {
            for (var j = i + 1; j < citedNotes.Count && connections.Count < MaxConnections; j++)
            {
                var first = citedNotes[i];
                var second = citedNotes[j];

                var link = links.FirstOrDefault(candidate =>
                    (candidate.SourceId == first.Id && candidate.TargetId == second.Id) ||
                    (candidate.SourceId == second.Id && candidate.TargetId == first.Id));

                if (link is not null)
                {
                    connections.Add(new ChatConnection(
                        $"\u201c{first.Title}\u201d \u2194 \u201c{second.Title}\u201d",
                        link.Kind.ToString().ToLowerInvariant() + " link",
                        link.Rationale));
                    continue;
                }

                var sharedTag = first.Tags
                    .Select(tag => tag.Name)
                    .FirstOrDefault(name => second.Tags.Any(tag => tag.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

                if (sharedTag is not null)
                {
                    connections.Add(new ChatConnection(
                        $"\u201c{first.Title}\u201d \u2194 \u201c{second.Title}\u201d",
                        $"both tagged \u201c{sharedTag}\u201d"));
                    continue;
                }

                if (first.SourceDocumentId is not null &&
                    first.SourceDocumentId == second.SourceDocumentId &&
                    documents.TryGetValue(first.SourceDocumentId, out var paper))
                {
                    connections.Add(new ChatConnection(
                        $"\u201c{first.Title}\u201d \u2194 \u201c{second.Title}\u201d",
                        $"both captured from \u201c{paper.Title}\u201d"));
                }
            }
        }

        return connections;
    }

    private static IReadOnlyList<string> Terms(string question, IReadOnlyList<string> subjects)
    {
        var text = subjects.Count > 0 ? string.Join(' ', subjects) + " " + question : question;

        return QueryWord.Matches(text)
            .Select(match => match.Value)
            .Where(word => word.Length >= 4 && !StopWords.Contains(word))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
    }

    private static string Excerpt(string body, IReadOnlyList<string> terms) =>
        BestWindow(Flatten(body), terms, ExcerptLength);

    /// <summary>
    /// The part of a note that the question actually matched, not its opening
    /// lines. A citation whose quote does not contain the claim above it is worse
    /// than no quote: it looks like evidence and is read as evidence.
    /// </summary>
    internal static string BestWindow(string text, IReadOnlyList<string> terms, int length)
    {
        if (text.Length <= length || terms.Count == 0)
            return Clip(text, length);

        var bestStart = 0;
        var bestScore = 0;

        foreach (var term in terms)
        {
            var at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);

            while (at >= 0)
            {
                var start = Math.Clamp(at - (length / 3), 0, text.Length - length);
                var window = text.Substring(start, length);
                var score = terms.Count(candidate => window.Contains(candidate, StringComparison.OrdinalIgnoreCase));

                if (score > bestScore)
                {
                    bestScore = score;
                    bestStart = start;
                }

                at = text.IndexOf(term, at + 1, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (bestScore == 0)
            return Clip(text, length);

        var chosen = text.Substring(bestStart, length).Trim();
        var prefix = bestStart > 0 ? "\u2026" : string.Empty;
        var suffix = bestStart + length < text.Length ? "\u2026" : string.Empty;
        return prefix + chosen + suffix;
    }

    private static string Flatten(string body) => string.Join(
        ' ',
        body.Replace("\r", " ").Replace("\n", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string Clip(string text, int length) =>
        text.Length > length ? text[..length] + "\u2026" : text;

    // ---------------------------------------------------------------- answers

    private static ChatAnswer BuildExtractive(
        IReadOnlyList<ChatCitation> citations,
        IReadOnlyList<ChatConnection> connections)
    {
        if (citations.Count == 0)
            return NothingFound(usedLocalModel: false);

        var builder = new StringBuilder();

        if (connections.Count > 0)
        {
            builder.AppendLine("The vault already records these connections:");
            foreach (var connection in connections)
                builder.Append("- ").AppendLine(connection.ToString());
            builder.AppendLine();
        }

        builder.AppendLine("No local model is configured, so these are the matching passages from your vault:");
        builder.AppendLine();

        foreach (var citation in citations)
        {
            builder.Append('[').Append(citation.Number).Append("] ").Append(citation.Label).AppendLine();
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
        IReadOnlyList<ChatConnection> connections,
        IReadOnlyList<(string Question, string Answer)> history,
        CancellationToken ct)
    {
        if (citations.Count == 0)
            return NothingFound(usedLocalModel: true);

        string text;
        try
        {
            text = (await _model!.CompleteAsync(
                SystemPrompt,
                BuildPrompt(question, citations, connections, history),
                ct)).Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The vault still has an answer; the model just is not reachable.
            return BuildExtractive(citations, connections) with { Warning = "local model unavailable: " + ex.Message };
        }

        return Finalize(text, citations);
    }

    private async Task<ChatAnswer> AskModelStreamingAsync(
        string question,
        IReadOnlyList<ChatCitation> citations,
        IReadOnlyList<ChatConnection> connections,
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
                               SystemPrompt, BuildPrompt(question, citations, connections, history), ct))
            {
                builder.Append(delta);
                await onDelta(delta);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping should cost the answer, not the question: keep whatever
            // was written, marked as unfinished, so it can be read and resumed.
            var partial = builder.ToString().Trim();
            if (partial.Length > 0)
            {
                var stopped = Finalize(partial, citations) with { Warning = "Answer stopped before it finished." };
                await SaveAssistantAsync(stopped, CancellationToken.None);
            }

            throw;
        }
        catch (Exception ex)
        {
            var fallback = BuildExtractive(citations, connections);
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
    /// labelled unverified instead of being trusted. The sources keep the numbers
    /// the answer was written with, so <c>[2]</c> still opens source 2.
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
        IReadOnlyList<ChatConnection> connections,
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

        if (connections.Count > 0)
        {
            builder.AppendLine("Connections the vault already records between these sources:");
            foreach (var connection in connections)
                builder.Append("- ").AppendLine(connection.ToString());
            builder.AppendLine();
        }

        builder.AppendLine("Sources:");

        // Share the budget across the sources instead of letting the first few
        // fill it, so the last source is not the one that disappears.
        var perSource = Math.Clamp(SourcesBudget / Math.Max(1, citations.Count), 280, ExcerptLength);

        for (var i = 0; i < citations.Count; i++)
        {
            var citation = citations[i];
            builder.Append('[').Append(citation.Number).Append("] ");
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
            builder.AppendLine(citation.Excerpt.Length > perSource
                ? citation.Excerpt[..perSource] + "\u2026"
                : citation.Excerpt);
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    // ------------------------------------------------------------ next steps

    /// <summary>
    /// Questions worth asking next, derived from what the answer already cited:
    /// its links first (the vault's own structure), then its tags, then its paper.
    /// Deterministic and offline, so suggestions never cost a model call.
    /// </summary>
    public async Task<IReadOnlyList<string>> SuggestAsync(
        IReadOnlyList<ChatCitation> citations,
        CancellationToken ct = default)
    {
        var suggestions = new List<string>();

        if (citations.Count == 0)
        {
            suggestions.Add("What have I captured most recently?");
            suggestions.Add("Which paper in this vault should I read next?");
            return Limit(suggestions);
        }

        var notes = (await _repo.ListNotesAsync(ct)).ToDictionary(n => n.Id, StringComparer.Ordinal);
        var documents = (await _repo.ListDocumentsAsync(ct)).ToDictionary(d => d.Id, StringComparer.Ordinal);
        var links = await _repo.ListLinksAsync(ct);

        foreach (var citation in citations)
        {
            if (citation.IsPage)
            {
                suggestions.Add($"What else does \u201c{citation.NoteTitle}\u201d argue?");
                continue;
            }

            if (!notes.TryGetValue(citation.NoteId, out var note))
                continue;

            var partner = links
                .Where(link => link.SourceId == note.Id || link.TargetId == note.Id)
                .Select(link => link.SourceId == note.Id ? link.TargetId : link.SourceId)
                .Select(id => notes.GetValueOrDefault(id)?.Title)
                .FirstOrDefault(title => !string.IsNullOrWhiteSpace(title));

            if (partner is not null)
            {
                suggestions.Add($"How does \u201c{note.Title}\u201d connect to \u201c{partner}\u201d?");
                continue;
            }

            var tag = note.Tags.FirstOrDefault()?.Name;
            if (tag is not null)
            {
                suggestions.Add($"What else is tagged \u201c{tag}\u201d?");
                continue;
            }

            if (note.SourceDocumentId is not null &&
                documents.TryGetValue(note.SourceDocumentId, out var paper))
            {
                suggestions.Add($"What else does \u201c{paper.Title}\u201d claim?");
            }
        }

        return Limit(suggestions);

        static IReadOnlyList<string> Limit(List<string> items) => items
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
    }
}
