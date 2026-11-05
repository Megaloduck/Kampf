using Kampf.Application.Models;
using Kampf.Domain.Entities;
using Kampf.Infrastructure.Storage;
using Xunit;

namespace Kampf.Infrastructure.Tests;

/// <summary>
/// The stored paper text: written on import, searched by page, removed with the
/// document. These run against the real SQLite/FTS5 schema, which the in-memory
/// fakes cannot check.
/// </summary>
public sealed class DocumentPageStoreTests : IDisposable
{
    private static readonly string Page4 =
        "Modularity maximisation finds communities in citation networks and compares " +
        "observed edge density with a random null model.";

    private static readonly string Page5 =
        "Entropy of a graph partition is computed from the degree sequence of each block.";

    private readonly string _root;
    private readonly SqliteVaultRepository _repo;

    public DocumentPageStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kampf-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
        _repo = new SqliteVaultRepository(
            Path.Combine(_root, "kampf.db"),
            Path.Combine(_root, "notes"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best effort cleanup
        }
    }

    private async Task<PdfDocument> SeedDocumentAsync()
    {
        await _repo.InitializeAsync();
        var document = PdfDocument.Create(Path.Combine(_root, "assets", "methods.pdf"), "Community detection", 5);
        await _repo.SaveDocumentAsync(document);
        return document;
    }

    [Fact]
    public async Task Page_text_round_trips_in_page_order()
    {
        var document = await SeedDocumentAsync();

        await _repo.SaveDocumentPagesAsync(new[]
        {
            new DocumentPage(document.Id, 5, Page5),
            new DocumentPage(document.Id, 4, Page4),
        });

        var pages = await _repo.ListDocumentPagesAsync(document.Id);

        Assert.Equal(new[] { 4, 5 }, pages.Select(p => p.Page));
        Assert.Contains("Modularity maximisation", pages[0].Text);
    }

    [Fact]
    public async Task Full_text_search_over_pages_names_the_page()
    {
        var document = await SeedDocumentAsync();
        await _repo.SaveDocumentPagesAsync(new[]
        {
            new DocumentPage(document.Id, 4, Page4),
            new DocumentPage(document.Id, 5, Page5),
        });

        var hits = await _repo.SearchPagesAsync("entropy partition");

        var hit = Assert.Single(hits);
        Assert.Equal("document", hit.Kind);
        Assert.Equal(document.Id, hit.Id);
        Assert.Equal("Community detection", hit.Title);
        Assert.Equal(5, hit.Page);
        Assert.Contains("p. 5", hit.Snippet);
    }

    [Fact]
    public async Task Saving_pages_again_replaces_the_search_index()
    {
        var document = await SeedDocumentAsync();
        await _repo.SaveDocumentPagesAsync(new[] { new DocumentPage(document.Id, 4, Page4) });

        await _repo.SaveDocumentPagesAsync(new[] { new DocumentPage(document.Id, 4, Page5) });

        Assert.Empty(await _repo.SearchPagesAsync("modularity"));
        Assert.Single(await _repo.SearchPagesAsync("entropy"));
        Assert.Single(await _repo.ListDocumentPagesAsync(document.Id));
    }

    [Fact]
    public async Task Papers_without_page_text_are_listed_for_backfill()
    {
        var document = await SeedDocumentAsync();
        Assert.Single(await _repo.ListDocumentIdsWithoutPagesAsync());

        await _repo.SaveDocumentPagesAsync(new[] { new DocumentPage(document.Id, 4, Page4) });

        Assert.Empty(await _repo.ListDocumentIdsWithoutPagesAsync());
    }

    [Fact]
    public async Task Deleting_a_paper_removes_its_pages_vectors_and_search_rows()
    {
        var document = await SeedDocumentAsync();
        await _repo.SaveDocumentPagesAsync(new[]
        {
            new DocumentPage(document.Id, 4, Page4),
            new DocumentPage(document.Id, 5, Page5),
        });
        await _repo.SavePageEmbeddingsAsync(new[]
        {
            new PageEmbedding(document.Id, 4, "kampf-hash-512", "H1", new[] { 1f, 0f }),
            new PageEmbedding(document.Id, 5, "kampf-hash-512", "H2", new[] { 0f, 1f }),
        });

        await _repo.DeleteDocumentAsync(document.Id);

        Assert.Empty(await _repo.ListDocumentPagesAsync(document.Id));
        Assert.Empty(await _repo.ListPageEmbeddingsAsync("kampf-hash-512"));
        Assert.Empty(await _repo.SearchPagesAsync("modularity"));
    }

    [Fact]
    public async Task Page_vectors_are_tagged_with_the_model_that_made_them()
    {
        var document = await SeedDocumentAsync();

        await _repo.SavePageEmbeddingsAsync(new[]
        {
            new PageEmbedding(document.Id, 4, "kampf-hash-512", "H1", new[] { 0.5f, 0.5f }),
        });

        var current = Assert.Single(await _repo.ListPageEmbeddingsAsync("kampf-hash-512"));
        Assert.Equal(4, current.Page);
        Assert.Equal("H1", current.ContentHash);
        Assert.Equal(new[] { 0.5f, 0.5f }, current.Vector);

        Assert.Empty(await _repo.ListPageEmbeddingsAsync("ollama:nomic-embed-text"));
    }
}
