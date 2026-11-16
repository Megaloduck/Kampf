using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Kampf.ViewModels;

/// <summary>One entry under a folder in the navigator: a note or a paper.</summary>
public sealed partial class NavigatorRowViewModel : ObservableObject
{
    private readonly Action<string> _select;

    public NavigatorRowViewModel(string id, string title, string detail, string toolTip, Action<string> select)
    {
        Id = id;
        Title = title;
        Detail = detail;
        ToolTip = toolTip;
        _select = select;
    }

    public string Id { get; }

    public string Title { get; }

    /// <summary>Small trailing text: the page a paper matched on, or a note's anchor.</summary>
    public string Detail { get; }

    public string ToolTip { get; }

    public bool HasDetail => Detail.Length > 0;

    [ObservableProperty] private bool _isSelected;

    [RelayCommand]
    private void Select() => _select(Id);
}

/// <summary>
/// A folder in the navigator: a tag with its notes, or the papers. Kept flat
/// rather than bound to a TreeView so the header can carry its own chevron,
/// count and add button.
/// </summary>
public sealed partial class NavigatorFolderViewModel : ObservableObject
{
    public NavigatorFolderViewModel(
        string title,
        IEnumerable<NavigatorRowViewModel> rows,
        string countLabel,
        Action<string>? addNote = null,
        bool isExpanded = true)
    {
        Title = title;
        Rows = new ObservableCollection<NavigatorRowViewModel>(rows);
        CountLabel = countLabel;
        CanAddNote = addNote is not null;
        AddNoteCommand = addNote is null ? null : new RelayCommand(() => addNote(Title));
        _isExpanded = isExpanded;
    }

    public string Title { get; }

    public ObservableCollection<NavigatorRowViewModel> Rows { get; }

    public string CountLabel { get; }

    /// <summary>Folders of notes take a new note; the papers folder does not.</summary>
    public bool CanAddNote { get; }

    public IRelayCommand? AddNoteCommand { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCollapsed))]
    private bool _isExpanded;

    public bool IsCollapsed => !IsExpanded;

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}
