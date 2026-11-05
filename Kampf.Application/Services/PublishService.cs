using System.Text;
using Kampf.Application.Abstractions;
using Kampf.Domain.Entities;

namespace Kampf.Application.Services;

/// <summary>What a publish run wrote.</summary>
public sealed record PublishResult(string Directory, string IndexPath, int Notes, int Papers, int Links);

/// <summary>
/// Writes a static, self-contained copy of the vault: one HTML page per note,
/// a page for the papers those notes came from, an index, and a stylesheet.
/// Nothing is uploaded and no network is touched — publishing is a folder the
/// researcher can copy, zip, commit or serve.
/// </summary>
public sealed class PublishService
{
    private readonly IVaultRepository _repository;

    public PublishService(IVaultRepository repository) => _repository = repository;

    public async Task<PublishResult> PublishAsync(string directory, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("A publish folder is required.", nameof(directory));

        var notes = await _repository.ListNotesAsync(ct);
        var documents = await _repository.ListDocumentsAsync(ct);
        var links = await _repository.ListLinksAsync(ct);

        var notesDirectory = Path.Combine(directory, "notes");
        Directory.CreateDirectory(notesDirectory);

        var notesById = notes.ToDictionary(note => note.Id, StringComparer.Ordinal);
        var documentsById = documents.ToDictionary(document => document.Id, StringComparer.Ordinal);

        // Wikilinks are resolved by title, exactly as the vault resolves them.
        var idByTitle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var note in notes)
        {
            var title = note.Title.Trim();
            if (title.Length > 0)
                idByTitle.TryAdd(title, note.Id);
        }

        string? Resolve(string target) =>
            idByTitle.TryGetValue(target.Trim(), out var id) ? FileName(id) : null;

        var outgoing = links
            .GroupBy(link => link.SourceId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(l => l.TargetId).Distinct().ToList(), StringComparer.Ordinal);

        var incoming = links
            .GroupBy(link => link.TargetId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(l => l.SourceId).Distinct().ToList(), StringComparer.Ordinal);

        var notesByDocument = notes
            .Where(note => note.SourceDocumentId is not null)
            .GroupBy(note => note.SourceDocumentId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var note in notes)
        {
            ct.ThrowIfCancellationRequested();

            var html = BuildNotePage(
                note,
                documentsById,
                notesById,
                outgoing.GetValueOrDefault(note.Id) ?? new List<string>(),
                incoming.GetValueOrDefault(note.Id) ?? new List<string>(),
                Resolve);

            await File.WriteAllTextAsync(
                Path.Combine(notesDirectory, FileName(note.Id)),
                html,
                new UTF8Encoding(false),
                ct);
        }

        await File.WriteAllTextAsync(
            Path.Combine(directory, "papers.html"),
            BuildPapersPage(documents, notesByDocument),
            new UTF8Encoding(false),
            ct);

        var indexPath = Path.Combine(directory, "index.html");
        await File.WriteAllTextAsync(
            indexPath,
            BuildIndexPage(notes, documents),
            new UTF8Encoding(false),
            ct);

        await File.WriteAllTextAsync(
            Path.Combine(directory, "style.css"),
            StyleSheet,
            new UTF8Encoding(false),
            ct);

        return new PublishResult(directory, indexPath, notes.Count, documents.Count, links.Count);
    }

    /// <summary>Stable file name for a note; ids are the only thing guaranteed unique.</summary>
    public static string FileName(string noteId)
    {
        var safe = new string(noteId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return $"note-{(safe.Length == 0 ? "untitled" : safe)}.html";
    }

    // ------------------------------------------------------------------ pages

    private static string BuildNotePage(
        Note note,
        IReadOnlyDictionary<string, PdfDocument> documents,
        IReadOnlyDictionary<string, Note> notes,
        IReadOnlyList<string> outgoing,
        IReadOnlyList<string> incoming,
        Func<string, string?> resolve)
    {
        var body = new StringBuilder();

        body.Append("<nav><a href=\"../index.html\">All notes</a>")
            .Append(" \u00b7 ")
            .Append("<a href=\"../papers.html\">Papers</a></nav>");

        body.Append("<article>");
        body.Append("<h1>").Append(MarkdownHtml.Escape(note.Title)).AppendLine("</h1>");

        body.Append("<p class=\"meta\">")
            .Append(note.UpdatedAt.LocalDateTime.ToString("MMM d, yyyy"))
            .Append(note.Tags.Count == 0
                ? string.Empty
                : " \u00b7 " + string.Join(' ', note.Tags.Select(tag =>
                    $"<span class=\"tag\">#{MarkdownHtml.Escape(tag.Name)}</span>")))
            .Append("</p>");

        if (note.SourceDocumentId is { } documentId &&
            documents.TryGetValue(documentId, out var document))
        {
            body.Append("<p class=\"provenance\">Anchored to <a href=\"../papers.html#doc-")
                .Append(MarkdownHtml.Escape(document.Id))
                .Append("\">")
                .Append(MarkdownHtml.Escape(document.Title))
                .Append("</a>")
                .Append(note.SourcePage is { } page ? $", page {page}" : string.Empty)
                .AppendLine("</p>");
        }

        body.Append(MarkdownHtml.ToHtml(note.Body, resolve));
        body.AppendLine("</article>");

        if (outgoing.Count > 0)
            body.Append(LinkList("Links to", outgoing, notes));

        if (incoming.Count > 0)
            body.Append(LinkList("Linked from", incoming, notes));

        return Page(note.Title, body.ToString(), "../style.css");
    }

    private static string LinkList(string heading, IReadOnlyList<string> ids, IReadOnlyDictionary<string, Note> notes)
    {
        var builder = new StringBuilder();
        builder.Append("<section class=\"links\"><h2>").Append(heading).AppendLine("</h2><ul>");

        foreach (var id in ids)
        {
            if (!notes.TryGetValue(id, out var note))
                continue;

            builder.Append("<li><a href=\"").Append(FileName(id)).Append("\">")
                .Append(MarkdownHtml.Escape(note.Title))
                .AppendLine("</a></li>");
        }

        return builder.AppendLine("</ul></section>").ToString();
    }

    private static string BuildPapersPage(
        IReadOnlyList<PdfDocument> documents,
        IReadOnlyDictionary<string, List<Note>> notesByDocument)
    {
        var body = new StringBuilder();

        body.Append("<nav><a href=\"index.html\">All notes</a></nav>");
        body.Append("<h1>Papers</h1>");

        if (documents.Count == 0)
            body.AppendLine("<p class=\"meta\">No papers imported yet.</p>");

        foreach (var document in documents.OrderBy(d => d.Title, StringComparer.OrdinalIgnoreCase))
        {
            body.Append("<section class=\"paper\" id=\"doc-")
                .Append(MarkdownHtml.Escape(document.Id))
                .AppendLine("\">");

            body.Append("<h2>").Append(MarkdownHtml.Escape(document.Title)).AppendLine("</h2>");

            var citation = new List<string>();
            if (!string.IsNullOrWhiteSpace(document.Authors))
                citation.Add(MarkdownHtml.Escape(document.Authors));
            citation.Add(document.Year?.ToString() ?? "n.d.");
            if (!string.IsNullOrWhiteSpace(document.Publication))
                citation.Add(MarkdownHtml.Escape(document.Publication));

            body.Append("<p class=\"meta\">").Append(string.Join(" \u00b7 ", citation)).AppendLine("</p>");

            if (!string.IsNullOrWhiteSpace(document.Abstract))
                body.Append("<p>").Append(MarkdownHtml.Escape(document.Abstract)).AppendLine("</p>");

            if (!string.IsNullOrWhiteSpace(document.Doi))
                body.Append("<p class=\"meta\">DOI: ").Append(MarkdownHtml.Escape(document.Doi)).AppendLine("</p>");

            if (notesByDocument.TryGetValue(document.Id, out var anchored) && anchored.Count > 0)
            {
                body.AppendLine("<h3>Notes from this paper</h3><ul>");
                foreach (var note in anchored)
                    body.Append("<li><a href=\"notes/").Append(FileName(note.Id)).Append("\">")
                        .Append(MarkdownHtml.Escape(note.Title))
                        .Append("</a>")
                        .Append(note.SourcePage is { } page ? $" <span class=\"meta\">p. {page}</span>" : string.Empty)
                        .AppendLine("</li>");
                body.AppendLine("</ul>");
            }

            body.AppendLine("</section>");
        }

        return Page("Papers", body.ToString(), "style.css");
    }

    private static string BuildIndexPage(
        IReadOnlyList<Note> notes,
        IReadOnlyList<PdfDocument> documents)
    {
        var body = new StringBuilder();
        body.Append("<h1>Notes</h1>");
        body.Append("<p class=\"meta\">")
            .Append(notes.Count).Append(notes.Count == 1 ? " note" : " notes")
            .Append(" \u00b7 ")
            .Append(documents.Count).Append(documents.Count == 1 ? " paper" : " papers")
            .Append(" \u00b7 <a href=\"papers.html\">papers</a>")
            .AppendLine("</p>");

        if (notes.Count == 0)
            body.AppendLine("<p class=\"meta\">Nothing published yet.</p>");

        body.AppendLine("<ul class=\"notes\">");
        foreach (var note in notes.OrderByDescending(n => n.UpdatedAt))
        {
            body.Append("<li><a href=\"notes/").Append(FileName(note.Id)).Append("\">")
                .Append(MarkdownHtml.Escape(note.Title))
                .Append("</a>")
                .Append(" <span class=\"meta\">")
                .Append(note.UpdatedAt.LocalDateTime.ToString("MMM d, yyyy"))
                .Append(note.Tags.Count == 0
                    ? string.Empty
                    : " \u00b7 " + string.Join(", ", note.Tags.Select(t => "#" + MarkdownHtml.Escape(t.Name))))
                .AppendLine("</span></li>");
        }

        body.AppendLine("</ul>");
        body.Append("<p class=\"footer\">Published from a local Kampf vault.</p>");

        return Page("Notes", body.ToString(), "style.css");
    }

    private static string Page(string title, string body, string styleSheet) => $"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8"/>
        <meta name="viewport" content="width=device-width, initial-scale=1"/>
        <title>{MarkdownHtml.Escape(title)}</title>
        <link rel="stylesheet" href="{styleSheet}"/>
        </head>
        <body>
        {body}
        </body>
        </html>
        """;

    private const string StyleSheet = """
        :root { color-scheme: light dark; }
        body {
            margin: 0 auto; padding: 40px 22px 80px; max-width: 720px;
            font: 16px/1.65 "Inter", -apple-system, Segoe UI, Roboto, sans-serif;
        }
        h1 { font-size: 1.8rem; line-height: 1.25; margin: 0 0 12px; }
        h2 { font-size: 1.25rem; margin: 26px 0 8px; }
        h3 { font-size: 1.05rem; margin: 20px 0 6px; }
        nav, .meta, .footer { color: #6b7280; font-size: 0.85rem; }
        nav { margin-bottom: 22px; }
        a { color: #3b6fd4; }
        .tag { margin-left: 4px; }
        blockquote {
            margin: 16px 0; padding: 6px 14px;
            border-left: 3px solid #c3cbd9; color: #4b5563;
        }
        pre {
            padding: 12px 14px; overflow: auto; border-radius: 6px;
            background: rgba(127, 127, 127, 0.12);
        }
        code { font-family: ui-monospace, Consolas, Menlo, monospace; font-size: 0.9em; }
        .unresolved { color: #9ca3af; }
        section.paper { border-top: 1px solid rgba(127,127,127,0.25); padding-top: 18px; margin-top: 22px; }
        .footer { margin-top: 40px; }
        """;
}
