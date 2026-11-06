using System.Text.Json;
using System.Text.Json.Serialization;
using Kampf.Application.Abstractions;

namespace Kampf.Infrastructure.Ai;

/// <summary>Which protocol the configured local server speaks.</summary>
public enum ChatProtocol
{
    /// <summary>OpenAI-shaped server: LM Studio, llama.cpp, vLLM, Ollama's <c>/v1</c>.</summary>
    OpenAiCompatible = 0,

    /// <summary>Native Ollama API on its own port.</summary>
    Ollama = 1,
}

/// <summary>
/// Where Idea-Craft's model comes from, as chosen in Settings rather than through
/// environment variables. Stored beside the vault in <c>chat-model.json</c>: it
/// names a server on this machine, so it belongs with the local vault, not in a
/// global profile.
///
/// Environment variables still work and act as the fallback when nothing has been
/// saved here.
/// </summary>
public sealed record ChatModelSettings(
    string? ServerUrl = null,
    string? Model = null,
    ChatProtocol Protocol = ChatProtocol.OpenAiCompatible)
{
    public const string FileName = "chat-model.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(Model);

    /// <summary>The API root, with <c>/v1</c> filled in for OpenAI-shaped servers.</summary>
    public bool TryGetEndpoint(out Uri address) =>
        ChatModels.TryEndpoint(ServerUrl, Protocol, out address);

    /// <summary>The model these settings describe, or null when they describe none.</summary>
    public IChatModel? CreateModel() =>
        IsConfigured ? ChatModels.Create(Protocol, ServerUrl, Model) : null;

    public static string PathFor(string vaultRoot) => Path.Combine(vaultRoot, FileName);

    /// <summary>Reads the saved settings; a missing or unreadable file means "not configured".</summary>
    public static ChatModelSettings Load(string vaultRoot)
    {
        try
        {
            var path = PathFor(vaultRoot);
            if (!File.Exists(path))
                return new ChatModelSettings();

            var settings = JsonSerializer.Deserialize<ChatModelSettings>(File.ReadAllText(path), Json);
            return settings ?? new ChatModelSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or NotSupportedException)
        {
            // A corrupt file must not stop the vault from opening.
            return new ChatModelSettings();
        }
    }

    public void Save(string vaultRoot)
    {
        Directory.CreateDirectory(vaultRoot);
        File.WriteAllText(PathFor(vaultRoot), JsonSerializer.Serialize(this, Json));
    }

    public static void Clear(string vaultRoot)
    {
        var path = PathFor(vaultRoot);
        if (File.Exists(path))
            File.Delete(path);
    }
}
