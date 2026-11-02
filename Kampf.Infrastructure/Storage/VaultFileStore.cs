using Kampf.Application.Abstractions;

namespace Kampf.Infrastructure.Storage;

public sealed class VaultFileStore : IVaultFileStore
{
    private readonly string _assetsDirectory;

    public VaultFileStore(string assetsDirectory) => _assetsDirectory = assetsDirectory;

    public Task<string> StoreDocumentAsync(string sourcePath, CancellationToken ct = default)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("PDF not found.", sourcePath);

        Directory.CreateDirectory(_assetsDirectory);

        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var extension = Path.GetExtension(sourcePath);
        var safeStem = string.Join('_', stem.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(safeStem))
            safeStem = "document";

        var target = Path.Combine(_assetsDirectory, $"{safeStem}_{Guid.NewGuid().ToString("n")[..8]}{extension}");

        // Already inside the vault: don't copy the file onto itself.
        if (Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(target);

        File.Copy(sourcePath, target, overwrite: false);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(target);
    }

    public Task DeleteAsync(string vaultPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(vaultPath))
            return Task.CompletedTask;

        var target = Path.GetFullPath(vaultPath);
        var assetsRoot = Path.GetFullPath(_assetsDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        // Never delete outside the vault's own assets folder.
        if (!target.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to delete a file outside the vault assets folder.");

        ct.ThrowIfCancellationRequested();

        if (File.Exists(target))
            File.Delete(target);

        return Task.CompletedTask;
    }
}
