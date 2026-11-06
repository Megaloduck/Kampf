using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Kampf.Application.Models;

namespace Kampf.ViewModels;

/// <summary>
/// One clickable source under an answer: opens the note it points at, or the
/// paper at the cited page when the citation is to a paper's own text.
/// </summary>
public sealed class ChatCitationViewModel
{
    public ChatCitationViewModel(
        ChatCitation citation,
        Func<string, Task> openNote,
        Func<string, int, string?, Task>? openPage = null)
    {
        Citation = citation;
        OpenCommand = new AsyncRelayCommand(() => citation.IsPage && openPage is not null
            ? openPage(citation.DocumentId!, citation.Page ?? 1, citation.Excerpt)
            : openNote(citation.NoteId));
    }

    public ChatCitation Citation { get; }

    public ICommand OpenCommand { get; }

    public string Label => Citation.Label;

    public string? SourceLabel => Citation.SourceLabel;

    public bool HasSource => SourceLabel is not null;

    public string ToolTip => string.IsNullOrWhiteSpace(Citation.Excerpt)
        ? (Citation.IsPage ? $"Open the paper at page {Citation.Page}" : "Open this note")
        : Citation.Excerpt;
}

/// <summary>
/// A question worth asking next. Clicking one asks it, so following a thread
/// costs no typing.
/// </summary>
public sealed class ChatSuggestionViewModel
{
    public ChatSuggestionViewModel(string text, Func<string, Task> ask)
    {
        Text = text;
        AskCommand = new AsyncRelayCommand(() => ask(text));
    }

    public string Text { get; }

    public ICommand AskCommand { get; }
}

/// <summary>
/// A model the configured server reported, offered as a chip in Settings so the
/// exact id can be picked instead of typed from memory.
/// </summary>
public sealed class ChatModelChoiceViewModel
{
    public ChatModelChoiceViewModel(string name, Action<string> use)
    {
        Name = name;
        UseCommand = new RelayCommand(() => use(name));
    }

    public string Name { get; }

    public ICommand UseCommand { get; }
}

/// <summary>A question and the cited answer it received.</summary>
public sealed class ChatExchangeViewModel
{
    public ChatExchangeViewModel(
        string question,
        string answer,
        IReadOnlyList<ChatCitationViewModel> citations,
        DateTimeOffset at,
        string? warning,
        IReadOnlyList<ChatSuggestionViewModel>? suggestions = null)
    {
        Question = question;
        Answer = answer;
        Citations = citations;
        Warning = warning;
        Suggestions = suggestions ?? Array.Empty<ChatSuggestionViewModel>();
        TimeLabel = at.LocalDateTime.ToString("MMM d, HH:mm");
    }

    public string Question { get; }

    public string Answer { get; }

    public IReadOnlyList<ChatCitationViewModel> Citations { get; }

    public string? Warning { get; }

    public bool HasWarning => !string.IsNullOrEmpty(Warning);

    public bool HasCitations => Citations.Count > 0;

    public IReadOnlyList<ChatSuggestionViewModel> Suggestions { get; }

    public bool HasSuggestions => Suggestions.Count > 0;

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
