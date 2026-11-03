using Kampf.Application.Abstractions;
using Kampf.Infrastructure.Embeddings;
using Kampf.Infrastructure.Pdf;
using Kampf.Infrastructure.Storage;

namespace Kampf.Infrastructure;

/// <summary>
/// Local-first vault on disk: a SQLite database for the index and metadata,
/// plus plain markdown files and imported PDFs under one folder.
/// </summary>
public sealed class Vault
{
    public string RootPath { get; }
    public string NotesDirectory { get; }
    public string AssetsDirectory { get; }
    public string DatabasePath { get; }

    public SqliteVaultRepository Repository { get; }
    public VaultFileStore Files { get; }
    public PdfPigExtractor PdfExtractor { get; }

    /// <summary>Offline hashing embedder unless a local Ollama model is configured.</summary>
    public IEmbeddingProvider Embeddings { get; }

    private Vault(string rootPath)
    {
        RootPath = rootPath;
        NotesDirectory = Path.Combine(rootPath, "notes");
        AssetsDirectory = Path.Combine(rootPath, "assets");
        DatabasePath = Path.Combine(rootPath, "kampf.db");

        Repository = new SqliteVaultRepository(DatabasePath, NotesDirectory);
        Files = new VaultFileStore(AssetsDirectory);
        PdfExtractor = new PdfPigExtractor();
        Embeddings = EmbeddingProviders.CreateFromEnvironment();
    }

    /// <summary>
    /// Vault location: <c>%LOCALAPPDATA%\Kampf</c> by default, overridable with
    /// the <c>KAMPF_VAULT</c> environment variable (portable or second vault).
    /// </summary>
    public static string DefaultRootPath
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("KAMPF_VAULT");
            return string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Kampf")
                : overridden;
        }
    }

    public static async Task<Vault> OpenAsync(string? rootPath = null, CancellationToken ct = default)
    {
        var vault = new Vault(rootPath ?? DefaultRootPath);
        Directory.CreateDirectory(vault.RootPath);
        Directory.CreateDirectory(vault.NotesDirectory);
        Directory.CreateDirectory(vault.AssetsDirectory);
        await vault.Repository.InitializeAsync(ct);
        return vault;
    }
}
