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

    /// <summary>
    /// The same completion, delivered as it is generated. Used by Idea-Craft so a
    /// long answer appears while it is being written; a model that cannot stream
    /// can yield the whole answer once.
    /// </summary>
    IAsyncEnumerable<string> StreamAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken ct = default);

    /// <summary>
    /// Checks that the model named in <see cref="Name"/> can answer right now.
    /// Returns null when it can, or a sentence explaining what to fix. A model
    /// that is configured but not pulled fails as a 404 at question time, which
    /// looks like a bug instead of a setup step.
    /// </summary>
    Task<string?> ProbeAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
}
