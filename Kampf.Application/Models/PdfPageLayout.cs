namespace Kampf.Application.Models;

/// <summary>
/// One word on a rendered page. <see cref="Start"/> and <see cref="Length"/> are
/// offsets into <see cref="PdfPageLayout.Text"/>, and the box is normalised to
/// 0..1 with the origin at the top-left of the page, so it can be drawn over an
/// image of any size.
/// </summary>
public readonly record struct PdfWordBox(
    string Text,
    int Start,
    int Length,
    double X,
    double Y,
    double Width,
    double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;
}

/// <summary>
/// A page's text together with the boxes that place each word on the page, so a
/// character range in <see cref="Text"/> can be drawn exactly where the PDF put
/// it. <see cref="Text"/> is normalised the same way imports normalise page
/// text, which is what stored highlights and citations are matched against.
/// </summary>
public sealed record PdfPageLayout(
    int Page,
    string Text,
    double WidthPoints,
    double HeightPoints,
    IReadOnlyList<PdfWordBox> Words);

/// <summary>A rendered page as 32-bit BGRA pixels, rows from top to bottom.</summary>
public sealed record PdfPageImage(int Page, int Width, int Height, byte[] Pixels);
