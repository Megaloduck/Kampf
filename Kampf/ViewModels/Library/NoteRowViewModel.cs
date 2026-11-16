using System.Linq;
using Kampf.Domain.Entities;

namespace Kampf.ViewModels;

/// <summary>Display projection for one note in the sidebar list.</summary>
public sealed class NoteRowViewModel
{
    public NoteRowViewModel(Note note) => Note = note;

    public Note Note { get; }

    public string Id => Note.Id;

    public string Title => Note.Title;

    public string TagLine => Note.Tags.Count == 0
        ? string.Empty
        : string.Join("  ", Note.Tags.Select(t => "#" + t.Name));

    public bool HasTags => Note.Tags.Count > 0;

    public string ProvenanceLabel => Note.SourcePage is null
        ? string.Empty
        : $"p. {Note.SourcePage}";

    public bool HasProvenance => Note.SourcePage is not null;

    public string UpdatedLabel => Note.UpdatedAt.LocalDateTime.ToString("MMM d, HH:mm");

    public string Preview => Note.Body.Length == 0
        ? "No content yet"
        : Note.Body.Replace("\n", " ").Trim() is var flat && flat.Length > 90
            ? flat[..90] + "\u2026"
            : flat;
}
