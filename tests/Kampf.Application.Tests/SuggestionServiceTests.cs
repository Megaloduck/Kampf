using Kampf.Application.Services;
using Kampf.Domain.Entities;
using Kampf.Domain.ValueObjects;
using Xunit;

namespace Kampf.Application.Tests;

public class SuggestionServiceTests
{
    private static (NoteService Notes, SuggestionService Suggestions, FakeVaultRepository Repo) Create()
    {
        var repo = new FakeVaultRepository();
        var notes = new NoteService(repo);
        return (notes, new SuggestionService(repo, notes), repo);
    }

    [Fact]
    public async Task Accepting_a_link_after_its_target_was_deleted_drops_it_instead_of_writing_a_dangling_edge()
    {
        var (notes, suggestions, repo) = Create();
        var source = await notes.CreateAsync("Graph Theory");
        var target = await notes.CreateAsync("Modularity");

        var queued = await suggestions.ProposeLinkAsync(source.Id, target.Id, "80% similar");
        await repo.DeleteNoteAsync(target.Id);

        var decision = await suggestions.DecideAsync(queued.Id, accept: true);

        Assert.True(decision.IsStale);
        Assert.NotNull(decision.StaleReason);
        Assert.Null(decision.CreatedNoteId);
        Assert.Empty(repo.Links);
        Assert.Equal(SuggestionStatus.Rejected, decision.Suggestion.Status);
        Assert.Contains(repo.Audit, a => a.Action == "suggestion.stale");
        Assert.DoesNotContain(repo.Audit, a => a.Action == "suggestion.accepted");
        Assert.Empty(await suggestions.ListPendingAsync());
    }

    [Fact]
    public async Task Accepting_a_link_that_is_already_in_the_graph_records_it_without_claiming_a_change()
    {
        var (notes, suggestions, repo) = Create();
        var source = await notes.CreateAsync("Graph Theory");
        var target = await notes.CreateAsync("Modularity");

        var queued = await suggestions.ProposeLinkAsync(source.Id, target.Id, "80% similar");
        await repo.AddLinkAsync(
            Link.Create(source.Id, target.Id, EdgeKind.AcceptedSuggestion, "linked by hand"));

        var decision = await suggestions.DecideAsync(queued.Id, accept: true);

        Assert.True(decision.AlreadyApplied);
        Assert.False(decision.IsStale);
        Assert.Single(repo.Links);
        Assert.Equal(SuggestionStatus.Accepted, decision.Suggestion.Status);

        var audit = Assert.Single(repo.Audit.Where(a => a.Action == "suggestion.accepted"));
        Assert.Contains("already present", audit.Detail);
    }

    [Fact]
    public async Task Queueing_the_same_pair_again_with_other_wording_or_direction_is_refused()
    {
        var (notes, suggestions, repo) = Create();
        var source = await notes.CreateAsync("Graph Theory");
        var target = await notes.CreateAsync("Modularity");

        await suggestions.ProposeLinkAsync(source.Id, target.Id, "80% similar \u00b7 shares \u201cgraph\u201d");

        var again = await Assert.ThrowsAsync<InvalidOperationException>(
            () => suggestions.ProposeLinkAsync(target.Id, source.Id, "85% similar \u00b7 shares \u201cmodularity\u201d"));

        Assert.Equal("That link suggestion is already queued.", again.Message);
        Assert.Single(repo.Suggestions);
    }
}
