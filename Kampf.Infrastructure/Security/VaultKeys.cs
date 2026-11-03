using System.Security.Cryptography;
using System.Text;

namespace Kampf.Infrastructure.Security;

/// <summary>
/// Turns a passphrase into the raw SQLCipher key. The key is never stored: the
/// lock file keeps a salt and a verifier so the app can tell a wrong passphrase
/// from a corrupt vault without keeping anything that opens it.
/// </summary>
public static class VaultKeys
{
    /// <summary>PBKDF2-HMAC-SHA256 work factor; high enough to make guessing costly.</summary>
    public const int DefaultIterations = 600_000;

    private const int SaltBytes = 16;
    private const int KeyBytes = 32;
    private static readonly byte[] VerifierContext = Encoding.UTF8.GetBytes("kampf-vault-v1");

    /// <summary>Creates the lock material for a fresh passphrase.</summary>
    public static VaultLockData CreateLock(string passphrase, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = DeriveKey(passphrase, salt, iterations);
        return new VaultLockData(
            VaultLockData.CurrentVersion,
            Encrypted: true,
            VaultLockData.KdfName,
            iterations,
            Convert.ToBase64String(salt),
            Convert.ToBase64String(Verifier(key)));
    }

    /// <summary>Returns the 32-byte key, or null when the passphrase is wrong.</summary>
    public static byte[]? Unlock(VaultLockData data, string passphrase)
    {
        try
        {
            var salt = Convert.FromBase64String(data.Salt);
            var expected = Convert.FromBase64String(data.Verifier);
            var key = DeriveKey(passphrase, salt, data.Iterations);
            return CryptographicOperations.FixedTimeEquals(Verifier(key), expected) ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The key in the form SQLCipher expects after <c>PRAGMA key =</c>.</summary>
    public static string SqlCipherKey(byte[] key) => $"x'{Convert.ToHexString(key)}'";

    /// <summary>Hex form used by <see cref="Storage.SqliteVaultRepository"/>.</summary>
    public static string HexKey(byte[] key) => Convert.ToHexString(key);

    private static byte[] DeriveKey(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, KeyBytes);

    private static byte[] Verifier(byte[] key) =>
        HMACSHA256.HashData(key, VerifierContext)[..16];
}
