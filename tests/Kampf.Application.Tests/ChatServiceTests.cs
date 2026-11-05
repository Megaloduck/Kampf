using Kampf.Application.Abstractions;
using Kampf.Application.Embeddings;
using Kampf.Application.Services;
using Kampf.Domain.Entities;
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
}
