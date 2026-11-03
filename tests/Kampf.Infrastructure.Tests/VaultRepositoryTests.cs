using Kampf.Domain.Entities;
using Microsoft.Data.Sqlite;
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
    public async Task Chat_history_round_trips_in_order()
    {
        await _repo.InitializeAsync();

        var question = ChatMessage.Create(ChatMessage.UserRole, "What connects X and Y?");
        var answer = ChatMessage.Create(
            ChatMessage.AssistantRole,
            "Both notes discuss modularity [1].",
            "[{\"noteId\":\"n1\",\"noteTitle\":\"X\"}]");

        await _repo.SaveChatMessageAsync(question);
        await _repo.SaveChatMessageAsync(answer);

        var messages = await _repo.ListChatMessagesAsync();

        Assert.Equal(2, messages.Count);
        Assert.True(messages[0].IsUser);
        Assert.Equal("What connects X and Y?", messages[0].Text);
        Assert.Equal("Both notes discuss modularity [1].", messages[1].Text);
        Assert.Contains("\"noteId\":\"n1\"", messages[1].CitationsJson);
    }

    [Fact]
    public async Task Document_citation_detail_round_trips()
    {
        await _repo.InitializeAsync();

        var document = PdfDocument.Create(Path.Combine(_root, "paper.pdf"), "A Paper", 12);
        document.Authors = "Wang; Huang";
        document.Year = 2023;
        document.Doi = "10.1007/s11423-023-10247-8";
        document.ItemType = "Journal article";
        document.Abstract = "An abstract from the XMP packet.";
        document.Publication = "Educational technology research and development";
        document.Volume = "71";
        document.Issue = "4";
        document.PageRange = "1547-1565";
        document.Issn = "1042-1629";
        document.Url = "https://example.org/paper";
        document.Keywords = "games, learning";
        document.Language = "en";

        await _repo.SaveDocumentAsync(document);
        var loaded = await _repo.GetDocumentAsync(document.Id);

        Assert.NotNull(loaded);
        Assert.Equal("Journal article", loaded!.ItemType);
        Assert.Equal("Educational technology research and development", loaded.Publication);
        Assert.Equal("71", loaded.Volume);
        Assert.Equal("4", loaded.Issue);
        Assert.Equal("1547-1565", loaded.PageRange);
        Assert.Equal("1042-1629", loaded.Issn);
        Assert.Equal("https://example.org/paper", loaded.Url);
        Assert.Equal("games, learning", loaded.Keywords);
        Assert.Equal("en", loaded.Language);
        Assert.Equal("An abstract from the XMP packet.", loaded.Abstract);
    }

    [Fact]
    public async Task A_vault_from_before_the_detail_columns_gains_them_on_open()
    {
        var database = Path.Combine(_root, "kampf.db");
        await using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE documents (
                    id          TEXT PRIMARY KEY,
                    file_path   TEXT NOT NULL,
                    file_name   TEXT NOT NULL,
                    title       TEXT NOT NULL,
                    authors     TEXT NOT NULL,
                    year        INTEGER NULL,
                    doi         TEXT NULL,
                    page_count  INTEGER NOT NULL,
                    imported_at TEXT NOT NULL
                );
                INSERT INTO documents VALUES
                    ('old', '/vault/assets/old.pdf', 'old.pdf', 'Old paper', 'Someone', 2001, NULL, 3,
                     '2024-01-01T00:00:00.0000000+00:00');
                """;
            await command.ExecuteNonQueryAsync();
        }

        await _repo.InitializeAsync();

        var loaded = await _repo.GetDocumentAsync("old");

        Assert.NotNull(loaded);
        Assert.Equal("Old paper", loaded!.Title);
        Assert.Equal(3, loaded.PageCount);
        Assert.Null(loaded.Publication);
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
