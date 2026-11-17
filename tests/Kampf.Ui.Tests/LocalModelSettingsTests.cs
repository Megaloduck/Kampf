using Avalonia.Headless.XUnit;
using Kampf.Infrastructure;
using Kampf.Infrastructure.Ai;
using Kampf.ViewModels;
using Kampf.Views;
using Xunit;

namespace Kampf.Ui.Tests;

public sealed class LocalModelSettingsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "kampf-tests", Guid.NewGuid().ToString("n"));

    public LocalModelSettingsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best effort cleanup
        }
    }

    [AvaloniaFact]
    public async Task The_configuration_page_starts_with_a_model_ready_to_download()
    {
        var vault = await Vault.OpenAsync(Path.Combine(_root, "vault"));
        var viewModel = new MainViewModel(vault) { View = VaultView.LlmConfig };

        Assert.Equal(HuggingFaceModelReference.DefaultRepo, viewModel.HuggingFaceRepo);
        Assert.Equal(HuggingFaceModelReference.DefaultFile, viewModel.HuggingFaceFile);
        Assert.False(viewModel.HasHuggingFaceStatus);
        Assert.False(viewModel.IsDownloadingModel);

        // The page binds to the same view model without throwing, and is the
        // workspace the rail switched to.
        var view = new LlmConfigView { DataContext = viewModel };
        Assert.True(view.IsVisible);
    }

    [AvaloniaFact]
    public async Task A_model_already_inside_the_vault_becomes_the_answering_model()
    {
        var root = Path.Combine(_root, "vault");
        var vault = await Vault.OpenAsync(root);
        var viewModel = new MainViewModel(vault);

        viewModel.HuggingFaceRepo = "owner/test-slm";
        viewModel.HuggingFaceFile = "test-slm-q4_k_m.gguf";

        // Stand in for a completed download: the command must adopt a whole file
        // in the vault and must not fetch it again.
        var path = HuggingFaceModelStore.PathFor(root, viewModel.HuggingFaceRepo, viewModel.HuggingFaceFile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[2048]);

        await viewModel.DownloadHuggingFaceModelCommand.ExecuteAsync(null);

        Assert.Contains("Ready", viewModel.HuggingFaceStatus);
        Assert.Equal("local:test-slm-q4_k_m.gguf", viewModel.ChatModelLabel);
        Assert.IsType<LocalChatModel>(vault.Chat);
        Assert.Contains("runs inside the app", viewModel.ChatModelHealth);

        // Adopting the file also sizes it against this machine's free memory.
        Assert.True(viewModel.Hardware.LogicalCores > 0);
        Assert.Contains("fits comfortably", viewModel.ModelFitSummary);

        var saved = ChatModelSettings.Load(root);
        Assert.Equal(ChatProtocol.Local, saved.Protocol);
        Assert.Equal("owner/test-slm", saved.ServerUrl);
        Assert.Equal("test-slm-q4_k_m.gguf", saved.Model);

        // Reopening the vault finds the same in-process model.
        var reopened = await Vault.OpenAsync(root);
        Assert.Equal("local:test-slm-q4_k_m.gguf", reopened.Chat?.Name);

        reopened.UseChatModel(null);
        vault.UseChatModel(null);
    }

    [AvaloniaFact]
    public async Task The_scan_fills_in_the_model_that_fits_this_machine()
    {
        var root = Path.Combine(_root, "vault");
        var vault = await Vault.OpenAsync(root);
        var viewModel = new MainViewModel(vault);

        var recommended = ModelSizing.Recommend(HardwareProbe.Detect());
        viewModel.HuggingFaceRepo = recommended.RepoId;
        viewModel.HuggingFaceFile = recommended.FileName;

        // The estimate reads a file that is already here, so the test needs no network.
        var path = HuggingFaceModelStore.PathFor(root, recommended.RepoId, recommended.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[2048]);

        await viewModel.EstimateModelFitCommand.ExecuteAsync(null);

        Assert.True(viewModel.Hardware.LogicalCores > 0);
        Assert.Contains("fits comfortably", viewModel.ModelFitSummary);
        Assert.Contains("Recommended for this machine", viewModel.ModelRecommendation);

        await viewModel.UseRecommendedModelCommand.ExecuteAsync(null);

        Assert.Equal(recommended.RepoId, viewModel.HuggingFaceRepo);
        Assert.Equal(recommended.FileName, viewModel.HuggingFaceFile);
        Assert.Contains("Filled in", viewModel.HuggingFaceStatus);
    }

    [AvaloniaFact]
    public async Task A_saved_local_model_is_sized_when_the_app_starts()
    {
        var root = Path.Combine(_root, "vault");
        var repo = "owner/startup-slm";
        var file = "startup-slm-q4_k_m.gguf";

        var path = HuggingFaceModelStore.PathFor(root, repo, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[2048]);
        new ChatModelSettings(repo, file, ChatProtocol.Local).Save(root);

        var vault = await Vault.OpenAsync(root);
        Assert.IsType<LocalChatModel>(vault.Chat);

        var viewModel = new MainViewModel(vault);
        await viewModel.InitializeAsync();

        // No page visit and no network: the startup pass measured the saved file.
        Assert.Contains(file, viewModel.ModelFitSummary);
        Assert.Contains("fits comfortably", viewModel.ModelFitSummary);

        vault.UseChatModel(null);
    }

    [AvaloniaFact]
    public async Task A_repository_that_is_not_owner_slash_name_is_refused_before_downloading()
    {
        var root = Path.Combine(_root, "vault");
        var vault = await Vault.OpenAsync(root);
        var viewModel = new MainViewModel(vault);

        viewModel.HuggingFaceRepo = "not a repository";
        viewModel.HuggingFaceFile = "model.gguf";

        await viewModel.DownloadHuggingFaceModelCommand.ExecuteAsync(null);

        Assert.Contains("owner/name", viewModel.HuggingFaceStatus);
        Assert.Contains("no local model", viewModel.ChatModelLabel);
        Assert.False(File.Exists(ChatModelSettings.PathFor(root)));
    }
}
