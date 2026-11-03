using System.Text;
using Kampf.Domain.Entities;
using Kampf.Infrastructure;
using Kampf.Infrastructure.Security;
using Xunit;

namespace Kampf.Infrastructure.Tests;

public sealed class VaultEncryptionTests : IDisposable
{
    private const string Secret = "topsecretbody-marker";
    private const string Passphrase = "correct horse battery staple";

    private readonly string _root;

    public VaultEncryptionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kampf-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
    public async Task Encrypting_removes_plaintext_from_the_database_and_the_mirrors()
    {
        var vault = await Vault.OpenAsync(_root);
        var note = Note.Create("Entropy", Secret);
        await vault.Repository.SaveNoteAsync(note);

        var mirror = Path.Combine(vault.NotesDirectory, $"{note.Id}.md");
        Assert.True(File.Exists(mirror));

        await vault.EnableEncryptionAsync(Passphrase);

        Assert.True(vault.IsEncrypted);
        Assert.True(File.Exists(VaultLock.PathFor(_root)));
        Assert.False(File.Exists(mirror));
        Assert.DoesNotContain(Secret, Encoding.UTF8.GetString(File.ReadAllBytes(vault.DatabasePath)));

        // A backup of the plaintext vault was kept before the rewrite.
        Assert.True(Directory.Exists(Path.Combine(_root, "backups")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_root, "backups"), "*.db"));
    }

    [Fact]
    public async Task Encrypted_vault_reopens_with_the_passphrase()
    {
        var vault = await Vault.OpenAsync(_root);
        var note = Note.Create("Entropy", Secret);
        await vault.Repository.SaveNoteAsync(note);
        var tag = await vault.Repository.EnsureTagAsync("physics");
        await vault.Repository.SetNoteTagsAsync(note.Id, new[] { tag.Id });
        await vault.EnableEncryptionAsync(Passphrase);

        var reopened = await Vault.OpenAsync(_root, Passphrase);

        Assert.True(reopened.IsEncrypted);
        var loaded = await reopened.Repository.GetNoteAsync(note.Id);
        Assert.NotNull(loaded);
        Assert.Equal(Secret, loaded!.Body);
        Assert.Equal("physics", Assert.Single(loaded.Tags).Name);
    }

    [Fact]
    public async Task Wrong_or_missing_passphrase_is_rejected()
    {
        var vault = await Vault.OpenAsync(_root);
        await vault.Repository.SaveNoteAsync(Note.Create("Entropy", Secret));
        await vault.EnableEncryptionAsync(Passphrase);

        await Assert.ThrowsAsync<VaultLockedException>(() => Vault.OpenAsync(_root));
        await Assert.ThrowsAsync<VaultLockedException>(() => Vault.OpenAsync(_root, "wrong passphrase"));
    }

    [Fact]
    public async Task Disabling_encryption_restores_a_readable_plaintext_vault()
    {
        var vault = await Vault.OpenAsync(_root);
        var note = Note.Create("Entropy", Secret);
        await vault.Repository.SaveNoteAsync(note);
        await vault.EnableEncryptionAsync(Passphrase);

        await vault.DisableEncryptionAsync();

        Assert.False(vault.IsEncrypted);
        Assert.False(File.Exists(VaultLock.PathFor(_root)));

        var reopened = await Vault.OpenAsync(_root);
        Assert.False(reopened.IsEncrypted);
        Assert.Equal(Secret, (await reopened.Repository.GetNoteAsync(note.Id))!.Body);

        // Mirrors come back once the vault is plaintext again.
        Assert.True(File.Exists(Path.Combine(reopened.NotesDirectory, $"{note.Id}.md")));
    }

    [Fact]
    public async Task Changing_the_passphrase_retires_the_old_one()
    {
        var vault = await Vault.OpenAsync(_root);
        var note = Note.Create("Entropy", Secret);
        await vault.Repository.SaveNoteAsync(note);
        await vault.EnableEncryptionAsync(Passphrase);

        await vault.ChangePassphraseAsync("a different passphrase");

        await Assert.ThrowsAsync<VaultLockedException>(() => Vault.OpenAsync(_root, Passphrase));

        var reopened = await Vault.OpenAsync(_root, "a different passphrase");
        Assert.Equal(Secret, (await reopened.Repository.GetNoteAsync(note.Id))!.Body);
    }

    [Fact]
    public void Key_derivation_is_deterministic_and_rejects_other_passphrases()
    {
        var lockData = VaultKeys.CreateLock("swordfish", iterations: 1_000);

        var key = VaultKeys.Unlock(lockData, "swordfish");
        var again = VaultKeys.Unlock(lockData, "swordfish");

        Assert.NotNull(key);
        Assert.Equal(32, key!.Length);
        Assert.Equal(key, again);
        Assert.Null(VaultKeys.Unlock(lockData, "swordfisj"));

        // The lock file never carries the key or the passphrase.
        Assert.DoesNotContain("swordfish", lockData.ToString());
        Assert.DoesNotContain(VaultKeys.HexKey(key), lockData.ToString());
    }

    [Fact]
    public async Task Encrypted_vault_is_detected_before_it_is_opened()
    {
        var vault = await Vault.OpenAsync(_root);
        Assert.False(Vault.IsEncryptedVault(_root));

        await vault.EnableEncryptionAsync(Passphrase);

        Assert.True(Vault.IsEncryptedVault(_root));
    }
}
