using Microsoft.Data.Sqlite;

namespace Kampf.Infrastructure.Security;

/// <summary>
/// Rewrites <c>kampf.db</c> through SQLCipher's <c>sqlcipher_export</c> so the
/// same vault can move between plaintext and encrypted storage, or change key.
/// Every rewrite is backed up first and verified after, because a half-written
/// vault would be data loss.
/// </summary>
public sealed class VaultEncryption
{
    private readonly string _rootPath;
    private readonly string _databasePath;
    private readonly string? _currentKeyHex;

    public VaultEncryption(string rootPath, string databasePath, string? currentKeyHex)
    {
        _rootPath = rootPath;
        _databasePath = databasePath;
        _currentKeyHex = string.IsNullOrWhiteSpace(currentKeyHex) ? null : currentKeyHex;
    }

    public string BackupsDirectory => Path.Combine(_rootPath, "backups");

    /// <summary>Encrypts a plaintext vault with <paramref name="key"/> and writes the lock file.</summary>
    public Task EncryptAsync(byte[] key, VaultLockData lockData, CancellationToken ct = default) =>
        RewriteAsync(VaultKeys.HexKey(key), lockData, ct);

    /// <summary>Changes the key of an already encrypted vault.</summary>
    public Task RekeyAsync(byte[] key, VaultLockData lockData, CancellationToken ct = default) =>
        RewriteAsync(VaultKeys.HexKey(key), lockData, ct);

    /// <summary>Strips encryption, leaving a plaintext vault and no lock file.</summary>
    public Task DecryptAsync(CancellationToken ct = default) =>
        RewriteAsync(null, null, ct);

    private async Task RewriteAsync(string? targetKeyHex, VaultLockData? lockData, CancellationToken ct)
    {
        if (!File.Exists(_databasePath))
            throw new InvalidOperationException("The vault database does not exist yet.");

        var sourceCount = await CountRowsAsync(_databasePath, _currentKeyHex, ct);
        var exportPath = _databasePath + ".export";
        if (File.Exists(exportPath))
            File.Delete(exportPath);

        SqliteConnection.ClearAllPools();
        await ExportAsync(exportPath, targetKeyHex, ct);

        var exportedCount = await CountRowsAsync(exportPath, targetKeyHex, ct);
        if (exportedCount != sourceCount)
        {
            File.Delete(exportPath);
            throw new InvalidOperationException(
                $"Encryption aborted: the rewritten vault held {exportedCount} rows instead of {sourceCount}. The original database was left untouched.");
        }

        Backup();

        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var stale = _databasePath + suffix;
            if (File.Exists(stale))
                File.Delete(stale);
        }

        File.Move(exportPath, _databasePath, overwrite: true);

        if (lockData is null)
            VaultLock.Delete(_rootPath);
        else
            VaultLock.Write(_rootPath, lockData);

        SqliteConnection.ClearAllPools();
    }

    private async Task ExportAsync(string exportPath, string? targetKeyHex, CancellationToken ct)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Pooling = false,
        };

        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(ct);

        if (_currentKeyHex is not null)
            await ExecuteAsync(connection, $"PRAGMA key = \"x'{_currentKeyHex}'\";", ct);

        await ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", ct);

        var escaped = exportPath.Replace("'", "''");
        var attach = targetKeyHex is null
            ? $"ATTACH DATABASE '{escaped}' AS encrypted KEY '';"
            : $"ATTACH DATABASE '{escaped}' AS encrypted KEY \"x'{targetKeyHex}'\";";

        await ExecuteAsync(connection, attach, ct);
        await ExecuteAsync(connection, "SELECT sqlcipher_export('encrypted');", ct);
        await ExecuteAsync(connection, "DETACH DATABASE encrypted;", ct);
    }

    private static async Task<long> CountRowsAsync(string path, string? keyHex, CancellationToken ct)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(ct);

        if (keyHex is not null)
            await ExecuteAsync(connection, $"PRAGMA key = \"x'{keyHex}'\";", ct);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM notes)
                 + (SELECT COUNT(*) FROM links)
                 + (SELECT COUNT(*) FROM documents)
                 + (SELECT COUNT(*) FROM highlights)
                 + (SELECT COUNT(*) FROM suggestions)
                 + (SELECT COUNT(*) FROM audit)
                 + (SELECT COUNT(*) FROM chat_messages)
                 + (SELECT COUNT(*) FROM note_embeddings);
            """;

        var result = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result ?? 0L);
    }

    /// <summary>
    /// Keeps a copy of the current database before it is replaced. A backup of an
    /// encrypted vault stays encrypted; that is deliberate, it is the same secret.
    /// </summary>
    private void Backup()
    {
        Directory.CreateDirectory(BackupsDirectory);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var target = Path.Combine(BackupsDirectory, $"kampf-{stamp}.db");

        var counter = 1;
        while (File.Exists(target))
            target = Path.Combine(BackupsDirectory, $"kampf-{stamp}-{counter++}.db");

        File.Copy(_databasePath, target, overwrite: false);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}
