using Kampf.Application.Abstractions;
using Kampf.Application.Embeddings;
using Kampf.Application.Services;
using Kampf.Domain.Entities;
using Kampf.Domain.ValueObjects;
using Xunit;

namespace Kampf.Application.Tests;

public class ChatServiceTests
{
    private sealed class FakeChatModel : IChatModel
    {
        private readonly Func<string> _reply;

        public FakeChatModel(Func<string> reply) => _reply = reply;

        public string Name => "ollama:fake";

        public string? LastPrompt { get; private set; }

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            LastPrompt = userPrompt;
            return Task.FromResult(_reply());
        }

        public async IAsyncEnumerable<string> StreamAsync(
            string systemPrompt,
            string userPrompt,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            LastPrompt = userPrompt;
            var reply = _reply();

            // Two deltas, so the caller has to assemble them in order.
            var middle = reply.Length / 2;
            yield return reply[..middle];
            await Task.Yield();
            yield return reply[middle..];
        }
    }

    /// <summary>Writes one word and then stops, as the Stop button does.</summary>
    private sealed class CancellingChatModel : IChatModel
    {
        public string Name => "ollama:cancelling";

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            throw new OperationCanceledException();

        public async IAsyncEnumerable<string> StreamAsync(
            string systemPrompt,
            string userPrompt,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return "Modularity";
            await Task.Yield();
            throw new OperationCanceledException();
        }
    }

    private sealed class OfflineChatModel : IChatModel
    {
        public string Name => "ollama:offline";

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            throw new HttpRequestException("connection refused");

        public IAsyncEnumerable<string> StreamAsync(
            string systemPrompt,
            string userPrompt,
            CancellationToken ct = default) =>
            throw new HttpRequestException("connection refused");
    }

    private sealed class Harness
    {
        public FakeVaultRepository Repo { get; } = new();
        public NoteService Notes { get; }
        public SearchService Search { get; }
        public PdfDocument Paper { get; }

        public Harness()
        {
            Notes = new NoteService(Repo);
            Search = new SearchService(Repo, new NoteEmbeddingIndex(Repo, new HashingEmbeddingProvider()));
            Paper = PdfDocument.Create(Path.Combine("assets", "methods.pdf"), "Community detection methods", 12);
            Repo.Documents[Paper.Id] = Paper;
        }

        public async Task<Note> SeedHighlightAsync() =>
            await Notes.CreateFromHighlightAsync(
                Paper,
                4,
                "Modularity maximisation finds communities in citation networks and is widely used.");

        public ChatService With(IChatModel? model) => new(Repo, Search, model);

        public Task<Note> SeedNoteAsync(string title, string body) => Notes.CreateAsync(title, body);
    }

    [Fact]
    public async Task Without_a_model_the_answer_is_the_cited_passages()
    {
        var harness = new Harness();
        var note = await harness.SeedHighlightAsync();

        var answer = await harness.With(null).AskAsync("modularity communities");

        Assert.False(answer.UsedLocalModel);
        Assert.True(answer.IsGrounded);
        Assert.Contains("[1]", answer.Text);

        var citation = Assert.Single(answer.Citations);
        Assert.Equal(note.Id, citation.NoteId);
        Assert.Equal(4, citation.Page);
        Assert.Equal("Community detection methods", citation.DocumentTitle);
        Assert.Contains("p. 4", citation.Label);
    }

    [Fact]
    public async Task A_grounded_model_answer_keeps_only_the_sources_it_cited()
    {
        var harness = new Harness();
        await harness.SeedHighlightAsync();

        var model = new FakeChatModel(() => "Modularity finds communities in citation networks [1].");
        var answer = await harness.With(model).AskAsync("how are communities found?");

        Assert.True(answer.UsedLocalModel);
        Assert.True(answer.IsGrounded);
        Assert.Null(answer.Warning);
        Assert.Single(answer.Citations);
        Assert.Contains("Sources:", model.LastPrompt);
    }

    [Fact]
    public async Task A_citation_outside_the_sources_is_dropped_and_flagged()
    {
        var harness = new Harness();
        await harness.SeedHighlightAsync();

        var model = new FakeChatModel(() => "Modularity is the only method that matters [9].");
        var answer = await harness.With(model).AskAsync("what is modularity?");

        Assert.Empty(answer.Citations);
        Assert.False(answer.IsGrounded);
        Assert.NotNull(answer.Warning);
        Assert.Contains("unverified", answer.Warning!);
    }

    [Fact]
    public async Task A_model_that_cannot_be_reached_falls_back_to_the_passages()
    {
        var harness = new Harness();
        await harness.SeedHighlightAsync();

        var answer = await harness.With(new OfflineChatModel()).AskAsync("modularity communities");

        Assert.True(answer.IsGrounded);
        Assert.Single(answer.Citations);
        Assert.NotNull(answer.Warning);
        Assert.Contains("unavailable", answer.Warning!);
    }

    [Fact]
    public async Task An_empty_vault_says_so_instead_of_inventing_an_answer()
    {
        var harness = new Harness();

        var answer = await harness.With(new FakeChatModel(() => "Invented [1].")).AskAsync("quantum gravity");

        Assert.Empty(answer.Citations);
        Assert.Contains("Nothing in this vault", answer.Text);
    }

    [Fact]
    public async Task Both_turns_are_written_to_the_chat_history_with_citations()
    {
        var harness = new Harness();
        var note = await harness.SeedHighlightAsync();

        await harness.With(null).AskAsync("modularity communities");

        Assert.Equal(2, harness.Repo.Chat.Count);
        Assert.Equal(ChatMessage.UserRole, harness.Repo.Chat[0].Role);
        Assert.Equal(ChatMessage.AssistantRole, harness.Repo.Chat[1].Role);
        Assert.Contains(note.Id, harness.Repo.Chat[1].CitationsJson!);
    }

    [Fact]
    public async Task A_linking_question_reaches_both_of_its_subjects()
    {
        var harness = new Harness();
        await harness.SeedNoteAsync(
            "Modularity",
            "modularity compares the observed edge density of a partition with the expected density");
        await harness.SeedNoteAsync(
            "Homophily",
            "homophily measures how strongly nodes attach to nodes that resemble them");

        var model = new FakeChatModel(() => "Both are about partitioning [1][2].");
        var answer = await harness.With(model).AskAsync("What connects modularity and homophily?");

        // One blended query would surface whichever half ranked higher; both
        // subjects have to survive into the prompt.
        Assert.Contains("Modularity", model.LastPrompt!);
        Assert.Contains("Homophily", model.LastPrompt!);
        Assert.Equal(2, answer.Citations.Count);
    }

    [Fact]
    public async Task A_recorded_link_between_sources_is_given_to_the_model()
    {
        var harness = new Harness();
        var first = await harness.SeedNoteAsync(
            "Modularity",
            "modularity compares the observed edge density of a partition with the expected density");
        var second = await harness.SeedNoteAsync(
            "Homophily",
            "homophily measures how strongly nodes attach to nodes that resemble them");

        harness.Repo.Links.Add(Link.Create(
            first.Id,
            second.Id,
            EdgeKind.Manual,
            "both partition the same citation network"));

        var model = new FakeChatModel(() => "They share one network [1].");
        await harness.With(model).AskAsync("What connects modularity and homophily?");

        Assert.Contains("Connections the vault already records", model.LastPrompt!);
        Assert.Contains("both partition the same citation network", model.LastPrompt!);
    }

    [Fact]
    public async Task Without_a_model_the_connections_are_the_answer()
    {
        var harness = new Harness();
        var first = await harness.SeedNoteAsync(
            "Modularity",
            "modularity compares the observed edge density of a partition with the expected density");
        var second = await harness.SeedNoteAsync(
            "Homophily",
            "homophily measures how strongly nodes attach to nodes that resemble them");

        harness.Repo.Links.Add(Link.Create(first.Id, second.Id, EdgeKind.Manual, "same citation network"));

        var answer = await harness.With(null).AskAsync("What connects modularity and homophily?");

        Assert.Contains("The vault already records these connections", answer.Text);
        Assert.Contains("same citation network", answer.Text);
    }

    [Fact]
    public async Task A_citation_quotes_the_passage_that_matched_not_the_opening()
    {
        var harness = new Harness();

        var filler = string.Join(' ', Enumerable.Repeat("filler words about nothing in particular", 40));
        await harness.SeedNoteAsync("A long note", filler + " the claim about modularity arrives only at the end");

        var answer = await harness.With(null).AskAsync("modularity");

        var citation = Assert.Single(answer.Citations);
        Assert.Contains("modularity", citation.Excerpt);
        Assert.True(citation.Excerpt.Length > 0);
    }

    [Fact]
    public async Task Streaming_answers_are_assembled_in_order()
    {
        var harness = new Harness();
        await harness.SeedHighlightAsync();

        var deltas = new List<string>();
        var model = new FakeChatModel(() => "Modularity finds communities [1].");

        var answer = await harness.With(model).AskStreamingAsync(
            "modularity communities",
            delta =>
            {
                deltas.Add(delta);
                return Task.CompletedTask;
            });

        Assert.Equal(2, deltas.Count);
        Assert.Equal(answer.Text, string.Concat(deltas));
        Assert.Equal("Modularity finds communities [1].", answer.Text);
        Assert.Equal(answer.Text, harness.Repo.Chat[1].Text);
    }

    [Fact]
    public async Task Stopping_mid_answer_keeps_the_question_and_what_was_written()
    {
        var harness = new Harness();
        await harness.SeedHighlightAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.With(new CancellingChatModel()).AskStreamingAsync(
                "modularity communities",
                _ => Task.CompletedTask));

        Assert.Equal(2, harness.Repo.Chat.Count);
        Assert.Contains("modularity communities", harness.Repo.Chat[0].Text);
        Assert.Equal(ChatMessage.AssistantRole, harness.Repo.Chat[1].Role);
        Assert.Equal("Modularity", harness.Repo.Chat[1].Text);
        Assert.Contains("stopped", harness.Repo.Chat[1].Warning!.ToLowerInvariant());
    }

    [Fact]
    public async Task An_unverified_answer_is_stored_with_its_warning()
    {
        var harness = new Harness();
        await harness.SeedHighlightAsync();

        var model = new FakeChatModel(() => "Modularity is the only method that matters [9].");
        await harness.With(model).AskAsync("modularity communities");

        Assert.Null(harness.Repo.Chat[0].Warning);
        Assert.Contains("unverified", harness.Repo.Chat[1].Warning!.ToLowerInvariant());
    }

    [Fact]
    public async Task An_answer_offers_questions_that_follow_it()
    {
        var harness = new Harness();
        var first = await harness.SeedNoteAsync(
            "Modularity",
            "modularity compares the observed edge density of a partition with the expected density");
        var second = await harness.SeedNoteAsync(
            "Homophily",
            "homophily measures how strongly nodes attach to nodes that resemble them");

        harness.Repo.Links.Add(Link.Create(first.Id, second.Id, EdgeKind.Manual));

        var answer = await harness.With(null).AskAsync("What connects modularity and homophily?");

        Assert.NotEmpty(answer.NextQuestions);
        Assert.Contains(answer.NextQuestions, question => question.Contains("Homophily"));
    }

    [Fact]
    public async Task Clearing_the_history_leaves_the_vault_alone()
    {
        var harness = new Harness();
        await harness.SeedHighlightAsync();

        await harness.With(null).AskAsync("modularity communities");
        await harness.With(null).ClearHistoryAsync();

        Assert.Empty(harness.Repo.Chat);
        Assert.NotEmpty(harness.Repo.Notes);
    }

    [Fact]
    public async Task A_model_that_cannot_answer_says_so_when_checked()
    {
        var harness = new Harness();

        var without = await harness.With(null).CheckModelAsync();
        Assert.Contains("no local model", without.ToLowerInvariant());
    }
}
