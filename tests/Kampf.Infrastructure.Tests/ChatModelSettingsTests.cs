using Kampf.Infrastructure.Ai;
using Xunit;

namespace Kampf.Infrastructure.Tests;

public sealed class ChatModelSettingsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "kampf-tests", Guid.NewGuid().ToString("n"));

    public ChatModelSettingsTests() => Directory.CreateDirectory(_root);

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

    [Fact]
    public void A_chosen_server_and_model_survive_a_restart()
    {
        var settings = new ChatModelSettings("http://localhost:1234/v1", "ornith-1.0-9b");

        settings.Save(_root);
        var loaded = ChatModelSettings.Load(_root);

        Assert.Equal("http://localhost:1234/v1", loaded.ServerUrl);
        Assert.Equal("ornith-1.0-9b", loaded.Model);
        Assert.Equal(ChatProtocol.OpenAiCompatible, loaded.Protocol);
        Assert.True(loaded.IsConfigured);
    }

    [Fact]
    public void An_ollama_choice_remembers_its_protocol()
    {
        new ChatModelSettings("http://localhost:11434", "llama3.2", ChatProtocol.Ollama).Save(_root);

        var loaded = ChatModelSettings.Load(_root);

        Assert.Equal(ChatProtocol.Ollama, loaded.Protocol);
        Assert.IsType<OllamaChatModel>(loaded.CreateModel());
    }

    [Fact]
    public void A_missing_file_means_no_model()
    {
        var loaded = ChatModelSettings.Load(_root);

        Assert.False(loaded.IsConfigured);
        Assert.Null(loaded.CreateModel());
    }

    [Fact]
    public void Api_roots_are_normalised_so_the_v1_can_be_omitted()
    {
        var settings = new ChatModelSettings("http://localhost:1234", "ornith-1.0-9b");

        Assert.True(settings.TryGetEndpoint(out var address));
        Assert.Equal("http://localhost:1234/v1/", address.ToString());
        Assert.IsType<OpenAiCompatibleChatModel>(settings.CreateModel());
    }

    [Fact]
    public void An_ollama_url_keeps_its_own_shape()
    {
        var settings = new ChatModelSettings("http://localhost:11434", "llama3.2", ChatProtocol.Ollama);

        Assert.True(settings.TryGetEndpoint(out var address));
        Assert.Equal("http://localhost:11434/", address.ToString());
    }

    [Fact]
    public void Only_a_url_and_a_model_together_configure_anything()
    {
        Assert.False(new ChatModelSettings("http://localhost:1234/v1", null).IsConfigured);
        Assert.False(new ChatModelSettings(null, "ornith-1.0-9b").IsConfigured);
        Assert.Null(new ChatModelSettings("http://localhost:1234/v1", " ").CreateModel());
    }

    [Fact]
    public void Clearing_removes_the_saved_choice()
    {
        new ChatModelSettings("http://localhost:1234/v1", "ornith-1.0-9b").Save(_root);
        ChatModelSettings.Clear(_root);

        Assert.False(File.Exists(ChatModelSettings.PathFor(_root)));
        Assert.False(ChatModelSettings.Load(_root).IsConfigured);
    }
}
