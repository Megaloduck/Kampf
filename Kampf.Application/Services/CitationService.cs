using System.Text.RegularExpressions;
using Kampf.Application.Models;
using Kampf.Domain.Entities;

namespace Kampf.Application.Services;

/// <summary>
/// Citation metadata capture: normalises whatever a PDF carries into a
/// <see cref="Citation"/> the reader can trust.
/// </summary>
public static partial class CitationService
{
    public static Citation FromExtraction(PdfExtraction extraction) => new(
        string.IsNullOrWhiteSpace(extraction.Title) ? "Untitled document" : extraction.Title.Trim(),
        extraction.Authors ?? string.Empty,
        extraction.Year,
        NormalizeDoi(extraction.Doi));

    public static Citation FromDocument(PdfDocument document) => new(
        document.Title,
        document.Authors,
        document.Year,
        document.Doi);

    /// <summary>Accepts "10.x/y", "doi:10.x/y" and "https://doi.org/10.x/y".</summary>
    public static string? NormalizeDoi(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var value = raw.Trim();
        value = value.Replace("https://doi.org/", "", StringComparison.OrdinalIgnoreCase)
                     .Replace("http://doi.org/", "", StringComparison.OrdinalIgnoreCase)
                     .Replace("https://dx.doi.org/", "", StringComparison.OrdinalIgnoreCase)
                     .Replace("doi:", "", StringComparison.OrdinalIgnoreCase)
                     .Trim();

        value = value.TrimEnd('.', ',', ';', ')', ']');
        return DoiPattern().IsMatch(value) ? value : null;
    }

    /// <summary>Finds the first DOI-looking string in extracted text.</summary>
    public static string? FindDoi(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = DoiSearch().Match(text);
        return match.Success ? NormalizeDoi(match.Value) : null;
    }

    /// <summary>Tries to pull a four digit publication year out of a metadata date.</summary>
    public static int? FindYear(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // PDF dates are often compact ("D:20190314120000+01'00'"), so look at the
        // first four digits of each digit run, not only standalone numbers.
        foreach (Match run in YearPattern().Matches(text))
        {
            if (run.Value.Length < 4)
                continue;

            if (int.TryParse(run.Value[..4], out var year) && year is > 1400 and < 2200)
                return year;
        }

        return null;
    }

    [GeneratedRegex(@"^10\.\d{4,9}/\S+$", RegexOptions.Compiled)]
    private static partial Regex DoiPattern();

    [GeneratedRegex(@"10\.\d{4,9}/[-._;()/:a-zA-Z0-9]+", RegexOptions.Compiled)]
    private static partial Regex DoiSearch();

    [GeneratedRegex(@"\d{4,}", RegexOptions.Compiled)]
    private static partial Regex YearPattern();
}
