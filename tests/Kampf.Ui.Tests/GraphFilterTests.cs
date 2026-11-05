using Avalonia.Headless.XUnit;
using Kampf.Application.Models;
using Kampf.Application.Services;
using Kampf.Domain.Entities;
using Kampf.Domain.ValueObjects;
using Kampf.Infrastructure;
using Kampf.ViewModels;
using Xunit;

namespace Kampf.Ui.Tests;

/// <summary>
/// The graph filters are view-model state, so they are exercised headless against
/// a real vault: a paper's year and authors narrow the papers and the notes taken
/// from them, and "unreviewed" keeps exactly what the queue is asking about.
/// </summary>
public sealed class GraphFilterTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "kampf-tests", Guid.NewGuid().ToString("n"));

    public GraphFilterTests() => Directory.CreateDirectory(_root);

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
    public async Task Year_author_and_unreviewed_filters_narrow_the_graph()
    {
        var vault = await Vault.OpenAsync(Path.Combine(_root, "vault"));
        var notes = new NoteService(vault.Repository);

        var oldPaper = PdfDocument.Create(Path.Combine(_root, "old.pdf"), "Old paper", 3);
        oldPaper.Year = 2001;
        oldPaper.Authors = "Ada Lovelace";
        await vault.Repository.SaveDocumentAsync(oldPaper);

        var newPaper = PdfDocument.Create(Path.Combine(_root, "new.pdf"), "New paper", 5);
        newPaper.Year = 2022;
        newPaper.Authors = "Grace Hopper";
        await vault.Repository.SaveDocumentAsync(newPaper);

        var oldNote = Note.Create("Old finding");
        oldNote.SourceDocumentId = oldPaper.Id;
        await notes.SaveAsync(oldNote);

        var newNote = Note.Create("New finding");
        newNote.SourceDocumentId = newPaper.Id;
        await notes.SaveAsync(newNote);

        var loose = await notes.CreateAsync("Loose thought", "something unrelated entirely");

        await vault.Repository.SaveSuggestionAsync(Suggestion.Create(
            SuggestionKind.LinkNotes,
            "Link loose to old",
            SuggestionJson.Serialize(new LinkNotesPayload(loose.Id, oldNote.Id, "test"))));

        var viewModel = new MainViewModel(vault);
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(5, viewModel.Graph!.Nodes.Count); // three notes and two papers

        Assert.Contains("2001", viewModel.YearFilters);
        Assert.Contains("Grace Hopper", viewModel.AuthorFilters);

        viewModel.YearFilter = "2001";
        Assert.Equal(
            new[] { "Old finding", "Old paper" },
            viewModel.Graph!.Nodes.Select(n => n.Label).OrderBy(l => l, StringComparer.Ordinal));

        viewModel.YearFilter = "All years";
        viewModel.AuthorFilter = "Grace Hopper";
        Assert.Equal(
            new[] { "New finding", "New paper" },
            viewModel.Graph!.Nodes.Select(n => n.Label).OrderBy(l => l, StringComparer.Ordinal));

        viewModel.AuthorFilter = "All authors";
        viewModel.UnreviewedOnly = true;
        Assert.Equal(
            new[] { "Loose thought", "Old finding" },
            viewModel.Graph!.Nodes.Select(n => n.Label).OrderBy(l => l, StringComparer.Ordinal));
    }
}
