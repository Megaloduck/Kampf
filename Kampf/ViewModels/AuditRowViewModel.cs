using Kampf.Domain.Entities;

namespace Kampf.ViewModels;

/// <summary>One audit-trail line, shown so decisions stay visible.</summary>
public sealed class AuditRowViewModel
{
    public AuditRowViewModel(AuditEntry entry) => Entry = entry;

    public AuditEntry Entry { get; }

    public string Action => Entry.Action switch
    {
        "note.created" => "Note created",
        "note.deleted" => "Note deleted",
        "document.imported" => "PDF imported",
        "highlight.captured" => "Highlight captured",
        "suggestion.accepted" => "Suggestion accepted",
        "suggestion.rejected" => "Suggestion rejected",
        _ => Entry.Action,
    };

    public string Detail => Entry.Detail;

    public string TimeLabel => Entry.At.LocalDateTime.ToString("HH:mm");
}
