using Kampf.Application.Services;
using Kampf.Domain.Entities;
using Kampf.Domain.ValueObjects;
using Xunit;

namespace Kampf.Application.Tests;

/// <summary>
/// Publishing writes a static copy of the vault. These tests treat the output
/// as a reader would: links resolve, escapes hold, and the papers page carries
/// the notes captured from each paper.
/// </summary>
public sealed class PublishServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "kampf-tests", Guid.NewGuid().ToString("n"));

    public PublishServiceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Every_note_gets_a_page_and_wikilinks_resolve()
    {
        var repo = new FakeVaultRepository();

        var target = Note.Create("Graph theory", "Dense clusters matter.");
        var source = Note.Create("Modularity", "See [[Graph theory]] and [[Missing note]].");
        await repo.SaveNoteAsync(target);
        await repo.SaveNoteAsync(source);
        await repo.AddLinkAsync(Link.Create(source.Id, target.Id, EdgeKind.Wiki));

        var result = await new PublishService(repo).PublishAsync(_directory);

        Assert.Equal(2, result.Notes);
        Assert.Equal(1, result.Links);
        Assert.True(File.Exists(result.IndexPath));

        var page = await File.ReadAllTextAsync(Path.Combine(_directory, "notes", PublishService.FileName(source.Id)));
        Assert.Contains($"href=\"{PublishService.FileName(target.Id)}\"", page);
        Assert.Contains("class=\"unresolved\">Missing note</span>", page);

        // Backlinks are rendered on the target's page.
        var targetPage = await File.ReadAllTextAsync(
            Path.Combine(_directory, "notes", PublishService.FileName(target.Id)));
        Assert.Contains("Linked from", targetPage);
        Assert.Contains("Modularity", targetPage);
    }

    [Fact]
    public async Task Markup_in_a_note_is_escaped_and_markdown_becomes_html()
    {
        var repo = new FakeVaultRepository();
        var note = Note.Create("Injection <script>alert(1)</script>",
            "## Heading\n\n**bold** and *italic* with `code`\n\n- one\n- two\n");
        await repo.SaveNoteAsync(note);

        await new PublishService(repo).PublishAsync(_directory);

        var page = await File.ReadAllTextAsync(Path.Combine(_directory, "notes", PublishService.FileName(note.Id)));

        Assert.DoesNotContain("<script>", page);
        Assert.Contains("&lt;script&gt;", page);
        Assert.Contains("<h2>Heading</h2>", page);
        Assert.Contains("<strong>bold</strong>", page);
        Assert.Contains("<em>italic</em>", page);
        Assert.Contains("<code>code</code>", page);
        Assert.Contains("<li>one</li>", page);
    }

    [Fact]
    public async Task Papers_page_carries_citation_and_the_notes_taken_from_them()
    {
        var repo = new FakeVaultRepository();
        var document = PdfDocument.Create("modularity.pdf", @"C:\vault\assets\modularity.pdf", 14);
        document.Authors = "Newman, M.";
        document.Year = 2006;
        document.Publication = "Physical Review E";
        await repo.SaveDocumentAsync(document);

        var note = Note.Create("Edge density", "> A quote.\n\nMy reading.");
        note.SourceDocumentId = document.Id;
        note.SourcePage = 4;
        await repo.SaveNoteAsync(note);

        var result = await new PublishService(repo).PublishAsync(_directory);
        Assert.Equal(1, result.Papers);

        var papers = await File.ReadAllTextAsync(Path.Combine(_directory, "papers.html"));
        Assert.Contains("Newman, M.", papers);
        Assert.Contains("Physical Review E", papers);
        Assert.Contains($"id=\"doc-{document.Id}\"", papers);
        Assert.Contains($"notes/{PublishService.FileName(note.Id)}", papers);
        Assert.Contains("p. 4", papers);

        var notePage = await File.ReadAllTextAsync(Path.Combine(_directory, "notes", PublishService.FileName(note.Id)));
        Assert.Contains($"../papers.html#doc-{document.Id}", notePage);
    }

    [Fact]
    public async Task Publishing_again_overwrites_rather_than_duplicating()
    {
        var repo = new FakeVaultRepository();
        var note = Note.Create("One note", "Body.");
        await repo.SaveNoteAsync(note);

        var service = new PublishService(repo);
        await service.PublishAsync(_directory);
        await service.PublishAsync(_directory);

        var files = Directory.GetFiles(Path.Combine(_directory, "notes"));
        Assert.Single(files);

        var index = await File.ReadAllTextAsync(Path.Combine(_directory, "index.html"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(index, "One note"));
    }

    [Fact]
    public async Task An_empty_vault_publishes_nothing_but_a_placeholder()
    {
        var repo = new FakeVaultRepository();

        var result = await new PublishService(repo).PublishAsync(_directory);

        Assert.Equal(0, result.Notes);
        var index = await File.ReadAllTextAsync(result.IndexPath);
        Assert.Contains("Nothing published yet", index);
    }
}
