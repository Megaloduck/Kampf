using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Kampf.Application.Abstractions;
using LLama;
using LLama.Common;
using LLama.Transformers;

namespace Kampf.Infrastructure.Ai;

/// <summary>
/// The app's own model: a local GGUF file downloaded from Hugging Face and run
/// in-process with llama.cpp. Weights load on the first question rather than at
/// startup, and generation runs off the UI thread so a slower machine keeps a
/// responsive window while the answer is written.
/// </summary>
public sealed class LocalChatModel : IChatModel, IDisposable
{
    /// <summary>Enough for a cited answer; the source passages get their own share.</summary>
    private const int MaxTokens = 1024;

    private readonly string _path;
    private readonly string _displayName;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _generationGate = new(1, 1);

    private LLamaWeights? _weights;
    private ModelParams? _parameters;
    private bool _disposed;

    public LocalChatModel(string path, string? displayName = null)
    {
        _path = path;
        _displayName = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileName(path) : displayName;
    }

    public string Name => $"local:{_displayName}";

    public async Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken ct = default)
    {
        var answer = new StringBuilder();

        await foreach (var token in StreamAsync(systemPrompt, userPrompt, ct))
            answer.Append(token);

        return answer.ToString();
    }

    /// <summary>
    /// Tokens are produced on a worker thread and handed over through a channel,
    /// so awaiting the answer in a view model never blocks the dispatcher.
    /// </summary>
    public async IAsyncEnumerable<string> StreamAsync(
        string systemPrompt,
        string userPrompt,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var channel = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        _ = Task.Run(async () =>
        {
            var acquired = false;

            try
            {
                await _generationGate.WaitAsync(ct).ConfigureAwait(false);
                acquired = true;

                var weights = await EnsureLoadedAsync(ct).ConfigureAwait(false);

                using var context = await Task.Run(
                    () => weights.CreateContext(_parameters!), ct).ConfigureAwait(false);

                var executor = new InteractiveExecutor(context);

                // The template comes from the GGUF metadata, so ChatML models and
                // Llama-style models both get the prompt they were trained on.
                var session = new ChatSession(executor)
                    .WithHistoryTransform(new PromptTemplateTransformer(weights));

                session.AddSystemMessage(systemPrompt);

                var inference = new InferenceParams
                {
                    MaxTokens = MaxTokens,
                    AntiPrompts = new List<string> { "<|im_end|>", "</s>" },
                };

                await foreach (var token in session
                    .ChatAsync(new ChatHistory.Message(AuthorRole.User, userPrompt), inference, ct)
                    .ConfigureAwait(false))
                {
                    await channel.Writer.WriteAsync(token, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // A stopped answer is not an error; the caller sees the partial text.
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
                return;
            }
            finally
            {
                if (acquired)
                    _generationGate.Release();
            }

            channel.Writer.TryComplete();
        }, CancellationToken.None);

        await foreach (var token in channel.Reader.ReadAllAsync(ct))
            yield return token;
    }

    /// <summary>
    /// Says whether the weights are here and loadable, without generating
    /// anything. Loading is cached, so Check doubles as a warm-up before the
    /// first question.
    /// </summary>
    public async Task<string?> ProbeAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
            return $"the model file is missing: {_path}";

        if (new FileInfo(_path).Length < 1024)
            return $"the model file at {_path} looks incomplete — download it again";

        try
        {
            await EnsureLoadedAsync(ct);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"the model at {_path} could not be loaded: {ex.Message}";
        }
    }

    private async Task<LLamaWeights> EnsureLoadedAsync(CancellationToken ct)
    {
        if (_weights is { } loaded)
            return loaded;

        await _loadGate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (_weights is { } already)
                return already;

            if (!File.Exists(_path))
                throw new FileNotFoundException("The model file is missing.", _path);

            var parameters = new ModelParams(_path)
            {
                // A 4k window covers the retrieved passages plus the answer, and
                // keeps memory use modest on a laptop.
                ContextSize = ModelSizing.ContextTokens,
                GpuLayerCount = 0,
            };

            var weights = await Task.Run(() => LLamaWeights.LoadFromFile(parameters), ct)
                .ConfigureAwait(false);

            _parameters = parameters;
            _weights = weights;
            return weights;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _weights?.Dispose();
        _weights = null;
        _loadGate.Dispose();
        _generationGate.Dispose();
    }
}
