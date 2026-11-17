using System.Net;
using Kampf.Infrastructure.Ai;
using Xunit;

namespace Kampf.Infrastructure.Tests;

public sealed class HuggingFaceModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "kampf-tests", Guid.NewGuid().ToString("n"));

    public HuggingFaceModelTests() => Directory.CreateDirectory(_root);

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
    public void A_repository_and_file_become_a_direct_download_url()
    {
        Assert.True(HuggingFaceModelReference.TryParse(
            "Qwen/Qwen2.5-0.5B-Instruct-GGUF",
            "qwen2.5-0.5b-instruct-q4_k_m.gguf",
            out var reference,
            out var error));

        Assert.Null(error);
        Assert.Equal(
            "https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/qwen2.5-0.5b-instruct-q4_k_m.gguf",
            reference.DownloadUri.ToString());
    }

    [Fact]
    public void A_copied_file_url_supplies_the_repository_and_file()
    {
        Assert.True(HuggingFaceModelReference.TryParse(
            "https://huggingface.co/bartowski/Qwen2.5-1.5B-Instruct-GGUF/blob/main/Qwen2.5-1.5B-Instruct-Q4_K_M.gguf",
            file: null,
            out var reference,
            out var error));

        Assert.Null(error);
        Assert.Equal("bartowski/Qwen2.5-1.5B-Instruct-GGUF", reference.RepoId);
        Assert.Equal("Qwen2.5-1.5B-Instruct-Q4_K_M.gguf", reference.FileName);
        Assert.Contains("/resolve/main/", reference.DownloadUri.ToString());
    }

    [Theory]
    [InlineData("Qwen/Qwen2.5-0.5B-Instruct-GGUF", "notes.txt")]
    [InlineData("Qwen/Qwen2.5-0.5B-Instruct-GGUF", "../escape.gguf")]
    [InlineData("not-a-repository", "model.gguf")]
    [InlineData("Qwen/Qwen2.5-0.5B-Instruct-GGUF", null)]
    public void Anything_that_is_not_a_gguf_in_a_repository_is_refused(string repo, string? file)
    {
        Assert.False(HuggingFaceModelReference.TryParse(repo, file, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void A_local_model_choice_round_trips_and_resolves_inside_the_vault()
    {
        var settings = new ChatModelSettings(
            "Qwen/Qwen2.5-0.5B-Instruct-GGUF",
            "qwen2.5-0.5b-instruct-q4_k_m.gguf",
            ChatProtocol.Local);

        settings.Save(_root);
        var loaded = ChatModelSettings.Load(_root);

        Assert.Equal(ChatProtocol.Local, loaded.Protocol);
        Assert.Equal("Qwen/Qwen2.5-0.5B-Instruct-GGUF", loaded.ServerUrl);

        // Nothing downloaded yet: the app keeps answering extractively rather
        // than naming a model it cannot load.
        Assert.Null(loaded.CreateModel(_root));

        var path = HuggingFaceModelStore.PathFor(
            _root,
            loaded.ServerUrl!,
            loaded.Model!);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[2048]);

        using var model = Assert.IsType<LocalChatModel>(loaded.CreateModel(_root));
        Assert.StartsWith("local:", model.Name);
        Assert.Contains(Path.Combine("models", "Qwen-Qwen2.5-0.5B-Instruct-GGUF"), path);
    }

    [Fact]
    public async Task A_cancelled_download_keeps_its_partial_file_for_resuming()
    {
        var reference = new HuggingFaceModelReference("owner/cancelled", "weights.gguf");
        using var cts = new CancellationTokenSource();

        // The response produces one chunk, then stalls until the caller cancels.
        using var store = new HuggingFaceModelStore(_root, new StreamingHandler());
        cts.CancelAfter(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.DownloadAsync(reference, progress: null, cts.Token));

        var destination = store.PathFor(reference);
        var partial = destination + ".part";

        Assert.False(File.Exists(destination));
        Assert.True(File.Exists(partial));
        Assert.True(new FileInfo(partial).Length > 0);
    }

    [Fact]
    public void A_download_is_kept_under_the_owners_folder_so_names_cannot_collide()
    {
        var first = HuggingFaceModelStore.PathFor(_root, "owner/model-a", "weights.gguf");
        var second = HuggingFaceModelStore.PathFor(_root, "other/model-b", "weights.gguf");

        Assert.NotEqual(first, second);
        Assert.StartsWith(
            Path.GetFullPath(HuggingFaceModelStore.ModelsDirectory(_root)),
            Path.GetFullPath(first));
    }

    private sealed class StreamingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamingContent(),
            };
            response.Content.Headers.ContentLength = 1024;
            return Task.FromResult(response);
        }
    }

    private sealed class StreamingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = 1024;
            return true;
        }

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new StallingStream());
    }

    private sealed class StallingStream : Stream
    {
        private int _reads;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _reads) == 1)
            {
                buffer.Span[..Math.Min(16, buffer.Length)].Fill(0x2A);
                return ValueTask.FromResult(16);
            }

            return new ValueTask<int>(WaitForCancelAsync(cancellationToken));
        }

        private static async Task<int> WaitForCancelAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
