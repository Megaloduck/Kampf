using System.Text;
using Kampf.Application.Abstractions;
using Kampf.Domain.Entities;

namespace Kampf.Application.Services;

/// <summary>
/// Phase 2's summaries: a short brief of a paper written by the local model from
/// its title, abstract and page text. The brief is only returned here — nothing
/// is written to the vault, so the researcher reads it, then decides whether it
/// becomes an anchored note.
/// </summary>
public sealed class DocumentBriefService
{
    /// <summary>How much page text is sent for one brief.</summary>
    private const int MaxInputCharacters = 8000;

    private const string SystemPrompt =
        "You write briefs of research papers for a private research notebook. " +
        "Use only the supplied title, abstract and page text. Give five short bullets: " +
        "the question, the method, the main finding, the limitation, and why it might matter. " +
        "Quote no number you cannot see in the text. If part of the paper is missing, say which. " +
        "No preamble, no headings, no invented citations, at most 120 words.";

    private readonly IChatModel? _model;

    public DocumentBriefService(IChatModel? model = null) => _model = model;

    public bool HasLocalModel => _model is not null;

    /// <summary>Shown with the brief so it is always clear what wrote it.</summary>
    public string ModelLabel => _model?.Name ?? "no local model";

    public async Task<string> BriefAsync(
        PdfDocument document,
        IReadOnlyList<string> pages,
        CancellationToken ct = default)
    {
        if (_model is null)
        {
            throw new InvalidOperationException(
                "A brief needs a local model. Choose one in Settings, then try again.");
        }

        var brief = (await _model.CompleteAsync(SystemPrompt, BuildPrompt(document, pages), ct)).Trim();

        if (brief.Length == 0)
            throw new InvalidOperationException("The local model returned an empty brief.");

        return brief;
    }

    internal static string BuildPrompt(PdfDocument document, IReadOnlyList<string> pages)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Title: " + document.Title);

        if (!string.IsNullOrWhiteSpace(document.Authors))
            builder.AppendLine("Authors: " + document.Authors);

        if (document.Year is { } year)
            builder.AppendLine("Year: " + year);

        if (!string.IsNullOrWhiteSpace(document.Abstract))
        {
            builder.AppendLine();
            builder.AppendLine("Abstract:");
            builder.AppendLine(document.Abstract.Trim());
        }

        var body = new StringBuilder();
        foreach (var page in pages)
        {
            if (body.Length >= MaxInputCharacters)
                break;

            body.AppendLine(page.Trim());
            body.AppendLine();
        }

        if (body.Length > MaxInputCharacters)
            body.Length = MaxInputCharacters;

        if (body.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Page text:");
            builder.Append(body);
        }

        return builder.ToString().TrimEnd();
    }
}
