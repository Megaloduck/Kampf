using Kampf.Application.Abstractions;
using Kampf.Application.Embeddings;
using Kampf.Application.Models;
using Kampf.Application.Services;
using Kampf.Domain.Entities;
using Xunit;

namespace Kampf.Application.Tests;

/// <summary>
/// Paper text in retrieval: a page of an imported PDF must be searchable on its
/// own, citable on its own, and cached so a big vault is not re-embedded.
/// </summary>
public class DocumentPageTests
{
    private sealed class FakeExtractor : IPdfExtractor
    {
        public FakeExtractor(params PdfPageText[] pages) => Pages = pages;

        public IReadOnlyList<PdfPageText> Pages { get; }

        public int Calls { get; private set; }

        public Task<PdfExtraction> ExtractAsync(string filePath, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new PdfExtraction(
                "Paper", "A. Author", 2024, null, Pages.Count, Pages));
        }
    }

    private static PdfDocument Paper() =>
        PdfDocument.Create(Path.Combine(Path.GetTempPath(), "kampf-missing.pdf"), "Community detection", 3);

    private static readonly PdfPageText Page4 = new(
        4,
        "Modularity maximisation finds communities in citation networks. " +
        "It compares the observed edge density with a random null model.");

    private static DocumentPageIndex Index(FakeVaultRepository repo, FakeExtractor? extractor = null) =>
        new(repo, extractor ?? new FakeExtractor(Page4), new HashingEmbeddingProvider());

    /// <summary>A vault with one paper whose page text and vectors are stored.</summary>
    private static async Task<(FakeVaultRepository Repo, PdfDocument Document, SearchService Search)> IndexedAsync()
    {
        var repo = new FakeVaultRepository();
        var document = Paper();
        repo.Documents[document.Id] = document;
        await repo.SaveDocumentPagesAsync(new[] { new DocumentPage(document.Id, 4, Page4.Text) });

        var index = Index(repo);
        await index.IndexAsync(new[] { document });

        var search = new SearchService(
            repo,
            new NoteEmbeddingIndex(repo, new HashingEmbeddingProvider()),
            index);

        return (repo, document, search);
    }

    [Fact]
    public async Task Page_text_is_stored_once_per_paper()
    {
        var repo = new FakeVaultRepository();
        var document = Paper();
        repo.Documents[document.Id] = document;

        var index = Index(repo);
        Assert.True(await index.EnsureTextAsync(document, new[] { Page4 }));
        Assert.False(await index.EnsureTextAsync(document, new[] { Page4 }));

        var stored = Assert.Single(await repo.ListDocumentPagesAsync(document.Id));
        Assert.Equal(4, stored.Page);
        Assert.Contains("Modularity maximisation", stored.Text);
    }

    [Fact]
    public async Task A_paper_page_is_found_by_its_body_text()
    {
        var repo = new FakeVaultRepository();
        var document = Paper();
        repo.Documents[document.Id] = document;
        await repo.SaveDocumentPagesAsync(new[] { new DocumentPage(document.Id, 4, Page4.Text) });

        var search = new SearchService(repo, new NoteEmbeddingIndex(repo, new HashingEmbeddingProvider()), Index(repo));
        var outcome = await search.SearchAsync("modularity maximisation");

        var hit = Assert.Single(outcome.Hits);
        Assert.Equal("document", hit.Kind);
        Assert.Equal(document.Id, hit.Id);
        Assert.Equal(4, hit.Page);
        Assert.Contains("p. 4", hit.Snippet);
    }

    [Fact]
    public async Task Page_vectors_are_cached_and_only_changed_pages_are_re_embedded()
    {
        var repo = new FakeVaultRepository();
        var document = Paper();
        repo.Documents[document.Id] = document;
        await repo.SaveDocumentPagesAsync(new[] { new DocumentPage(document.Id, 4, Page4.Text) });

        var index = Index(repo);

        Assert.Equal(1, await index.IndexAsync(new[] { document }));
        Assert.Equal(0, await index.IndexAsync(new[] { document }));

        // New text on the same page invalidates only that page's vector.
        await repo.SaveDocumentPagesAsync(new[]
        {
            new DocumentPage(document.Id, 4, Page4.Text + " Revised in a later edition."),
        });

        Assert.Equal(1, await index.IndexAsync(new[] { document }));
    }

    [Fact]
    public async Task A_page_of_a_paper_is_cited_even_when_no_note_was_written_about_it()
    {
        var (repo, document, search) = await IndexedAsync();
        var chat = new ChatService(repo, search, null);

        var answer = await chat.AskAsync("modularity citation networks");

        Assert.True(answer.IsGrounded);
        var citation = Assert.Single(answer.Citations);
        Assert.True(citation.IsPage);
        Assert.Equal(document.Id, citation.DocumentId);
        Assert.Equal(4, citation.Page);
        Assert.Equal(string.Empty, citation.NoteId);
        Assert.Contains("p. 4", citation.Label);
    }

    [Fact]
    public async Task A_follow_up_question_sees_the_previous_turn_as_context()
    {
        var (repo, _, search) = await IndexedAsync();
        var model = new RecordingChatModel();
        var chat = new ChatService(repo, search, model);

        await chat.AskAsync("modularity citation networks");
        await chat.AskAsync("and how is modularity computed?");

        Assert.Contains("Earlier in this conversation", model.LastPrompt);
        Assert.Contains("modularity citation networks", model.LastPrompt);
        Assert.Contains("Question: and how is modularity computed?", model.LastPrompt);
    }

    [Fact]
    public async Task Streaming_yields_the_answer_in_pieces_and_keeps_the_citation()
    {
        var (repo, _, search) = await IndexedAsync();
        var chat = new ChatService(repo, search, new RecordingChatModel());

        var deltas = new List<string>();
        var answer = await chat.AskStreamingAsync("modularity", delta =>
        {
            deltas.Add(delta);
            return Task.CompletedTask;
        });

        Assert.True(deltas.Count > 1);
        Assert.Equal(answer.Text, string.Concat(deltas));
        Assert.Contains("[1]", answer.Text);
        Assert.Single(answer.Citations);
    }

    private sealed class RecordingChatModel : IChatModel
    {
        private const string Reply = "Modularity compares edge density with a null model [1].";
        private const string Streamed = "Modularity compares edge density with a null model [1].";

        public string Name => "ollama:recording";

        public string? LastPrompt { get; private set; }

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            LastPrompt = userPrompt;
            return Task.FromResult(Reply);
        }

        public async IAsyncEnumerable<string> StreamAsync(
            string systemPrompt,
            string userPrompt,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            LastPrompt = userPrompt;
            var middle = Streamed.Length / 3;

            yield return Streamed[..middle];
            await Task.Yield();
            yield return Streamed[middle..(middle * 2)];
            await Task.Yield();
            yield return Streamed[(middle * 2)..];
        }
    }
}
