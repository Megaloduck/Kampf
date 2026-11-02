using Kampf.Domain.Entities;
using Kampf.Domain.ValueObjects;
using Kampf.Infrastructure.Storage;
using Xunit;

namespace Kampf.Infrastructure.Tests;

public sealed class VaultRepositoryTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteVaultRepository _repo;

    public VaultRepositoryTests()
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
    public async Task Note_round_trips_with_provenance()
    {
        await _repo.InitializeAsync();

        var note = Note.Create("Entropy in graphs", "body text");
        note.SourceDocumentId = "doc1";
        note.SourcePage = 12;

        await _repo.SaveNoteAsync(note);
        var loaded = await _repo.GetNoteAsync(note.Id);

        Assert.NotNull(loaded);
        Assert.Equal("Entropy in graphs", loaded!.Title);
        Assert.Equal(12, loaded.SourcePage);
        Assert.Equal("doc1", loaded.SourceDocumentId);
    }

    [Fact]
    public async Task Note_body_is_mirrored_to_a_markdown_file()
    {
        await _repo.InitializeAsync();

        var note = Note.Create("Mirror", "## heading\ncontent");
        await _repo.SaveNoteAsync(note);

        var path = Path.Combine(_root, "notes", $"{note.Id}.md");
        Assert.True(File.Exists(path));

        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("title: Mirror", text);
        Assert.Contains("content", text);
    }

    [Fact]
    public async Task Full_text_search_finds_saved_note()
    {
        await _repo.InitializeAsync();

        await _repo.SaveNoteAsync(Note.Create("Graph theory", "Community detection with modularity maximisation"));
        await _repo.SaveNoteAsync(Note.Create("Unrelated", "Cooking notes about bread"));

        var hits = await _repo.SearchAsync("modularity");

        Assert.Single(hits);
        Assert.Equal("Graph theory", hits[0].Title);
    }

    [Fact]
    public async Task Wiki_links_are_replaced_not_duplicated()
    {
        await _repo.InitializeAsync();

        await _repo.ReplaceWikiLinksAsync("source", new[] { "a", "b" });
        await _repo.ReplaceWikiLinksAsync("source", new[] { "b", "c" });

        var links = await _repo.ListLinksAsync();

        Assert.Equal(2, links.Count);
        Assert.All(links, l => Assert.Equal(EdgeKind.Wiki, l.Kind));
        Assert.Contains(links, l => l.TargetId == "b");
        Assert.Contains(links, l => l.TargetId == "c");
    }

    [Fact]
    public async Task Tags_attach_to_notes_and_are_deduplicated_by_name()
    {
        await _repo.InitializeAsync();

        var note = Note.Create("Tagged");
        await _repo.SaveNoteAsync(note);

        var a = await _repo.EnsureTagAsync("Methods");
        var b = await _repo.EnsureTagAsync("#methods");
        Assert.Equal(a.Id, b.Id);

        await _repo.SetNoteTagsAsync(note.Id, new[] { a.Id });

        var loaded = await _repo.GetNoteAsync(note.Id);
        Assert.Single(loaded!.Tags);
        Assert.Equal("methods", loaded.Tags[0].Name);
    }

    [Fact]
    public async Task Suggestions_round_trip_with_status_filter()
    {
        await _repo.InitializeAsync();

        var pending = Suggestion.Create(SuggestionKind.CreateMissingNote, "Create X", "{}");
        var rejected = Suggestion.Create(SuggestionKind.ApplyTag, "Tag Y", "{}");
        rejected.Reject();

        await _repo.SaveSuggestionAsync(pending);
        await _repo.SaveSuggestionAsync(rejected);

        var onlyPending = await _repo.ListSuggestionsAsync(SuggestionStatus.Pending);

        Assert.Single(onlyPending);
        Assert.Equal(SuggestionStatus.Pending, onlyPending[0].Status);
    }

    [Fact]
    public async Task Deleting_a_note_removes_its_links_and_markdown()
    {
        await _repo.InitializeAsync();

        var note = Note.Create("Doomed");
        await _repo.SaveNoteAsync(note);
        await _repo.ReplaceWikiLinksAsync(note.Id, new[] { "other" });

        await _repo.DeleteNoteAsync(note.Id);

        Assert.Null(await _repo.GetNoteAsync(note.Id));
        Assert.Empty(await _repo.ListLinksAsync());
        Assert.False(File.Exists(Path.Combine(_root, "notes", $"{note.Id}.md")));
    }
}
