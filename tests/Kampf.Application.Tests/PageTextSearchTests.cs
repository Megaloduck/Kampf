using Kampf.Application.Services;
using Xunit;

namespace Kampf.Application.Tests;

/// <summary>
/// Highlight and citation marks are drawn from character offsets, so the quote
/// has to be found in exactly the text the vault stored.
/// </summary>
public sealed class PageTextSearchTests
{
    private const string Page =
        "Modularity maximisation compares the observed edge density\n" +
        "of a partition with the density expected from a null model.";

    [Fact]
    public void A_quote_is_found_at_its_offset()
    {
        var hit = PageTextSearch.Find(Page, "observed edge density");

        Assert.NotNull(hit);
        Assert.Equal(Page.IndexOf("observed edge density", StringComparison.Ordinal), hit!.Value.Start);
        Assert.Equal("observed edge density".Length, hit.Value.Length);
    }

    [Fact]
    public void A_quote_that_spans_a_line_break_is_flattened_to_match()
    {
        var hit = PageTextSearch.Find(Page, "density of a partition");

        Assert.NotNull(hit);
        Assert.Equal(Page.IndexOf("density", StringComparison.Ordinal), hit!.Value.Start);
        Assert.Equal("density of a partition".Length, hit.Value.Length);
    }

    [Fact]
    public void A_multi_line_quote_keeps_its_line_breaks()
    {
        var hit = PageTextSearch.Find(Page, "observed edge density\nof a partition");

        Assert.NotNull(hit);
        Assert.Equal(Page.IndexOf("observed", StringComparison.Ordinal), hit!.Value.Start);
    }

    [Fact]
    public void An_excerpt_cut_with_an_ellipsis_matches_its_prefix()
    {
        var hit = PageTextSearch.Find(Page, "Modularity maximisation compares the observed edge\u2026");

        Assert.NotNull(hit);
        Assert.Equal(0, hit!.Value.Start);
        Assert.True(hit.Value.Length >= 16);
    }

    [Fact]
    public void In_page_search_finds_every_occurrence_and_marks_whole_words()
    {
        const string page =
            "Density matters. Modularity compares EDGE density with a null model, \n" +
            "and edge density is what the reader searches for.";

        var hits = PageTextSearch.FindAll(page, "edge density");

        Assert.Equal(2, hits.Count);
        Assert.All(hits, hit => Assert.Equal("EDGE density", page.Substring(hit.Start, hit.Length),
            StringComparer.OrdinalIgnoreCase));
        Assert.Equal(page.IndexOf("EDGE", StringComparison.Ordinal), hits[0].Start);
        Assert.True(hits[1].Start > hits[0].Start);
    }

    [Fact]
    public void A_partial_word_hit_is_widened_to_the_word()
    {
        var hits = PageTextSearch.FindAll(Page, "served edge");

        var hit = Assert.Single(hits);
        Assert.Equal("observed edge", Page.Substring(hit.Start, hit.Length));
    }

    [Fact]
    public void Search_ignores_queries_too_short_to_be_useful()
    {
        Assert.Empty(PageTextSearch.FindAll(Page, "o"));
        Assert.Empty(PageTextSearch.FindAll(Page, "  "));
        Assert.Empty(PageTextSearch.FindAll(string.Empty, "density"));
    }

    [Fact]
    public void Text_that_is_not_on_the_page_is_not_marked()
    {
        Assert.Null(PageTextSearch.Find(Page, "a passage from another paper entirely"));
        Assert.Null(PageTextSearch.Find(Page, "   "));
        Assert.Null(PageTextSearch.Find(string.Empty, "anything"));
    }
}
