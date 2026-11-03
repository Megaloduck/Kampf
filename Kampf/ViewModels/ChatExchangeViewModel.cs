using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Kampf.Application.Models;

namespace Kampf.ViewModels;

/// <summary>One clickable source under an answer; opens the note it points at.</summary>
public sealed class ChatCitationViewModel
{
    public ChatCitationViewModel(ChatCitation citation, Func<string, Task> openNote)
    {
        Citation = citation;
        OpenCommand = new AsyncRelayCommand(() => openNote(citation.NoteId));
    }

    public ChatCitation Citation { get; }

    public ICommand OpenCommand { get; }

    public string Label => Citation.Label;

    public string? SourceLabel => Citation.SourceLabel;

    public bool HasSource => SourceLabel is not null;

    public string ToolTip => string.IsNullOrWhiteSpace(Citation.Excerpt)
        ? "Open this note"
        : Citation.Excerpt;
}

/// <summary>A question and the cited answer it received.</summary>
public sealed class ChatExchangeViewModel
{
    public ChatExchangeViewModel(
        string question,
        string answer,
        IReadOnlyList<ChatCitationViewModel> citations,
        DateTimeOffset at,
        string? warning)
    {
        Question = question;
        Answer = answer;
        Citations = citations;
        Warning = warning;
        TimeLabel = at.LocalDateTime.ToString("MMM d, HH:mm");
    }

    public string Question { get; }

    public string Answer { get; }

    public IReadOnlyList<ChatCitationViewModel> Citations { get; }

    public string? Warning { get; }

    public bool HasWarning => !string.IsNullOrEmpty(Warning);

    public bool HasCitations => Citations.Count > 0;

    public string TimeLabel { get; }

    /// <summary>Shown under the answer so provenance is visible, not implied.</summary>
    public string GroundingLabel => Citations.Count switch
    {
        0 => "no sources",
        1 => "1 source",
        _ => $"{Citations.Count} sources",
    };

    public bool IsUnverified => Citations.Count == 0;
}
