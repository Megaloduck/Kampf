using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Kampf.Application.Models;
using Kampf.Application.Services;
using Kampf.Controls;
using Kampf.Domain.Entities;
using Kampf.Domain.ValueObjects;
using Kampf.Infrastructure;
using Kampf.Infrastructure.Ai;
using Kampf.ViewModels;
using Kampf.Views;
using Xunit;

namespace Kampf.Ui.Tests;

public class AnswerSurfaceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "kampf-tests", Guid.NewGuid().ToString("n"));

    public AnswerSurfaceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
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
    public void An_answer_renders_headings_bullets_and_citation_markers()
    {
        var answer = new AnswerText
        {
            Answer = "## Finding\n**Modularity** finds communities [1].\n- a point",
            Citations = new List<ChatCitationViewModel>
            {
                new(
                    new ChatCitation("note-1", "Modularity", null, null, "excerpt", Number: 1),
                    _ => Task.CompletedTask),
            },
        };

        Assert.Equal(3, answer.Children.Count);

        var heading = Assert.IsType<TextBlock>(answer.Children[0]);
        Assert.Equal(FontWeight.SemiBold, heading.FontWeight);
        Assert.DoesNotContain("#", heading.Inlines!.OfType<Run>().Select(run => run.Text));

        var paragraph = Assert.IsType<TextBlock>(answer.Children[1]);
        var runs = paragraph.Inlines!.OfType<Run>().ToList();

        // The marker survives as its own run, so it can be clicked; the markdown
        // that produced the emphasis does not survive at all.
        Assert.Contains(runs, run => run.Text == "[1]");
        Assert.Contains(runs, run => run.Text == "Modularity" && run.FontWeight == FontWeight.SemiBold);
        Assert.DoesNotContain(runs, run => run.Text.Contains("**"));

        var bullet = Assert.IsType<Grid>(answer.Children[2]);
        Assert.Equal(2, bullet.Children.Count);
    }

    [AvaloniaFact]
    public async Task A_model_chosen_in_settings_is_applied_without_a_restart()
    {
        var vault = await Vault.OpenAsync(Path.Combine(_root, "vault"));
        var viewModel = new MainViewModel(vault);

        viewModel.ChatServerUrl = "http://localhost:1234/v1";
        viewModel.ChatModelName = "ornith-1.0-9b";
        viewModel.ApplyChatModelCommand.Execute(null);

        Assert.Equal("openai-compatible:ornith-1.0-9b", viewModel.ChatModelLabel);
        Assert.Equal("openai-compatible:ornith-1.0-9b", vault.Chat?.Name);
        Assert.Equal("ornith-1.0-9b", ChatModelSettings.Load(vault.RootPath).Model);

        // Reopening the vault finds the saved model, with no environment involved.
        var reopened = await Vault.OpenAsync(Path.Combine(_root, "vault"));
        Assert.Equal("openai-compatible:ornith-1.0-9b", reopened.Chat?.Name);

        viewModel.ClearChatModelCommand.Execute(null);

        Assert.Contains("no local model", viewModel.ChatModelLabel);
        Assert.False(File.Exists(ChatModelSettings.PathFor(vault.RootPath)));
    }

    [AvaloniaFact]
    public async Task An_unusable_server_address_is_refused_rather_than_saved()
    {
        var vault = await Vault.OpenAsync(Path.Combine(_root, "vault"));
        var viewModel = new MainViewModel(vault);

        viewModel.ChatServerUrl = "not a url";
        viewModel.ChatModelName = "ornith-1.0-9b";
        viewModel.ApplyChatModelCommand.Execute(null);

        Assert.Contains("not an address", viewModel.ChatModelHealth);
        Assert.Contains("no local model", viewModel.ChatModelLabel);
        Assert.False(File.Exists(ChatModelSettings.PathFor(vault.RootPath)));
    }

    [AvaloniaFact]
    public async Task Asking_a_linking_question_lands_in_the_chat_tab_with_sources_and_follow_ups()
    {
        var vault = await Vault.OpenAsync(Path.Combine(_root, "vault"));
        var notes = new NoteService(vault.Repository);

        var first = await notes.CreateAsync(
            "Modularity",
            "modularity compares the observed edge density of a partition with the expected density");
        var second = await notes.CreateAsync(
            "Homophily",
            "homophily measures how strongly nodes attach to nodes that resemble them");

        await vault.Repository.AddLinkAsync(
            Link.Create(first.Id, second.Id, EdgeKind.Manual, "both partition the same citation network"));

        var viewModel = new MainViewModel(vault);
        await viewModel.RefreshCommand.ExecuteAsync(null);

        viewModel.View = VaultView.Chat;
        viewModel.ChatQuestion = "What connects modularity and homophily?";
        await viewModel.AskIdeaCraftCommand.ExecuteAsync(null);

        var exchange = Assert.Single(viewModel.ChatExchanges);

        Assert.True(exchange.HasCitations);
        Assert.Contains("[1]", exchange.Answer);
        Assert.Contains("[2]", exchange.Answer);
        Assert.True(exchange.HasSuggestions);

        // The tab itself loads with that transcript bound to it.
        var view = new ChatView { DataContext = viewModel };
        Assert.True(view.IsVisible);
    }
}
