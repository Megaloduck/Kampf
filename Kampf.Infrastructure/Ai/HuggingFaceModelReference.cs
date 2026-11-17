namespace Kampf.Infrastructure.Ai;

/// <summary>
/// One GGUF file in a public Hugging Face repository. The app downloads the file
/// itself, so nothing but the model weights ever leaves the machine: inference
/// runs in-process through llama.cpp (LLamaSharp).
/// </summary>
public sealed record HuggingFaceModelReference(
    string RepoId,
    string FileName,
    string Revision = "main")
{
    /// <summary>A small instruct model that fits a laptop and downloads quickly.</summary>
    public const string DefaultRepo = "Qwen/Qwen2.5-0.5B-Instruct-GGUF";

    public const string DefaultFile = "qwen2.5-0.5b-instruct-q4_k_m.gguf";

    /// <summary>The direct download address; redirected to the CDN by Hugging Face.</summary>
    public Uri DownloadUri => new(
        $"https://huggingface.co/{RepoId}/resolve/{Uri.EscapeDataString(Revision)}/{EscapePath(FileName)}");

    /// <summary>
    /// Accepts a bare repository id plus a file name, or a repository / file URL
    /// copied from the model page (<c>resolve</c>, <c>blob</c> and <c>raw</c>).
    /// The file must be a <c>.gguf</c>, because that is what the local runtime reads.
    /// </summary>
    public static bool TryParse(
        string? repository,
        string? file,
        out HuggingFaceModelReference reference,
        out string? error)
    {
        reference = null!;
        error = null;

        var repoText = repository?.Trim();
        var fileText = file?.Trim();

        if (string.IsNullOrEmpty(repoText))
        {
            error = "Name the Hugging Face repository, e.g. " + DefaultRepo;
            return false;
        }

        var revision = "main";

        if (Uri.TryCreate(repoText, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            if (!uri.Host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase)
                && !uri.Host.Equals("www.huggingface.co", StringComparison.OrdinalIgnoreCase))
            {
                error = "That address is not huggingface.co.";
                return false;
            }

            var segments = uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.UnescapeDataString)
                .ToArray();

            if (segments.Length == 0)
            {
                error = "The address names no repository.";
                return false;
            }

            // Dataset/space URLs are not model repositories.
            if (segments[0] is "datasets" or "spaces" or "buckets")
            {
                error = "Only model repositories can be downloaded.";
                return false;
            }

            if (segments.Length < 2)
            {
                error = "The address names no repository.";
                return false;
            }

            repoText = segments[0] + "/" + segments[1];

            // /resolve/<rev>/<path>, /blob/<rev>/<path>, /raw/<rev>/<path>.
            if (segments.Length >= 5 && segments[2] is "resolve" or "blob" or "raw")
            {
                revision = segments[3];
                fileText = string.Join('/', segments.Skip(4));
            }
            else if (segments.Length >= 3 && segments[2] is "tree")
            {
                error = "That address points at a folder; open the file and copy its URL.";
                return false;
            }
        }

        if (!IsRepoId(repoText, out error))
            return false;

        if (string.IsNullOrWhiteSpace(fileText))
        {
            error = "Name the .gguf file in the repository.";
            return false;
        }

        fileText = fileText.Replace('\\', '/').TrimStart('/');

        if (!IsSafePath(fileText))
        {
            error = "The file name cannot contain '..' or start outside the repository.";
            return false;
        }

        if (!fileText.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            error = "Only GGUF files can run here — choose a file ending in .gguf.";
            return false;
        }

        reference = new HuggingFaceModelReference(repoText, fileText, revision);
        return true;
    }

    /// <summary>Where the file lands inside a vault, under <c>models/</c>.</summary>
    public string RelativePath
    {
        get
        {
            // The owner/name pair is flattened so two models with the same file
            // name cannot overwrite each other.
            var folder = RepoId.Replace('/', '-').Replace('\\', '-');
            return Path.Combine(folder, FileName.Replace('/', Path.DirectorySeparatorChar));
        }
    }

    private static bool IsRepoId(string repo, out string? error)
    {
        error = null;
        var parts = repo.Split('/');

        if (parts.Length != 2
            || parts.Any(part => part.Length == 0 || part is "." or "..")
            || parts.Any(part => part.Any(char.IsWhiteSpace)))
        {
            error = "A repository is written owner/name, e.g. " + DefaultRepo;
            return false;
        }

        return true;
    }

    private static bool IsSafePath(string file)
    {
        var parts = file.Split('/');
        return parts.All(part => part.Length > 0 && part is not "." and not "..");
    }

    private static string EscapePath(string file) =>
        string.Join('/', file.Split('/').Select(Uri.EscapeDataString));
}
