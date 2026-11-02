namespace Kampf.Application.Abstractions;

/// <summary>
/// Copies imported attachments into the vault so a document keeps working even
/// if the original file moves. Also owns the human-readable markdown mirror.
/// </summary>
public interface IVaultFileStore
{
    /// <summary>Copies a PDF into the vault and returns its vault path.</summary>
    Task<string> StoreDocumentAsync(string sourcePath, CancellationToken ct = default);

    /// <summary>
    /// Removes a previously stored file. Implementations must refuse paths
    /// outside the vault's assets folder.
    /// </summary>
    Task DeleteAsync(string vaultPath, CancellationToken ct = default);
}
