using Kampf.Application.Models;
using Kampf.Domain.Entities;
using Kampf.Infrastructure.Storage;
using Xunit;

namespace Kampf.Infrastructure.Tests;

public sealed class EmbeddingStoreTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteVaultRepository _repo;

    public EmbeddingStoreTests()
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

    [Fact]
    public async Task Vectors_round_trip_with_hash_and_model()
    {
        await _repo.InitializeAsync();

        var vector = new[] { 0.25f, -0.5f, 0.75f, 0f, 1f };
        await _repo.SaveEmbeddingsAsync(new[]
        {
            new NoteEmbedding("note-a", "kampf-hash-512", "ABC123", vector),
        });

        var loaded = await _repo.ListEmbeddingsAsync("kampf-hash-512");

        var embedding = Assert.Single(loaded);
        Assert.Equal("note-a", embedding.NoteId);
        Assert.Equal("ABC123", embedding.ContentHash);
        Assert.Equal(vector, embedding.Vector);
    }

    [Fact]
    public async Task A_different_model_supersedes_the_stored_vector()
    {
        await _repo.InitializeAsync();

        await _repo.SaveEmbeddingsAsync(new[]
        {
            new NoteEmbedding("note-a", "kampf-hash-512", "hash-1", new[] { 1f, 0f }),
        });
        await _repo.SaveEmbeddingsAsync(new[]
        {
            new NoteEmbedding("note-a", "ollama:nomic-embed-text", "hash-1", new[] { 0.6f, 0.8f }),
        });

        Assert.Empty(await _repo.ListEmbeddingsAsync("kampf-hash-512"));
        var current = Assert.Single(await _repo.ListEmbeddingsAsync("ollama:nomic-embed-text"));
        Assert.Equal(new[] { 0.6f, 0.8f }, current.Vector);
    }

    [Fact]
    public async Task Saving_again_updates_the_vector_in_place()
    {
        await _repo.InitializeAsync();

        await _repo.SaveEmbeddingsAsync(new[]
        {
            new NoteEmbedding("note-a", "kampf-hash-512", "hash-1", new[] { 1f, 0f }),
            new NoteEmbedding("note-b", "kampf-hash-512", "hash-2", new[] { 0f, 1f }),
        });
        await _repo.SaveEmbeddingsAsync(new[]
        {
            new NoteEmbedding("note-a", "kampf-hash-512", "hash-3", new[] { 0.5f, 0.5f }),
        });

        var all = await _repo.ListEmbeddingsAsync("kampf-hash-512");

        Assert.Equal(2, all.Count);
        var updated = Assert.Single(all, e => e.NoteId == "note-a");
        Assert.Equal("hash-3", updated.ContentHash);
        Assert.Equal(new[] { 0.5f, 0.5f }, updated.Vector);
    }

    [Fact]
    public async Task Deleting_a_note_removes_its_vector()
    {
        await _repo.InitializeAsync();

        var note = Note.Create("Doomed");
        await _repo.SaveNoteAsync(note);
        await _repo.SaveEmbeddingsAsync(new[]
        {
            new NoteEmbedding(note.Id, "kampf-hash-512", "hash", new[] { 1f }),
        });

        await _repo.DeleteNoteAsync(note.Id);

        Assert.Empty(await _repo.ListEmbeddingsAsync("kampf-hash-512"));
    }
}
