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

    /// <summary>Returns the vector mapped to the first marker found in each text.</summary>
    private sealed class ScriptedEmbeddings : IEmbeddingProvider
    {
        private readonly Dictionary<string, float[]> _vectors = new(StringComparer.Ordinal);

        public string Model => "scripted";

        public float SimilarityFloor => 0.5f;

        public void Map(string marker, float x, float y) =>
            _vectors[marker] = new[] { x, y };

        public Task<IReadOnlyList<float[]>> EmbedAsync(
            IReadOnlyList<string> texts,
            CancellationToken ct = default)
        {
            IReadOnlyList<float[]> result = texts
                .Select(text => _vectors.First(v => text.Contains(v.Key, StringComparison.Ordinal)).Value)
                .ToList();
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task An_isolated_note_is_offered_a_way_into_the_graph()
    {
        var repo = new FakeVaultRepository();
        var notes = new NoteService(repo);
        var embeddings = new ScriptedEmbeddings();
        var engine = new SuggestionEngine(repo, embeddings);
        var suggestions = new SuggestionService(repo, notes);

        // Three related notes fill each other's link budget first.
        var hub = await notes.CreateAsync("Hub alpha", "shared words here");
        var peer = await notes.CreateAsync("Peer beta", "shared words here");
        var other = await notes.CreateAsync("Peer gamma", "shared words here");
        var lonely = await notes.CreateAsync("Lonely delta", "shared words here");

        embeddings.Map("alpha", 1f, 0f);
        embeddings.Map("beta", 0.99f, 0.141f);
        embeddings.Map("gamma", 0.98f, 0.199f);
        embeddings.Map("delta", 0.9f, 0.436f);

        var batch = await engine.GenerateAsync();

        Assert.True(batch.LinkSuggestions > 0);
        Assert.Equal(1, batch.ConnectTheDots);
        Assert.Equal(4, batch.Clusters);

        var rescue = Assert.Single(repo.Suggestions, s => s.Kind == SuggestionKind.ConnectDots);
        var payload = SuggestionJson.Deserialize<LinkNotesPayload>(rescue.PayloadJson);

        Assert.NotNull(payload);
        Assert.Contains("no connections", payload!.Reason);
        Assert.Contains(lonely.Id, new[] { payload.SourceNoteId, payload.TargetNoteId });
        Assert.Empty(repo.Links);

        await suggestions.DecideAsync(rescue.Id, accept: true);

        var edge = Assert.Single(repo.Links, l => l.SourceId == lonely.Id || l.TargetId == lonely.Id);
        Assert.Equal(EdgeKind.AcceptedSuggestion, edge.Kind);
        Assert.Contains(repo.Audit, a => a.Action == "suggestion.accepted");
        Assert.DoesNotContain(repo.Suggestions, s => s.Kind == SuggestionKind.ConnectDots && s.IsPending);
    }

    [Fact]
    public async Task Two_separate_clusters_are_offered_a_bridge_instead_of_another_inner_link()
    {
        var repo = new FakeVaultRepository();
        var notes = new NoteService(repo);
        var embeddings = new ScriptedEmbeddings();
        var engine = new SuggestionEngine(repo, embeddings);
        var suggestions = new SuggestionService(repo, notes);

        var a = await notes.CreateAsync("Cluster note alpha", "shared words here");
        var b = await notes.CreateAsync("Cluster note beta", "shared words here");
        var c = await notes.CreateAsync("Cluster note gamma", "shared words here");
        var d = await notes.CreateAsync("Cluster note delta", "shared words here");

        embeddings.Map("alpha", 1f, 0f);
        embeddings.Map("beta", 1f, 0f);
        embeddings.Map("gamma", 0.9f, 0.43589f);
        embeddings.Map("delta", 0.9f, 0.43589f);

        await repo.AddLinkAsync(Link.Create(a.Id, b.Id, EdgeKind.Wiki));
        await repo.AddLinkAsync(Link.Create(c.Id, d.Id, EdgeKind.Wiki));

        var batch = await engine.GenerateAsync();

        Assert.Equal(2, batch.Clusters);
        Assert.Equal(0, batch.LinkSuggestions);
        Assert.Equal(2, batch.ConnectTheDots);

        var bridges = repo.Suggestions.Where(s => s.Kind == SuggestionKind.ConnectDots).ToList();
        Assert.All(bridges, s => Assert.Contains("separate clusters", s.PayloadJson));
        Assert.All(bridges, s => Assert.Contains("2 and 2 notes", s.PayloadJson));

        await suggestions.DecideAsync(bridges[0].Id, accept: true);

        Assert.Equal(3, repo.Links.Count);
        Assert.Contains(repo.Links, l => l.Kind == EdgeKind.AcceptedSuggestion);
    }

    [Fact]
    public void Components_group_linked_notes_and_leave_isolated_ones_alone()
    {
        var a = Note.Create("A");
        var b = Note.Create("B");
        var c = Note.Create("C");
        var lonely = Note.Create("Lonely");

        var components = GraphService.NoteComponents(
            new[] { a, b, c, lonely },
            new[] { Link.Create(a.Id, b.Id, EdgeKind.Wiki) });

        Assert.Equal(components[a.Id], components[b.Id]);
        Assert.NotEqual(components[a.Id], components[c.Id]);
        Assert.NotEqual(components[a.Id], components[lonely.Id]);
        Assert.NotEqual(components[c.Id], components[lonely.Id]);
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
