using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kampf.Controls;

namespace Kampf.ViewModels;

/// <summary>One entry under a folder in the navigator: a note or a paper.</summary>
public sealed partial class NavigatorRowViewModel : ObservableObject
{
    private readonly Action<string> _select;

    public NavigatorRowViewModel(
        string id,
        string title,
        string detail,
        string toolTip,
        Action<string> select,
        bool isPaper = false)
    {
        Id = id;
        Title = title;
        Detail = detail;
        ToolTip = toolTip;
        _select = select;
        IsPaper = isPaper;
    }

    public string Id { get; }

    public string Title { get; }

    /// <summary>Small trailing text: the page a paper matched on, or a note's anchor.</summary>
    public string Detail { get; }

    public string ToolTip { get; }

    /// <summary>An imported paper rather than a note in the vault.</summary>
    public bool IsPaper { get; }

    /// <summary>A paper and a note get different glyphs, so a row reads at a glance.</summary>
    public string Icon => IsPaper ? Symbols.Description : Symbols.StickyNote;

    public bool HasDetail => Detail.Length > 0;

    [ObservableProperty] private bool _isSelected;

    [RelayCommand]
    private void Select() => _select(Id);
}

/// <summary>
/// A folder in the navigator: a tag with its notes, one the researcher added
/// before any note carried it, or the papers. Kept flat rather than bound to a
/// TreeView so the header can carry its own chevron, count and add menu.
/// </summary>
public sealed partial class NavigatorFolderViewModel : ObservableObject
{
    private readonly Func<string, string, bool>? _rename;

    public NavigatorFolderViewModel(
        string title,
        IEnumerable<NavigatorRowViewModel> rows,
        string countLabel,
        Action<string>? addNote = null,
        IRelayCommand? importPapers = null,
        bool isExpanded = true,
        Func<string, string, bool>? rename = null,
        Action<string>? deleteFolder = null)
    {
        Title = title;
        Rows = new ObservableCollection<NavigatorRowViewModel>(rows);
        CountLabel = countLabel;
        CanAddNote = addNote is not null;
        AddNoteCommand = addNote is null ? null : new RelayCommand(() => addNote(Title));
        ImportPapersCommand = importPapers;
        CanRename = rename is not null;
        _rename = rename;
        CanDeleteFolder = deleteFolder is not null;
        DeleteFolderCommand = deleteFolder is null ? null : new RelayCommand(() => deleteFolder(Title));
        _isExpanded = isExpanded;
    }

    public string Title { get; }

    public ObservableCollection<NavigatorRowViewModel> Rows { get; }

    public string CountLabel { get; }

    /// <summary>Folders of notes take a new note; the papers folder only takes imports.</summary>
    public bool CanAddNote { get; }

    public IRelayCommand? AddNoteCommand { get; }

    /// <summary>The import-paper half of the folder's add menu.</summary>
    public IRelayCommand? ImportPapersCommand { get; }

    // ------------------------------------------------------------ delete folder

    /// <summary>The papers folder and the unfiled one are not folders you made.</summary>
    public bool CanDeleteFolder { get; }

    /// <summary>Sends every note in the folder to the trash; the notes are recoverable.</summary>
    public IRelayCommand? DeleteFolderCommand { get; }

    // ------------------------------------------------------------------ rename

    /// <summary>The papers folder and the unfiled one have no tag to rename.</summary>
    public bool CanRename { get; }

    /// <summary>True while the header is an editable box instead of a label.</summary>
    [ObservableProperty] private bool _isRenaming;

    /// <summary>The name being typed; the folder keeps its own until it is committed.</summary>
    [ObservableProperty] private string _renameName = string.Empty;

    [RelayCommand]
    private void BeginRename()
    {
        if (!CanRename)
            return;

        RenameName = Title;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CancelRename()
    {
        IsRenaming = false;
        RenameName = string.Empty;
    }

    /// <summary>
    /// Hands the typed name to the shell, which renames the tag every note in the
    /// folder carries. A name that was refused leaves the box open to correct.
    /// </summary>
    public void CommitRename()
    {
        if (!IsRenaming || _rename is null)
            return;

        if (_rename(Title, RenameName.Trim()))
            IsRenaming = false;
    }

    /// <summary>An open folder shows folder_open, a closed one shows folder.</summary>
    public string Icon => IsExpanded ? Symbols.FolderOpen : Symbols.Folder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCollapsed), nameof(Icon))]
    private bool _isExpanded;

    public bool IsCollapsed => !IsExpanded;

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}
