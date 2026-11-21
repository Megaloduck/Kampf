using Kampf.Domain.Entities;
using Kampf.Infrastructure;
using Kampf.Infrastructure.Security;
using Kampf.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kampf.Infrastructure.Tests;

public sealed class VaultBackupTests : IDisposable
{
    private readonly string _root;

    public VaultBackupTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kampf-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
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
    public async Task Backup_is_a_consistent_database_that_opens_on_its_own()
    {
        var vault = await Vault.OpenAsync(_root);
        var note = Note.Create("Backup me", "body that must survive");
        await vault.Repository.SaveNoteAsync(note);

        var path = await vault.BackupNowAsync();

        Assert.True(File.Exists(path));
        Assert.Equal(Path.Combine(_root, "backups"), Path.GetDirectoryName(path));

        var copy = new SqliteVaultRepository(path, Path.Combine(_root, "copy-notes"));
        await copy.InitializeAsync();

        var loaded = await copy.GetNoteAsync(note.Id);
        Assert.NotNull(loaded);
        Assert.Equal("body that must survive", loaded!.Body);
    }

    [Fact]
    public async Task Encrypted_backup_restores_with_the_same_passphrase()
    {
        const string passphrase = "correct horse battery staple";
        var vault = await Vault.OpenAsync(_root);
        var note = Note.Create("Encrypted backup", "secret body");
        await vault.Repository.SaveNoteAsync(note);
        await vault.EnableEncryptionAsync(passphrase);

        var backup = await vault.BackupNowAsync();

        var restoreRoot = Path.Combine(_root, "restore");
        Directory.CreateDirectory(restoreRoot);
        File.Copy(backup, Path.Combine(restoreRoot, "kampf.db"));
        File.Copy(VaultLock.PathFor(_root), VaultLock.PathFor(restoreRoot));

        var restored = await Vault.OpenAsync(restoreRoot, passphrase);
        var loaded = await restored.Repository.GetNoteAsync(note.Id);

        Assert.NotNull(loaded);
        Assert.Equal("secret body", loaded!.Body);
    }
}
