using Kampf.Application.Models;
using Xunit;

namespace Kampf.Application.Tests;

public class AnswerMarkupTests
{
    [Fact]
    public void Headings_bullets_and_citation_markers_become_structure()
    {
        var blocks = AnswerMarkup.Parse(
            "## Finding\n**Modularity** finds communities [2].\n- first point\n- second point");

        Assert.Equal(4, blocks.Count);
        Assert.Equal(AnswerBlockKind.Heading, blocks[0].Kind);
        Assert.Equal("Finding", string.Concat(blocks[0].Segments.Select(segment => segment.Text)));

        Assert.Equal(AnswerBlockKind.Paragraph, blocks[1].Kind);
        Assert.Contains(blocks[1].Segments,
            segment => segment.Kind == AnswerSegmentKind.Bold && segment.Text == "Modularity");
        Assert.Contains(blocks[1].Segments,
            segment => segment.IsCitation && segment.Citation == 2);

        Assert.Equal(AnswerBlockKind.Bullet, blocks[2].Kind);
        Assert.Equal("first point", string.Concat(blocks[2].Segments.Select(segment => segment.Text)));
        Assert.Equal(AnswerBlockKind.Bullet, blocks[3].Kind);
    }

    [Fact]
    public void Emphasis_becomes_runs_rather_than_asterisks()
    {
        var blocks = AnswerMarkup.Parse("a *muted* claim and a **strong** one");

        var paragraph = Assert.Single(blocks);
        var kinds = paragraph.Segments.Select(segment => segment.Kind).ToList();

        Assert.Contains(AnswerSegmentKind.Italic, kinds);
        Assert.Contains(AnswerSegmentKind.Bold, kinds);
        Assert.DoesNotContain("**", string.Concat(paragraph.Segments.Select(segment => segment.Text)));
    }

    [Fact]
    public void An_underscore_inside_a_word_is_not_emphasis()
    {
        var paragraph = Assert.Single(AnswerMarkup.Parse("the note_id column is left alone"));

        Assert.Equal("the note_id column is left alone",
            string.Concat(paragraph.Segments.Select(segment => segment.Text)));
        Assert.All(paragraph.Segments, segment => Assert.Equal(AnswerSegmentKind.Text, segment.Kind));
    }

    [Fact]
    public void Plain_prose_is_one_paragraph()
    {
        var paragraph = Assert.Single(AnswerMarkup.Parse("Modularity compares edge density."));

        Assert.Equal(AnswerBlockKind.Paragraph, paragraph.Kind);
        Assert.Equal("Modularity compares edge density.",
            string.Concat(paragraph.Segments.Select(segment => segment.Text)));
    }

    [Fact]
    public void An_empty_answer_produces_nothing()
    {
        Assert.Empty(AnswerMarkup.Parse(null));
        Assert.Empty(AnswerMarkup.Parse("   \n  "));
    }
}
