using Kampf.Application.Abstractions;
using Kampf.Infrastructure.Ai;
using Kampf.Infrastructure.Embeddings;
using Kampf.Infrastructure.Pdf;
using Kampf.Infrastructure.Security;
using Kampf.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Kampf.Infrastructure;

/// <summary>
/// Local-first vault on disk: a SQLite database for the index and metadata,
/// plus imported PDFs under one folder. When a passphrase is set the database
/// is SQLCipher-encrypted and <c>vault.lock</c> records what is needed to
/// derive the key (never the key itself).
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

    /// <summary>Local chat model, or null when none is configured (chat stays extractive).</summary>
    public IChatModel? Chat { get; }

    /// <summary>True once the database is encrypted at rest.</summary>
    public bool IsEncrypted { get; private set; }

    private string? _keyHex;

    private Vault(string rootPath, string? keyHex)
    {
        RootPath = rootPath;
        NotesDirectory = Path.Combine(rootPath, "notes");
        AssetsDirectory = Path.Combine(rootPath, "assets");
        DatabasePath = Path.Combine(rootPath, "kampf.db");

        Repository = new SqliteVaultRepository(DatabasePath, NotesDirectory, keyHex);
        Files = new VaultFileStore(AssetsDirectory);
        PdfExtractor = new PdfPigExtractor();
        Embeddings = EmbeddingProviders.CreateFromEnvironment();
        Chat = ChatModels.CreateFromEnvironment();
        _keyHex = keyHex;
        IsEncrypted = keyHex is not null;
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

    /// <summary>
    /// True when <c>vault.lock</c> says this vault needs a passphrase. Cheap enough
    /// to call before opening, which is exactly what startup does.
    /// </summary>
    public static bool IsEncryptedVault(string? rootPath = null) =>
        VaultLock.Read(rootPath ?? DefaultRootPath) is { Encrypted: true };

    /// <exception cref="VaultLockedException">
    /// The vault is encrypted and the passphrase is missing or wrong.
    /// </exception>
    public static async Task<Vault> OpenAsync(
        string? rootPath = null,
        string? passphrase = null,
        CancellationToken ct = default)
    {
        var root = rootPath ?? DefaultRootPath;
        var lockData = VaultLock.Read(root);

        string? keyHex = null;
        if (lockData is { Encrypted: true })
        {
            if (string.IsNullOrEmpty(passphrase))
                throw new VaultLockedException("This vault is encrypted. Enter its passphrase to unlock it.");

            var key = VaultKeys.Unlock(lockData, passphrase)
                      ?? throw new VaultLockedException("That passphrase does not unlock this vault.");
            keyHex = VaultKeys.HexKey(key);
        }

        var vault = new Vault(root, keyHex);
        Directory.CreateDirectory(vault.RootPath);
        Directory.CreateDirectory(vault.NotesDirectory);
        Directory.CreateDirectory(vault.AssetsDirectory);

        try
        {
            await vault.Repository.InitializeAsync(ct);
        }
        catch (SqliteException) when (keyHex is not null)
        {
            // The lock file and the database disagree: a stale lock file, or a
            // database that was replaced from a backup taken before encryption.
            throw new VaultLockedException(
                "The database could not be decrypted with this vault's passphrase. It may have been replaced by a backup taken before encryption.");
        }

        return vault;
    }

    // ---------------------------------------------------------- encryption

    /// <summary>
    /// Encrypts the database and records the lock file. Plaintext markdown
    /// mirrors are deleted afterwards — keeping them would defeat the point.
    /// </summary>
    public async Task EnableEncryptionAsync(string passphrase, CancellationToken ct = default)
    {
        if (IsEncrypted)
            throw new InvalidOperationException("The vault is already encrypted.");

        var lockData = VaultKeys.CreateLock(passphrase);
        var key = VaultKeys.Unlock(lockData, passphrase)
                  ?? throw new InvalidOperationException("The passphrase could not be derived.");

        var encryption = new VaultEncryption(RootPath, DatabasePath, _keyHex);
        await encryption.EncryptAsync(key, lockData, ct);

        _keyHex = VaultKeys.HexKey(key);
        Repository.UseKey(_keyHex);
        Repository.DeleteMarkdownMirrors();
        IsEncrypted = true;
    }

    /// <summary>Removes encryption and starts writing markdown mirrors again.</summary>
    public async Task DisableEncryptionAsync(CancellationToken ct = default)
    {
        if (!IsEncrypted)
            return;

        var encryption = new VaultEncryption(RootPath, DatabasePath, _keyHex);
        await encryption.DecryptAsync(ct);

        _keyHex = null;
        Repository.UseKey(null);
        IsEncrypted = false;

        // Mirrors were deleted on the way in; bring them back on the way out.
        foreach (var note in await Repository.ListNotesAsync(ct))
            await Repository.SaveNoteAsync(note, ct);
    }

    /// <summary>Checks a passphrase against the lock file without opening the vault.</summary>
    public bool VerifyPassphrase(string passphrase) =>
        VaultLock.Read(RootPath) is { Encrypted: true } lockData
        && VaultKeys.Unlock(lockData, passphrase) is not null;

    /// <summary>Re-encrypts the database under a new passphrase.</summary>
    public async Task ChangePassphraseAsync(string passphrase, CancellationToken ct = default)
    {
        if (!IsEncrypted)
            throw new InvalidOperationException("The vault is not encrypted.");

        var lockData = VaultKeys.CreateLock(passphrase);
        var key = VaultKeys.Unlock(lockData, passphrase)
                  ?? throw new InvalidOperationException("The passphrase could not be derived.");

        var encryption = new VaultEncryption(RootPath, DatabasePath, _keyHex);
        await encryption.RekeyAsync(key, lockData, ct);

        _keyHex = VaultKeys.HexKey(key);
        Repository.UseKey(_keyHex);
        IsEncrypted = true;
    }
}
