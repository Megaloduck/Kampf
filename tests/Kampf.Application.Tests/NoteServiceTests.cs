using Kampf.Application.Models;
using Kampf.Application.Services;
using Kampf.Domain.Entities;
using Kampf.Domain.ValueObjects;
using Xunit;

namespace Kampf.Application.Tests;

public class NoteServiceTests
{
    private static (NoteService Notes, SuggestionService Suggestions, FakeVaultRepository Repo) Create()
    {
        var repo = new FakeVaultRepository();
        var notes = new NoteService(repo);
        return (notes, new SuggestionService(repo, notes), repo);
    }

    [Fact]
    public async Task Saving_a_note_links_existing_notes_by_title()
    {
        var (notes, _, repo) = Create();
        await notes.CreateAsync("Graph Theory");
        await notes.CreateAsync("Modularity");

        var note = await notes.CreateAsync("PageRank", "Builds on [[Graph Theory]] and [[modularity]].");

        var links = repo.Links.Where(l => l.SourceId == note.Id).ToList();
        Assert.Equal(2, links.Count);
        Assert.All(links, l => Assert.Equal(EdgeKind.Wiki, l.Kind));
    }

    [Fact]
    public async Task Unresolved_wiki_link_queues_a_review_suggestion_instead_of_creating_a_note()
    {
        var (notes, _, repo) = Create();

        await notes.CreateAsync("PageRank", "Relates to [[Spectral Graph Theory]].");

        var suggestion = Assert.Single(repo.Suggestions);
        Assert.Equal(SuggestionKind.CreateMissingNote, suggestion.Kind);
        Assert.Equal(SuggestionStatus.Pending, suggestion.Status);
        Assert.Single(repo.Notes); // nothing auto-created

        var payload = SuggestionJson.Deserialize<CreateMissingNotePayload>(suggestion.PayloadJson);
        Assert.Equal("Spectral Graph Theory", payload!.TargetTitle);
    }

    [Fact]
    public async Task Repeated_saves_do_not_duplicate_missing_note_suggestions()
    {
        var (notes, _, repo) = Create();
        var note = await notes.CreateAsync("PageRank", "[[Missing Idea]]");

        await notes.SaveAsync(note);
        await notes.SaveAsync(note);

        Assert.Single(repo.Suggestions);
    }

    [Fact]
    public async Task Accepting_a_missing_note_suggestion_creates_note_and_edge()
    {
        var (notes, suggestions, repo) = Create();
        var source = await notes.CreateAsync("PageRank", "Built on [[Spectral Theory]].");

        var pending = await suggestions.ListPendingAsync();
        var decision = await suggestions.DecideAsync(pending[0].Id, accept: true);

        Assert.NotNull(decision.CreatedNoteId);
        Assert.Equal(2, repo.Notes.Count);
        Assert.Contains(repo.Links, l => l.SourceId == source.Id && l.TargetId == decision.CreatedNoteId);
        Assert.Contains(repo.Audit, a => a.Action == "suggestion.accepted");
    }

    [Fact]
    public async Task Rejecting_a_suggestion_changes_nothing_in_the_graph()
    {
        var (notes, suggestions, repo) = Create();
        await notes.CreateAsync("PageRank", "Built on [[Spectral Theory]].");
        var linksBefore = repo.Links.Count;

        var pending = await suggestions.ListPendingAsync();
        await suggestions.DecideAsync(pending[0].Id, accept: false);

        Assert.Equal(linksBefore, repo.Links.Count);
        Assert.Single(repo.Notes);
        Assert.Contains(repo.Audit, a => a.Action == "suggestion.rejected");
        Assert.Empty(await suggestions.ListPendingAsync());
    }

    [Fact]
    public async Task Highlight_to_note_keeps_document_and_page_provenance()
    {
        var (notes, _, repo) = Create();
        var document = PdfDocument.Create("/vault/assets/paper.pdf", "A Paper", 12);
        document.Year = 2020;
        await repo.SaveDocumentAsync(document);

        var note = await notes.CreateFromHighlightAsync(document, 7, "Modularity measures community structure.");

        Assert.Equal(document.Id, note.SourceDocumentId);
        Assert.Equal(7, note.SourcePage);
        Assert.Contains("p. 7", note.Body);

        var highlight = Assert.Single(repo.Highlights);
        Assert.Equal(note.Id, highlight.NoteId);
        Assert.Equal(7, highlight.Page);
    }

    [Fact]
    public async Task Setting_tags_creates_missing_tags()
    {
        var (notes, _, repo) = Create();
        var note = await notes.CreateAsync("Tagged note");

        await notes.SetTagsAsync(note, new[] { "Methods", "#methods", "Graphs" });

        Assert.Equal(2, repo.Tags.Count);
        Assert.Equal(2, repo.NoteTags[note.Id].Count);
    }

    [Fact]
    public async Task Link_suggestion_requires_a_pending_payload_and_creates_edge_on_accept()
    {
        var (notes, suggestions, repo) = Create();
        var a = await notes.CreateAsync("A");
        var b = await notes.CreateAsync("B");

        await suggestions.ProposeLinkAsync(a.Id, b.Id, "shared vocabulary");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            suggestions.ProposeLinkAsync(a.Id, b.Id, "shared vocabulary"));

        var pending = await suggestions.ListPendingAsync();
        await suggestions.DecideAsync(pending[0].Id, accept: true);

        Assert.Contains(repo.Links, l => l.Kind == EdgeKind.AcceptedSuggestion);
    }
}
