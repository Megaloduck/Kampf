using System.Text.RegularExpressions;
using System.Xml.Linq;
using Kampf.Application.Abstractions;
using Kampf.Application.Models;
using Kampf.Application.Services;
using UglyToad.PdfPig.Content;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Kampf.Infrastructure.Pdf;

/// <summary>Text + metadata extraction with PdfPig (pure .NET, no native deps).</summary>
public sealed partial class PdfPigExtractor : IPdfExtractor
{
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Prism = "http://prismstandard.org/namespaces/basic/2.0/";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace PdfNs = "http://ns.adobe.com/pdf/1.3/";
    private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";

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

        var xmp = ReadXmp(document);
        var openingText = string.Concat(pages.Take(2).Select(p => p.Text + "\n"));

        var title = Clean(information.Title) ?? XmpValue(xmp, Dc, "title") ?? FirstMeaningfulLine(pages);
        var authors = Clean(information.Author) ?? XmpBag(xmp, Dc, "creator");
        var doi = CitationService.NormalizeDoi(XmpValue(xmp, Prism, "doi"))
                  ?? CitationService.FindDoi(openingText);
        var issn = Clean(XmpValue(xmp, Prism, "issn")) ?? FindIssn(openingText);
        var pageRange = Clean(XmpValue(xmp, Prism, "pageRange")) ?? JoinPages(
            XmpValue(xmp, Prism, "startingPage"),
            XmpValue(xmp, Prism, "endingPage")) ?? FindPageRange(openingText);

        var publication = Clean(XmpValue(xmp, Prism, "publicationName"));
        var itemType = DescribeItemType(XmpValue(xmp, Prism, "aggregationType"), publication, doi, issn);
        var abstractText = Clean(XmpValue(xmp, Dc, "description"))
                           ?? LongSubject(information.Subject);
        var keywords = Clean(XmpValue(xmp, PdfNs, "Keywords"))
                       ?? Clean(XmpValue(xmp, Prism, "keyword"))
                       ?? Clean(XmpBag(xmp, Dc, "subject"))
                       ?? Clean(information.Keywords);

        return new PdfExtraction(
            title,
            authors,
            ParseYear(information.CreationDate)
                ?? ParseYear(information.ModifiedDate)
                ?? CitationService.FindYear(XmpValue(xmp, Prism, "coverDate"))
                ?? CitationService.FindYear(XmpValue(xmp, Xmp, "CreateDate")),
            doi,
            document.NumberOfPages,
            pages,
            abstractText,
            publication,
            Clean(XmpValue(xmp, Prism, "volume")),
            Clean(XmpValue(xmp, Prism, "number")),
            pageRange,
            issn,
            Clean(XmpValue(xmp, Prism, "url")),
            keywords,
            itemType,
            Clean(XmpValue(xmp, Dc, "language")));
    }

    // ------------------------------------------------------------------ xmp

    /// <summary>
    /// Reads the document level XMP packet Zotero-style reference managers write.
    /// Absent or malformed packets simply yield no values.
    /// </summary>
    private static XDocument? ReadXmp(PigDocument document)
    {
        try
        {
            return document.TryGetXmpMetadata(out XmpMetadata? metadata)
                ? metadata.GetXDocument()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? XmpValue(XDocument? xmp, XNamespace ns, string localName)
    {
        var element = xmp?.Descendants(ns + localName).FirstOrDefault();
        return string.IsNullOrWhiteSpace(element?.Value) ? null : element.Value.Trim();
    }

    /// <summary>Reads an RDF bag/seq node (authors, keywords) as one comma separated string.</summary>
    private static string? XmpBag(XDocument? xmp, XNamespace ns, string localName)
    {
        var node = xmp?.Descendants(ns + localName).FirstOrDefault();
        if (node is null)
            return null;

        var items = node.Descendants(Rdf + "li")
            .Select(li => li.Value.Trim())
            .Where(v => v.Length > 0)
            .ToList();

        if (items.Count > 0)
            return string.Join(", ", items);

        return string.IsNullOrWhiteSpace(node.Value) ? null : node.Value.Trim();
    }

    // -------------------------------------------------------------- fallbacks

    private static string? FindIssn(string text)
    {
        var match = IssnPattern().Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Looks for "pp. 1547-1565" on the opening pages.</summary>
    private static string? FindPageRange(string text)
    {
        var match = PageRangePattern().Match(text);
        return match.Success ? $"{match.Groups[1].Value}-{match.Groups[2].Value}" : null;
    }

    private static string? JoinPages(string? start, string? end)
    {
        if (string.IsNullOrWhiteSpace(start) && string.IsNullOrWhiteSpace(end))
            return null;

        if (string.IsNullOrWhiteSpace(end))
            return start!.Trim();

        return string.IsNullOrWhiteSpace(start) ? end.Trim() : $"{start.Trim()}-{end.Trim()}";
    }

    private static string? LongSubject(string? subject)
    {
        var value = Clean(subject);
        return value is { Length: > 80 } ? value : null;
    }

    private static string DescribeItemType(string? aggregationType, string? publication, string? doi, string? issn)
    {
        var kind = aggregationType?.Trim().ToLowerInvariant();
        var label = kind switch
        {
            null or "" => null,
            "journal" or "journalarticle" => "Journal article",
            "book" => "Book",
            "bookchapter" => "Book chapter",
            "conferenceproceedings" or "conferencepaper" => "Conference paper",
            "report" => "Report",
            "thesis" or "dissertation" => "Thesis",
            _ => char.ToUpperInvariant(kind[0]) + kind[1..],
        };

        if (label is not null)
            return label;

        if (!string.IsNullOrWhiteSpace(publication) || !string.IsNullOrWhiteSpace(issn))
            return "Journal article";

        return string.IsNullOrWhiteSpace(doi) ? "Document" : "Journal article";
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

    [GeneratedRegex(@"ISSN[:\s]*([0-9]{4}-[0-9]{3}[0-9Xx])", RegexOptions.IgnoreCase)]
    private static partial Regex IssnPattern();

    [GeneratedRegex(@"\bpp?\.\s*([0-9]{1,5})\s*[-\u2013]\s*([0-9]{1,5})\b", RegexOptions.IgnoreCase)]
    private static partial Regex PageRangePattern();
}
