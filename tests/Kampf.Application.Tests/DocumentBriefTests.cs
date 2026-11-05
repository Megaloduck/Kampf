using Kampf.Application.Abstractions;
using Kampf.Application.Services;
using Kampf.Domain.Entities;
using Xunit;

namespace Kampf.Application.Tests;

public class DocumentBriefTests
{
    private sealed class RecordingModel : IChatModel
    {
        public string Name => "ollama:fake";

        public string? LastSystem { get; private set; }
        public string? LastPrompt { get; private set; }

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            LastSystem = systemPrompt;
            LastPrompt = userPrompt;
            return Task.FromResult("  - The question is community detection.\n  - Only an abstract was supplied.  ");
        }

        public async IAsyncEnumerable<string> StreamAsync(
            string systemPrompt,
            string userPrompt,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return await CompleteAsync(systemPrompt, userPrompt, ct);
        }
    }

    private static PdfDocument Paper()
    {
        var paper = PdfDocument.Create(Path.Combine("assets", "methods.pdf"), "Community detection methods", 12);
        paper.Authors = "Ada Lovelace";
        paper.Year = 2001;
        paper.Abstract = "We compare modularity maximisation against label propagation.";
        return paper;
    }

    [Fact]
    public async Task A_brief_is_written_from_the_abstract_and_page_text()
    {
        var model = new RecordingModel();

        var brief = await new DocumentBriefService(model).BriefAsync(
            Paper(),
            new[] { "Page one text about modularity.", "Page two text about benchmarks." });

        Assert.StartsWith("- The question", brief);
        Assert.EndsWith("supplied.", brief);
        Assert.DoesNotContain(" ", brief[..1]); // trimmed

        Assert.Contains("Title: Community detection methods", model.LastPrompt);
        Assert.Contains("Authors: Ada Lovelace", model.LastPrompt);
        Assert.Contains("We compare modularity maximisation", model.LastPrompt);
        Assert.Contains("Page two text about benchmarks.", model.LastPrompt);
        Assert.Contains("five short bullets", model.LastSystem);
    }

    [Fact]
    public async Task The_prompt_is_capped_so_a_whole_paper_is_never_sent()
    {
        var model = new RecordingModel();
        var pages = Enumerable.Range(1, 60)
            .Select(i => $"page {i} " + new string('x', 500))
            .ToList();

        await new DocumentBriefService(model).BriefAsync(Paper(), pages);

        Assert.True(model.LastPrompt!.Length < 9500, $"prompt was {model.LastPrompt.Length} chars");
        Assert.DoesNotContain("page 60", model.LastPrompt);
    }

    [Fact]
    public async Task Without_a_local_model_a_brief_is_refused_rather_than_invented()
    {
        var service = new DocumentBriefService();

        Assert.False(service.HasLocalModel);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.BriefAsync(Paper(), new[] { "text" }));

        Assert.Contains("Settings", error.Message);
    }
}
