using Kampf.Application.Abstractions;
using Kampf.Application.Embeddings;
using Kampf.Application.Models;
using Kampf.Application.Services;
using Kampf.Domain.Entities;
using Kampf.Domain.ValueObjects;
using Xunit;

namespace Kampf.Application.Tests;

public class SuggestionEngineTests
{
    private sealed class CountingEmbeddings : IEmbeddingProvider
    {
        private readonly HashingEmbeddingProvider _inner = new();

        public int TextsEmbedded { get; private set; }

        public string Model => _inner.Model;

        public float SimilarityFloor => _inner.SimilarityFloor;

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            TextsEmbedded += texts.Count;
            return _inner.EmbedAsync(texts, ct);
        }
    }

    private sealed class Harness
    {
        public FakeVaultRepository Repo { get; } = new();
        public CountingEmbeddings Embeddings { get; } = new();
        public NoteService Notes { get; }
        public SuggestionService Suggestions { get; }
        public SuggestionEngine Engine { get; }

        public Harness()
        {
            Notes = new NoteService(Repo);
            Suggestions = new SuggestionService(Repo, Notes);
            Engine = new SuggestionEngine(Repo, Embeddings);
        }
    }

    private static async Task<(Harness Harness, Note Graph, Note Rendering, Note Unrelated)> SeedAsync()
    {
        var harness = new Harness();

        var graph = await harness.Notes.CreateAsync(
            "Force-directed graph layout",
            "Force directed graph rendering with level of detail for large layouts. Graph layout is expensive.");
        var rendering = await harness.Notes.CreateAsync(
            "Graph rendering at scale",
            "Level of detail rendering keeps large graph layouts interactive at scale.");
        var unrelated = await harness.Notes.CreateAsync(
            "Vault encryption",
            "SQLCipher key derivation and local vault storage for private research.");
        await harness.Notes.CreateAsync("List", string.Empty); // too thin to index

        return (harness, graph, rendering, unrelated);
    }

    [Fact]
    public async Task Related_notes_are_queued_as_a_link_with_a_readable_reason()
    {
        var (harness, graph, rendering, unrelated) = await SeedAsync();

        var batch = await harness.Engine.GenerateAsync();

        var link = Assert.Single(harness.Repo.Suggestions, s => s.Kind == SuggestionKind.LinkNotes);
        var payload = SuggestionJson.Deserialize<LinkNotesPayload>(link.PayloadJson);

        Assert.NotNull(payload);
        Assert.Equal(SuggestionStatus.Pending, link.Status);
        Assert.Equal(1, batch.LinkSuggestions);
        Assert.Contains("Force-directed graph layout", link.Title);
        Assert.Contains("Graph rendering at scale", link.Title);
        Assert.Contains("similar", payload!.Reason);
        Assert.Contains("graph", payload.Reason);

        var pair = new[] { payload.SourceNoteId, payload.TargetNoteId };
        Assert.Contains(graph.Id, pair);
        Assert.Contains(rendering.Id, pair);
        Assert.DoesNotContain(unrelated.Id, pair);
    }

    [Fact]
    public async Task The_graph_is_untouched_until_the_link_is_accepted()
    {
        var (harness, _, _, _) = await SeedAsync();

        await harness.Engine.GenerateAsync();
        Assert.Empty(harness.Repo.Links);

        var link = Assert.Single(harness.Repo.Suggestions, s => s.Kind == SuggestionKind.LinkNotes);
        await harness.Suggestions.DecideAsync(link.Id, accept: true);

        var edge = Assert.Single(harness.Repo.Links);
        Assert.Equal(EdgeKind.AcceptedSuggestion, edge.Kind);
        Assert.Contains(harness.Repo.Audit, a => a.Action == "suggestion.accepted");
    }

    [Fact]
    public async Task A_second_run_does_not_duplicate_the_same_proposal()
    {
        var (harness, _, _, _) = await SeedAsync();

        var first = await harness.Engine.GenerateAsync();
        var second = await harness.Engine.GenerateAsync();

        Assert.True(first.LinkSuggestions > 0);
        Assert.Equal(0, second.Total);
        Assert.Single(harness.Repo.Suggestions);
    }

    [Fact]
    public async Task A_rejected_link_is_not_proposed_again()
    {
        var (harness, _, _, _) = await SeedAsync();

        await harness.Engine.GenerateAsync();
        var link = Assert.Single(harness.Repo.Suggestions, s => s.Kind == SuggestionKind.LinkNotes);
        await harness.Suggestions.DecideAsync(link.Id, accept: false);

        var second = await harness.Engine.GenerateAsync();

        Assert.Equal(0, second.Total);
        Assert.Empty(harness.Repo.Links);
    }

    [Fact]
    public async Task Notes_that_are_already_linked_are_not_proposed()
    {
        var (harness, graph, rendering, _) = await SeedAsync();
        await harness.Repo.AddLinkAsync(Link.Create(graph.Id, rendering.Id, EdgeKind.Wiki));

        var batch = await harness.Engine.GenerateAsync();

        Assert.Equal(0, batch.LinkSuggestions);
        Assert.DoesNotContain(harness.Repo.Suggestions, s => s.Kind == SuggestionKind.LinkNotes);
    }

    [Fact]
    public async Task Unchanged_notes_are_not_re_embedded_but_edited_ones_are()
    {
        var (harness, graph, _, _) = await SeedAsync();

        await harness.Engine.GenerateAsync();
        var afterFirst = harness.Embeddings.TextsEmbedded;
        Assert.Equal(3, afterFirst); // the one-word note is too thin to index

        await harness.Engine.GenerateAsync();
        Assert.Equal(afterFirst, harness.Embeddings.TextsEmbedded);

        graph.Body += " Community detection over the same layout.";
        await harness.Notes.SaveAsync(graph);

        await harness.Engine.GenerateAsync();
        Assert.Equal(afterFirst + 1, harness.Embeddings.TextsEmbedded);
    }

    [Fact]
    public async Task A_tag_shared_by_related_notes_is_proposed_and_applied_on_accept()
    {
        var harness = new Harness();

        var a = await harness.Notes.CreateAsync(
            "Modularity in citation graphs",
            "Modularity maximisation for citation graph communities and clustering.");
        var b = await harness.Notes.CreateAsync(
            "Community detection benchmarks",
            "Benchmarks for citation graph community detection and modularity clustering.");
        var untagged = await harness.Notes.CreateAsync(
            "Citation graph clustering notes",
            "Notes on modularity clustering for citation graph communities.");

        await harness.Notes.SetTagsAsync(a, new[] { "graphs" });
        await harness.Notes.SetTagsAsync(b, new[] { "graphs" });

        var batch = await harness.Engine.GenerateAsync();

        Assert.True(batch.TagSuggestions > 0);
        var tagSuggestion = harness.Repo.Suggestions
            .Single(s => s.Kind == SuggestionKind.ApplyTag &&
                         SuggestionJson.Deserialize<ApplyTagPayload>(s.PayloadJson)!.NoteId == untagged.Id);
        Assert.Contains("graphs", tagSuggestion.Title);

        await harness.Suggestions.DecideAsync(tagSuggestion.Id, accept: true);

        var reloaded = await harness.Repo.GetNoteAsync(untagged.Id);
        Assert.Contains(reloaded!.Tags, t => t.Name == "graphs");
    }

    [Fact]
    public async Task Large_vaults_block_candidates_by_shared_words_instead_of_scanning_every_pair()
    {
        var harness = new Harness();

        for (var i = 0; i < 1600; i++)
        {
            var note = Note.Create(
                $"Note {i}",
                $"cluster{i % 80} detail item{i} analysis of the corpus number {i}");
            await harness.Repo.SaveNoteAsync(note);
        }

        var batch = await harness.Engine.GenerateAsync();

        Assert.True(batch.LinkSuggestions > 0);
        Assert.All(
            harness.Repo.Suggestions.Where(s => s.Kind == SuggestionKind.LinkNotes),
            s => Assert.Contains("similar", s.Title + s.PayloadJson));
    }

    [Fact]
    public void Hashing_embedder_is_normalized_and_ranks_overlap_above_noise()
    {
        var related = HashingEmbeddingProvider.Embed("graph rendering engine");
        var nearby = HashingEmbeddingProvider.Embed("graph rendering pipeline");
        var far = HashingEmbeddingProvider.Embed("archival metadata standards");

        Assert.Equal(1f, related.Sum(v => v * v), 3);
        Assert.True(VectorMath.Cosine(related, nearby) > VectorMath.Cosine(related, far));
        Assert.True(VectorMath.Cosine(related, nearby) > 0.3f);
    }
}
