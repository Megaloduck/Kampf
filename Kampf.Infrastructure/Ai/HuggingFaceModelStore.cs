namespace Kampf.Infrastructure.Ai;

/// <summary>Bytes received so far, for a progress line in Settings.</summary>
public sealed record ModelDownloadProgress(long Received, long? Total)
{
    public double? Fraction => Total is > 0 ? (double)Received / Total.Value : null;

    public string Describe()
    {
        var received = ModelSizing.FormatBytes(Received);

        return Total is > 0
            ? $"{received} of {ModelSizing.FormatBytes(Total.Value)} ({Fraction:P0})"
            : received;
    }
}

/// <summary>
/// Downloads GGUF weights from a public Hugging Face repository into the vault's
/// <c>models/</c> folder. The file is written to a <c>.part</c> beside its final
/// name and resumed with a range request, so a dropped connection does not throw
/// away a gigabyte of progress.
/// </summary>
public sealed class HuggingFaceModelStore : IDisposable
{
    private const string Host = "https://huggingface.co";

    private readonly HttpClient _http;

    public HuggingFaceModelStore(string vaultRoot, HttpMessageHandler? handler = null)
    {
        VaultRoot = vaultRoot;
        _http = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: true);

        _http.Timeout = TimeSpan.FromMinutes(30);
    }

    public string VaultRoot { get; }

    /// <summary>Where a reference is stored in this vault.</summary>
    public string PathFor(HuggingFaceModelReference reference)
    {
        var models = ModelsDirectory(VaultRoot);
        var path = Path.GetFullPath(Path.Combine(models, reference.RelativePath));

        if (!path.StartsWith(Path.GetFullPath(models) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("The model path escapes the vault's models folder.");

        return path;
    }

    public static string ModelsDirectory(string vaultRoot) => Path.Combine(vaultRoot, "models");

    /// <summary>The file a saved configuration names, whether or not it is there.</summary>
    public static string PathFor(string vaultRoot, string repository, string file)
    {
        if (!HuggingFaceModelReference.TryParse(repository, file, out var reference, out var error))
            throw new ArgumentException(error);

        return Path.GetFullPath(Path.Combine(ModelsDirectory(vaultRoot), reference.RelativePath));
    }

    /// <summary>
    /// The file's size without downloading it: from disk when the vault already
    /// has it, otherwise the length Hugging Face reports for the resolved file.
    /// Used by LLM Configuration to size a model before it is fetched.
    /// </summary>
    public async Task<long> GetSizeAsync(
        HuggingFaceModelReference reference,
        CancellationToken ct = default)
    {
        var local = PathFor(reference);

        if (File.Exists(local))
            return new FileInfo(local).Length;

        using var head = new HttpRequestMessage(HttpMethod.Head, reference.DownloadUri);
        using var headResponse = await _http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct);

        if (headResponse.IsSuccessStatusCode
            && headResponse.Content.Headers.ContentLength is { } headLength
            && headLength > 0)
        {
            return headLength;
        }

        // Some proxies refuse HEAD; a GET whose body is never read costs only the
        // headers, and the redirect still reports the real length.
        using var get = new HttpRequestMessage(HttpMethod.Get, reference.DownloadUri);
        using var getResponse = await _http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct);

        if (getResponse.IsSuccessStatusCode
            && getResponse.Content.Headers.ContentLength is { } length
            && length > 0)
        {
            return length;
        }

        throw new HttpRequestException(
            $"Hugging Face did not report a size for {reference.RepoId}/{reference.FileName}.");
    }

    /// <summary>
    /// Downloads the file, or returns it when a complete copy is already here.
    /// </summary>
    public async Task<string> DownloadAsync(
        HuggingFaceModelReference reference,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var destination = PathFor(reference);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var partial = destination + ".part";

        // Already here: applying the same model twice must not download it again.
        if (File.Exists(destination) && !File.Exists(partial) && new FileInfo(destination).Length > 1024)
            return destination;

        long existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, reference.DownloadUri);

        if (existing > 0)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (existing > 0 && response.StatusCode == System.Net.HttpStatusCode.OK)
        {
            // The server ignored the range request: start the file again rather
            // than appending a second copy.
            existing = 0;
            File.Delete(partial);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Hugging Face returned {(int)response.StatusCode} for {reference.RepoId}/{reference.FileName}. " +
                "Check the repository is public and the file name is exact.");
        }

        var total = response.Content.Headers.ContentLength is { } length
            ? length + existing
            : (long?)null;

        progress?.Report(new ModelDownloadProgress(existing, total));

        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = new FileStream(
            partial,
            existing > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true))
        {
            var buffer = new byte[81920];
            long received = existing;
            int read;

            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                progress?.Report(new ModelDownloadProgress(received, total));
            }
        }

        var size = new FileInfo(partial).Length;

        if (size == 0)
            throw new IOException("The download was empty; the model file was not saved.");

        // A file that ends before the advertised length was truncated by a
        // dropped connection: keep the part file so the next attempt resumes.
        if (total is { } expected && size < expected)
            throw new IOException($"The download stopped at {size:N0} of {expected:N0} bytes; try again to resume.");

        File.Move(partial, destination, overwrite: true);
        return destination;
    }

    public void Dispose() => _http.Dispose();
}
