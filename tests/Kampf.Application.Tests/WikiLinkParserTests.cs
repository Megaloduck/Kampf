using Kampf.Application.Markdown;
using Xunit;

namespace Kampf.Application.Tests;

public class WikiLinkParserTests
{
    [Fact]
    public void Parses_targets_and_aliases()
    {
        var links = WikiLinkParser.Parse("See [[Graph Theory]] and [[Entropy|entropy here]].");

        Assert.Equal(2, links.Count);
        Assert.Equal("Graph Theory", links[0].Target);
        Assert.Null(links[0].Alias);
        Assert.Equal("Entropy", links[1].Target);
        Assert.Equal("entropy here", links[1].Alias);
    }

    [Fact]
    public void Ignores_links_inside_fenced_code()
    {
        var markdown = """
            before [[Real Link]]
            ```csharp
            var x = [[NotALink]];
            ```
            after [[Second Link]]
            """;

        var targets = WikiLinkParser.ParseTargets(markdown);

        Assert.Equal(new[] { "Real Link", "Second Link" }, targets);
    }

    [Fact]
    public void Ignores_links_inside_inline_code()
    {
        var markdown = "use `[[syntax]]` here but [[Actual]] matters";

        var targets = WikiLinkParser.ParseTargets(markdown);

        Assert.Equal(new[] { "Actual" }, targets);
    }

    [Fact]
    public void Handles_unbalanced_and_empty_brackets()
    {
        var links = WikiLinkParser.Parse("[[unclosed and [[]] and [[ok]]");

        Assert.Single(links);
        Assert.Equal("ok", links[0].Target);
    }

    [Fact]
    public void Deduplicates_targets_case_insensitively()
    {
        var targets = WikiLinkParser.ParseTargets("[[Alpha]] [[alpha]] [[Beta]]");

        Assert.Equal(2, targets.Count);
    }
}
