using Kampf.Application.Services;
using Xunit;

namespace Kampf.Application.Tests;

public class CitationServiceTests
{
    [Theory]
    [InlineData("10.1038/nature14539", "10.1038/nature14539")]
    [InlineData("https://doi.org/10.1038/nature14539", "10.1038/nature14539")]
    [InlineData("doi:10.1038/nature14539.", "10.1038/nature14539")]
    [InlineData("not a doi", null)]
    [InlineData("", null)]
    public void Normalizes_doi_forms(string input, string? expected)
    {
        Assert.Equal(expected, CitationService.NormalizeDoi(input));
    }

    [Fact]
    public void Finds_doi_inside_page_text()
    {
        var text = "Published in Nature. https://doi.org/10.1038/s41586-021-03819-2 \u00a9 authors";

        Assert.Equal("10.1038/s41586-021-03819-2", CitationService.FindDoi(text));
    }

    [Fact]
    public void Finds_year_in_metadata_date()
    {
        Assert.Equal(2019, CitationService.FindYear("D:20190314120000+01'00'"));
        Assert.Null(CitationService.FindYear("unknown"));
    }
}
