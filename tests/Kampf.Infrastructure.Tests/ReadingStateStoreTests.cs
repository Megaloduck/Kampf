using Kampf.Application.Models;
using Kampf.Domain.Entities;
using Kampf.Infrastructure;
using Xunit;

namespace Kampf.Infrastructure.Tests;

/// <summary>
/// The reader resumes a paper where it was left, so the position has to be in
/// the vault — and has to disappear with the paper it described.
/// </summary>
public sealed class ReadingStateStoreTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "kampf-tests", Guid.NewGuid().ToString("n"));

    private Vault _vault = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _vault = await Vault.OpenAsync(Path.Combine(_root, "vault"));
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_position_is_stored_per_paper_and_replaced()
    {
        var document = PdfDocument.Create("paper.pdf", @"C:\vault\assets\paper.pdf", 24);
        await _vault.Repository.SaveDocumentAsync(document);

        Assert.Null(await _vault.Repository.GetReadingStateAsync(document.Id));

        await _vault.Repository.SaveReadingStateAsync(
            new ReadingState(document.Id, 7, 0.35, 1.4, DateTimeOffset.Now));

        var stored = await _vault.Repository.GetReadingStateAsync(document.Id);
        Assert.NotNull(stored);
        Assert.Equal(7, stored!.Page);
        Assert.Equal(0.35, stored.Offset, 3);
        Assert.Equal(1.4, stored.Zoom, 3);

        await _vault.Repository.SaveReadingStateAsync(
            new ReadingState(document.Id, 9, 0, 1, DateTimeOffset.Now));

        var replaced = await _vault.Repository.GetReadingStateAsync(document.Id);
        Assert.Equal(9, replaced!.Page);
        Assert.Equal(ReadingState.DefaultZoom, replaced.Zoom);
    }

    [Fact]
    public async Task Nonsense_values_are_clamped_on_the_way_back_in()
    {
        var document = PdfDocument.Create("paper.pdf", @"C:\vault\assets\paper.pdf", 10);
        await _vault.Repository.SaveDocumentAsync(document);

        await _vault.Repository.SaveReadingStateAsync(
            new ReadingState(document.Id, 0, 4.2, 99, DateTimeOffset.Now));

        var stored = await _vault.Repository.GetReadingStateAsync(document.Id);
        Assert.NotNull(stored);
        Assert.Equal(1, stored!.Page);
        Assert.Equal(1, stored.Offset);
        Assert.Equal(ReadingState.MaximumZoom, stored.Zoom);
    }

    [Fact]
    public async Task Deleting_a_paper_forgets_where_it_was_read()
    {
        var document = PdfDocument.Create("paper.pdf", @"C:\vault\assets\paper.pdf", 12);
        await _vault.Repository.SaveDocumentAsync(document);
        await _vault.Repository.SaveReadingStateAsync(ReadingState.For(document.Id));

        await _vault.Repository.DeleteDocumentAsync(document.Id);

        Assert.Null(await _vault.Repository.GetReadingStateAsync(document.Id));
    }
}
