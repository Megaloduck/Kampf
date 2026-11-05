namespace Kampf.Application.Models;

/// <summary>
/// Where the reader left a paper: the page at the top of the viewport, how far
/// into it the viewport sat, and the zoom it was read at. Restored the next time
/// the paper is opened, so a long PDF does not start at page 1 again.
/// </summary>
public sealed record ReadingState(
    string DocumentId,
    int Page,
    double Offset,
    double Zoom,
    DateTimeOffset UpdatedAt)
{
    public const double DefaultZoom = 1.0;

    public const double MinimumZoom = 0.6;

    public const double MaximumZoom = 2.4;

    public static ReadingState For(string documentId) =>
        new(documentId, 1, 0, DefaultZoom, DateTimeOffset.Now);

    /// <summary>Clamps whatever came back from disk into the range the reader supports.</summary>
    public ReadingState Sanitized() => this with
    {
        Page = Page < 1 ? 1 : Page,
        Offset = double.IsFinite(Offset) ? Math.Clamp(Offset, 0, 1) : 0,
        Zoom = double.IsFinite(Zoom) ? Math.Clamp(Zoom, MinimumZoom, MaximumZoom) : DefaultZoom,
    };
}
