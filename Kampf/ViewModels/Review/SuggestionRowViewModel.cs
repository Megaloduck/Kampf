using System;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Kampf.Application.Models;
using Kampf.Domain.Entities;
using Kampf.Domain.ValueObjects;

namespace Kampf.ViewModels;

/// <summary>One pending proposal in the review queue.</summary>
public sealed class SuggestionRowViewModel
{
    public SuggestionRowViewModel(
        Suggestion suggestion,
        Func<SuggestionRowViewModel, Task> accept,
        Func<SuggestionRowViewModel, Task> reject)
    {
        Suggestion = suggestion;
        AcceptCommand = new AsyncRelayCommand(() => accept(this));
        RejectCommand = new AsyncRelayCommand(() => reject(this));
    }

    public Suggestion Suggestion { get; }

    public ICommand AcceptCommand { get; }

    public ICommand RejectCommand { get; }

    public string Id => Suggestion.Id;

    public string Title => Suggestion.Title;

    public string KindLabel => Suggestion.Kind switch
    {
        SuggestionKind.CreateMissingNote => "MISSING NOTE",
        SuggestionKind.LinkNotes => "LINK",
        SuggestionKind.ApplyTag => "TAG",
        SuggestionKind.ConnectDots => "CONNECT",
        _ => "SUGGESTION",
    };

    /// <summary>Why the proposal exists, when the payload carries a reason.</summary>
    public string Detail => Suggestion.Kind switch
    {
        SuggestionKind.LinkNotes or SuggestionKind.ConnectDots =>
            SuggestionJson.Deserialize<LinkNotesPayload>(Suggestion.PayloadJson)?.Reason ?? string.Empty,
        _ => string.Empty,
    };

    public bool HasDetail => Detail.Length > 0;

    public string AgeLabel => Suggestion.CreatedAt.LocalDateTime.ToString("MMM d, HH:mm");
}
