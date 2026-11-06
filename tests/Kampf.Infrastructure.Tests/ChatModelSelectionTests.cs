using Kampf.Infrastructure.Ai;
using Xunit;

namespace Kampf.Infrastructure.Tests;

public class ChatModelSelectionTests
{
    [Fact]
    public void An_openai_compatible_url_is_preferred()
    {
        var model = ChatModels.Create(
            "http://localhost:1234/v1",
            "http://localhost:11434",
            "llama3.2");

        Assert.IsType<OpenAiCompatibleChatModel>(model);
    }

    [Fact]
    public void Ollama_is_used_when_that_is_the_only_server_configured()
    {
        var model = ChatModels.Create(null, "http://localhost:11434", "llama3.2");

        Assert.IsType<OllamaChatModel>(model);
    }

    [Fact]
    public void A_server_without_a_model_name_stays_extractive()
    {
        Assert.Null(ChatModels.Create("http://localhost:1234/v1", "http://localhost:11434", "  "));
    }

    [Fact]
    public void A_model_name_without_a_server_stays_extractive()
    {
        Assert.Null(ChatModels.Create(null, null, "llama3.2"));
    }
}
