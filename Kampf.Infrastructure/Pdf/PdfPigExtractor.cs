using Kampf.Application.Abstractions;
using Kampf.Application.Models;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Kampf.Infrastructure.Pdf;

/// <summary>Text + metadata extraction with PdfPig (pure .NET, no native deps).</summary>
public sealed class PdfPigExtractor : IPdfExtractor
{
    public Task<PdfExtraction> ExtractAsync(string filePath, CancellationToken ct = default) =>
        Task.Run(() => Extract(filePath, ct), ct);

    private static PdfExtraction Extract(string filePath, CancellationToken ct)
    {
        using var document = PigDocument.Open(filePath);
        var information = document.Information;
        var pages = new List<PdfPageText>(document.NumberOfPages);

        for (var number = 1; number <= document.NumberOfPages; number++)
        {
            ct.ThrowIfCancellationRequested();

            string text;
            try
            {
                text = document.GetPage(number).Text ?? string.Empty;
            }
            catch (Exception)
            {
                // A malformed page should not abort the whole import.
                text = string.Empty;
            }

            pages.Add(new PdfPageText(number, NormalizeWhitespace(text)));
        }

        var title = Clean(information.Title) ?? FirstMeaningfulLine(pages);

        return new PdfExtraction(
            title,
            Clean(information.Author),
            ParseYear(information.CreationDate) ?? ParseYear(information.ModifiedDate),
            null,
            document.NumberOfPages,
            pages);
    }

    private static string? FirstMeaningfulLine(IReadOnlyList<PdfPageText> pages)
    {
        foreach (var page in pages.Take(1))
        {
            foreach (var line in page.Text.Split('\n'))
            {
                var candidate = line.Trim();
                if (candidate.Length is > 8 and < 200)
                    return candidate;
            }
        }

        return null;
    }

    /// <summary>Collapses the ragged line breaks PDF extraction produces.</summary>
    private static string NormalizeWhitespace(string text)
    {
        var lines = text
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);

        return string.Join('\n', lines);
    }

    private static int? ParseYear(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        foreach (var part in value.Split(' ', '/', '-', ':', 'T'))
        {
            if (part.Length == 4 && int.TryParse(part, out var year) && year is > 1400 and < 2200)
                return year;
        }

        return null;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
