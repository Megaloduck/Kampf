using System.Text.Json;

namespace Kampf.Infrastructure.Security;

/// <summary>
/// The sidecar that says whether the vault database is encrypted and what is
/// needed to derive its key. It holds only a salt and a verifier — never the key
/// or the passphrase.
/// </summary>
public sealed record VaultLockData(
    int Version,
    bool Encrypted,
    string Kdf,
    int Iterations,
    string Salt,
    string Verifier)
{
    public const int CurrentVersion = 1;
    public const string KdfName = "pbkdf2-sha256";
}

/// <summary>Reads and writes <c>vault.lock</c> next to the database.</summary>
public static class VaultLock
{
    public const string FileName = "vault.lock";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string PathFor(string rootPath) => Path.Combine(rootPath, FileName);

    public static VaultLockData? Read(string rootPath)
    {
        var path = PathFor(rootPath);
        if (!File.Exists(path))
            return null;

        try
        {
            var data = JsonSerializer.Deserialize<VaultLockData>(File.ReadAllText(path), Options);
            return data is { Version: VaultLockData.CurrentVersion } ? data : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    public static void Write(string rootPath, VaultLockData data)
    {
        Directory.CreateDirectory(rootPath);
        File.WriteAllText(PathFor(rootPath), JsonSerializer.Serialize(data, Options));
    }

    public static void Delete(string rootPath)
    {
        var path = PathFor(rootPath);
        if (File.Exists(path))
            File.Delete(path);
    }
}

/// <summary>Raised when a vault cannot be opened without (or with the wrong) passphrase.</summary>
public sealed class VaultLockedException : Exception
{
    public VaultLockedException(string message) : base(message)
    {
    }
}
