using Avalonia.Headless.XUnit;
using Kampf.Application.Services;
using Kampf.Domain.Entities;
using Kampf.Infrastructure;
using Kampf.ViewModels;
using Xunit;

namespace Kampf.Ui.Tests;

public sealed class SafetyFlowTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "kampf-tests", Guid.NewGuid().ToString("n"));

    public SafetyFlowTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best effort cleanup
        }
    }

    [AvaloniaFact]
    public async Task Deleting_a_folder_only_trashes_notes_filed_under_its_first_tag()
    {
        var vault = await Vault.OpenAsync(Path.Combine(_root, "vault"));
        var notes = new NoteService(vault.Repository);

        var analysis = await vault.Repository.EnsureTagAsync("analysis");
        var methods = await vault.Repository.EnsureTagAsync("methods");

        // Tags come back sorted by name, so "analysis" is the folder this note is
        // filed under even though it also carries "methods".
        var filedUnderMethods = Note.Create("Methods notes", "first tag methods");
        await notes.SaveAsync(filedUnderMethods);
        await vault.Repository.SetNoteTagsAsync(filedUnderMethods.Id, new[] { methods.Id });

        var alsoAnalysis = Note.Create("Analysis with a method", "second tag methods");
        await notes.SaveAsync(alsoAnalysis);
        await vault.Repository.SetNoteTagsAsync(alsoAnalysis.Id, new[] { analysis.Id, methods.Id });

        var viewModel = new MainViewModel(vault)
        {
            Confirm = _ => Task.FromResult(true),
        };
        await viewModel.RefreshCommand.ExecuteAsync(null);

        var folder = Assert.Single(viewModel.NavigatorFolders, f => f.Title == "methods");
        folder.DeleteFolderCommand!.Execute(null);

        await WaitUntilAsync(async () =>
            (await vault.Repository.ListTrashedNotesAsync()).Any(n => n.Id == filedUnderMethods.Id));

        var survivor = await vault.Repository.GetNoteAsync(alsoAnalysis.Id);
        Assert.NotNull(survivor);
        Assert.Null(survivor!.DeletedAt);
    }

    [AvaloniaFact]
    public async Task Permanent_delete_can_be_undone_during_its_window()
    {
        var vault = await Vault.OpenAsync(Path.Combine(_root, "vault"));
        var notes = new NoteService(vault.Repository);
        var note = await notes.CreateAsync("Delete me", "body");
        await notes.TrashAsync(note);

        var viewModel = new MainViewModel(vault)
        {
            Confirm = _ => Task.FromResult(true),
        };

        viewModel.ShowTrashCommand.Execute(null);
        await WaitUntilAsync(() => Task.FromResult(viewModel.TrashRows.Count == 1));

        viewModel.TrashRows[0].DeleteForeverCommand.Execute(null);

        await WaitUntilAsync(() => Task.FromResult(viewModel.HasStatusAction));
        Assert.Empty(viewModel.TrashRows);

        viewModel.StatusActionCommand!.Execute(null);

        await WaitUntilAsync(() => Task.FromResult(viewModel.TrashRows.Count == 1));
        var trashed = await vault.Repository.ListTrashedNotesAsync();
        Assert.Contains(trashed, n => n.Id == note.Id);
    }

    [AvaloniaFact]
    public async Task Save_for_close_writes_the_unsaved_editor_body()
    {
        var vault = await Vault.OpenAsync(Path.Combine(_root, "vault"));
        var notes = new NoteService(vault.Repository);
        var note = await notes.CreateAsync("Draft", "first version");

        var viewModel = new MainViewModel(vault);
        await viewModel.RefreshCommand.ExecuteAsync(null);
        viewModel.SelectedNote = Assert.Single(viewModel.Notes, n => n.Id == note.Id);

        await WaitUntilAsync(() => Task.FromResult(viewModel.EditorBody == "first version"));

        viewModel.EditorBody = "second version";
        Assert.True(viewModel.IsDirty);
        Assert.True(await viewModel.SaveForCloseAsync());

        var saved = await vault.Repository.GetNoteAsync(note.Id);
        Assert.Equal("second version", saved!.Body);
        Assert.False(viewModel.IsDirty);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await condition())
                return;

            await Task.Delay(20);
        }

        Assert.Fail("The expected state did not arrive in two seconds.");
    }
}
