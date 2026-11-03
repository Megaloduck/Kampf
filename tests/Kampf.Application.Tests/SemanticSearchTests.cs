using Kampf.Application.Abstractions;
using Kampf.Application.Embeddings;
using Kampf.Application.Services;
using Xunit;

namespace Kampf.Application.Tests;

public class SemanticSearchTests
{
    private sealed class BrokenEmbeddings : IEmbeddingProvider
    {
        public string Model => "broken";

        public float SimilarityFloor => 0.2f;

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            throw new InvalidOperationException("model offline");
    }

    private static (FakeVaultRepository Repo, NoteService Notes) Seed()
    {
        var repo = new FakeVaultRepository();
        return (repo, new NoteService(repo));
    }

    private static SearchService Hybrid(FakeVaultRepository repo) =>
        new(repo, new NoteEmbeddingIndex(repo, new HashingEmbeddingProvider()));

    [Fact]
    public async Task Notes_that_mean_the_same_thing_are_found_without_the_exact_phrase()
    {
        var (repo, notes) = Seed();
        var graph = await notes.CreateAsync(
            "Citation graphs",
            "Modularity maximisation for citation graph communities.");
        await notes.CreateAsync("Bread", "Sourdough starter feeding schedule for the weekend.");

        var outcome = await Hybrid(repo).SearchAsync("modularity communities");

        var hit = Assert.Single(outcome.Hits);
        Assert.Equal(graph.Id, hit.Id);
        Assert.Equal(1, outcome.SemanticOnly);
        Assert.Null(outcome.Warning);
    }

    [Fact]
    public async Task Exact_matches_rank_above_semantic_ones()
    {
        var (repo, notes) = Seed();
        var exact = await notes.CreateAsync(
            "Modularity",
            "Modularity maximisation for citation graph communities.");
        await notes.CreateAsync(
            "Graph communities",
            "Community structure and citation graph modularity measures.");

        var outcome = await Hybrid(repo).SearchAsync("Modularity");

        Assert.Equal(exact.Id, outcome.Hits[0].Id);
        Assert.Equal(0, outcome.SemanticOnly);
    }

    [Fact]
    public async Task The_tag_filter_also_narrows_semantic_results()
    {
        var (repo, notes) = Seed();
        var allowed = await notes.CreateAsync(
            "Citation graphs",
            "Modularity maximisation for citation graph communities.");
        var other = await notes.CreateAsync(
            "Community notes",
            "Community structure and modularity in graph partitions.");
        await notes.SetTagsAsync(allowed, new[] { "graphs" });

        var outcome = await Hybrid(repo).SearchAsync("modularity communities tag:graphs");

        Assert.Contains(outcome.Hits, h => h.Id == allowed.Id);
        Assert.DoesNotContain(outcome.Hits, h => h.Id == other.Id);
    }

    [Fact]
    public async Task Search_without_an_index_is_text_only()
    {
        var (repo, notes) = Seed();
        await notes.CreateAsync("Citation graphs", "Modularity maximisation for citation communities.");

        var outcome = await new SearchService(repo).SearchAsync("modularity communities");

        Assert.Empty(outcome.Hits);
        Assert.Equal(0, outcome.SemanticOnly);
    }

    [Fact]
    public async Task A_stopped_local_model_degrades_to_exact_matches_with_a_warning()
    {
        var (repo, notes) = Seed();
        await notes.CreateAsync("Modularity notes", "Modularity maximisation for citation graph communities.");

        var search = new SearchService(repo, new NoteEmbeddingIndex(repo, new BrokenEmbeddings()));
        var outcome = await search.SearchAsync("modularity");

        Assert.NotEmpty(outcome.Hits);
        Assert.Equal(0, outcome.SemanticOnly);
        Assert.NotNull(outcome.Warning);
        Assert.Contains("unavailable", outcome.Warning!);
    }
}
