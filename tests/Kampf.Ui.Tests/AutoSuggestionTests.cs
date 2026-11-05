using Avalonia.Headless.XUnit;
using Kampf.Application.Services;
using Kampf.Infrastructure;
using Kampf.ViewModels;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Kampf.Ui.Tests;

/// <summary>
/// Capturing a highlight should fill the review queue on its own now, while the
/// graph stays exactly as the researcher left it until something is accepted.
/// </summary>
public sealed class AutoSuggestionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "kampf-tests", Guid.NewGuid().ToString("n"));

    public AutoSuggestionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [AvaloniaFact]
    public async Task Capturing_a_highlight_queues_proposals_and_leaves_the_graph_alone()
    {
        var vault = await Vault.OpenAsync(Path.Combine(_root, "vault"));
        var notes = new NoteService(vault.Repository);

        await notes.CreateAsync(
            "Community detection",
            "modularity compares the observed edge density of a partition in citation networks");
        await notes.CreateAsync(
            "Edge density",
            "the observed edge density of a partition decides modularity in citation networks");
        await notes.CreateAsync(
            "Citation networks",
            "citation networks are partitioned by modularity and by their observed edge density");

        var importer = new ImportService(vault.Repository, vault.PdfExtractor, vault.Files, vault.Pages);
        await importer.ImportAsync(WritePdf());

        var viewModel = new MainViewModel(vault);
        await viewModel.RefreshCommand.ExecuteAsync(null);
        viewModel.SelectedDocument = Assert.Single(viewModel.Documents);

        var edgesBefore = (await vault.Repository.ListLinksAsync()).Count;

        await viewModel.CreateNoteFromHighlightAsync(
            1,
            "Modularity compares the observed edge density of a partition in citation networks.");

        Assert.NotEmpty(viewModel.Suggestions);
        Assert.Equal(edgesBefore, (await vault.Repository.ListLinksAsync()).Count);
        Assert.Contains("queued", viewModel.StatusMessage);
    }

    private string WritePdf()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(595, 842);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText(
            "Modularity compares the observed edge density of a partition.",
            12,
            new PdfPoint(50, 760),
            font);

        var path = Path.Combine(_root, "paper.pdf");
        File.WriteAllBytes(path, builder.Build());
        return path;
    }
}
