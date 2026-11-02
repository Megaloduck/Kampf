using Kampf.Domain.Entities;
using Kampf.Domain.ValueObjects;
using Xunit;

namespace Kampf.Domain.Tests;

public class DomainRuleTests
{
    [Fact]
    public void Suggestion_cannot_be_decided_twice()
    {
        var suggestion = Suggestion.Create(SuggestionKind.LinkNotes, "Link A and B", "{}");

        suggestion.Accept();

        Assert.Equal(SuggestionStatus.Accepted, suggestion.Status);
        Assert.NotNull(suggestion.DecidedAt);
        Assert.Throws<InvalidOperationException>(() => suggestion.Reject());
    }

    [Fact]
    public void Reject_marks_suggestion_rejected_and_keeps_it_out_of_pending_state()
    {
        var suggestion = Suggestion.Create(SuggestionKind.CreateMissingNote, "Create X", "{}");

        suggestion.Reject();

        Assert.False(suggestion.IsPending);
        Assert.Equal(SuggestionStatus.Rejected, suggestion.Status);
    }

    [Fact]
    public void Link_refuses_self_reference()
    {
        Assert.Throws<InvalidOperationException>(() => Link.Create("same", "same", EdgeKind.Manual));
    }

    [Fact]
    public void Note_gets_a_fallback_title()
    {
        Assert.Equal("Untitled note", Note.Create("   ").Title);
        Assert.Equal("Trimmed", Note.Create("  Trimmed  ").Title);
    }

    [Fact]
    public void Highlight_requires_a_one_based_page_and_text()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Highlight.Create("doc", 0, "text"));
        Assert.Throws<ArgumentException>(() => Highlight.Create("doc", 1, "   "));
    }

    [Fact]
    public void Tag_names_are_normalised()
    {
        Assert.Equal("network-science", Tag.Normalize("  #Network-Science "));
    }

    [Fact]
    public void Citation_formats_year_and_doi()
    {
        var citation = new Citation("A paper", "Doe, J.", 2021, "10.1000/xyz");

        var text = citation.Format();

        Assert.Contains("Doe, J. (2021)", text);
        Assert.Contains("https://doi.org/10.1000/xyz", text);
    }
}
