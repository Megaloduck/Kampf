namespace Kampf.Application.Abstractions;

/// <summary>
/// A local text-generation model. Implementations live in infrastructure
/// (Ollama); when none is configured the chat service answers extractively from
/// the vault instead of calling out to a cloud API.
/// </summary>
public interface IChatModel
{
    /// <summary>Model identifier shown in the chat header, e.g. <c>ollama:llama3.2</c>.</summary>
    string Name { get; }

    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);
}
