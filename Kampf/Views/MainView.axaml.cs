using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Kampf.Controls;
using Kampf.ViewModels;

namespace Kampf.Views;

public partial class MainView : UserControl
{
    private MainViewModel? _bound;

    public MainView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;

        // Undo and redo live in the header, so they follow the editor's history.
        NoteEditor.HistoryChanged += (_, _) => UpdateHistoryButtons();
        UpdateHistoryButtons();

        // PDFs can be dropped anywhere on the workspace.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_bound is not null)
            _bound.PropertyChanged -= OnViewModelPropertyChanged;

        _bound = DataContext as MainViewModel;

        if (_bound is not null)
            _bound.PropertyChanged += OnViewModelPropertyChanged;

        UpdateHistoryButtons();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A newly loaded note replaces the editor's undo history.
        if (e.PropertyName is nameof(MainViewModel.HasActiveNote) or nameof(MainViewModel.EditorBody))
            UpdateHistoryButtons();
    }

    // ------------------------------------------------------------ window chrome

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e) =>
        WindowControls.BeginDrag(TopLevel.GetTopLevel(this), e);

    private void OnHeaderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (WindowControls.IsDragSurface(e.Source))
            WindowControls.ToggleMaximize(TopLevel.GetTopLevel(this) as Window);
    }

    // --------------------------------------------------------- panel layout

    private static readonly GridLength PanelSplitterWidth = new(6);
    private GridLength _navigatorWidth = new(268);
    private GridLength _reviewWidth = new(330);

    // Column definitions get no generated fields, so address them by index:
    // 0 rail, 1 navigator, 2 splitter, 3 workspace, 4 splitter, 5 review queue.
    private ColumnDefinition NavigatorColumn => RootLayout.ColumnDefinitions[1];
    private ColumnDefinition NavigatorSplitterColumn => RootLayout.ColumnDefinitions[2];
    private ColumnDefinition ReviewSplitterColumn => RootLayout.ColumnDefinitions[4];
    private ColumnDefinition ReviewColumn => RootLayout.ColumnDefinitions[5];

    private void OnToggleNavigatorClick(object? sender, RoutedEventArgs e)
    {
        var hidden = NavigatorColumn.Width.Value <= 0;

        if (hidden)
        {
            NavigatorColumn.Width = _navigatorWidth;
            NavigatorSplitterColumn.Width = PanelSplitterWidth;
            NavigatorPanel.IsVisible = true;
        }
        else
        {
            _navigatorWidth = new GridLength(Math.Max(NavigatorColumn.ActualWidth, 200));
            NavigatorPanel.IsVisible = false;
            NavigatorColumn.Width = new GridLength(0);
            NavigatorSplitterColumn.Width = new GridLength(0);
        }

        UpdatePanelButtons();
    }

    private void OnToggleReviewClick(object? sender, RoutedEventArgs e)
    {
        var hidden = ReviewColumn.Width.Value <= 0;

        if (hidden)
        {
            ReviewColumn.Width = _reviewWidth;
            ReviewSplitterColumn.Width = PanelSplitterWidth;
            ReviewPanel.IsVisible = true;
        }
        else
        {
            _reviewWidth = new GridLength(Math.Max(ReviewColumn.ActualWidth, 240));
            ReviewPanel.IsVisible = false;
            ReviewColumn.Width = new GridLength(0);
            ReviewSplitterColumn.Width = new GridLength(0);
        }

        UpdatePanelButtons();
    }

    private void UpdatePanelButtons()
    {
        var navigatorHidden = NavigatorColumn.Width.Value <= 0;
        NavigatorToggleIcon.Text = navigatorHidden ? Symbols.LeftPanelOpen : Symbols.LeftPanelClose;
        ToolTip.SetTip(NavigatorToggleButton, navigatorHidden ? "Show the navigator" : "Hide the navigator");

        var reviewHidden = ReviewColumn.Width.Value <= 0;
        ReviewToggleIcon.Text = reviewHidden ? Symbols.RightPanelOpen : Symbols.RightPanelClose;
        ToolTip.SetTip(ReviewToggleButton, reviewHidden ? "Show the review queue" : "Hide the review queue");
    }

    // ------------------------------------------------------------ undo / redo

    private void OnUndoClick(object? sender, RoutedEventArgs e) => NoteEditor.Undo();

    private void OnRedoClick(object? sender, RoutedEventArgs e) => NoteEditor.Redo();

    private void UpdateHistoryButtons()
    {
        // The buttons live in the window header, so they only mean something
        // while a note is loaded into the editor.
        var hasNote = _bound?.HasActiveNote == true;
        UndoButton.IsEnabled = hasNote && NoteEditor.CanUndo;
        RedoButton.IsEnabled = hasNote && NoteEditor.CanRedo;
    }

    // ---------------------------------------------------- drag and drop import

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DroppedPdfs(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        var paths = DroppedPdfs(e);
        if (_bound is null)
            return;

        if (paths.Count == 0)
        {
            _bound.StatusMessage = "Only PDF files can be imported. Drop a .pdf onto the window.";
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        _ = _bound.ImportFilesAsync(paths);
    }

    private static List<string> DroppedPdfs(DragEventArgs e)
    {
        var items = e.Data.GetFiles();
        if (items is null)
            return new List<string>();

        return items
            .Select(item => item.TryGetLocalPath())
            .Where(path => path is not null && path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            .Select(path => path!)
            .ToList();
    }

    // --------------------------------------------------------------- sidebar

    /// <summary>Puts the caret in the search box, query selected, ready to replace.</summary>
    public void FocusSearch()
    {
        // A collapsed navigator would take the focus with it.
        if (NavigatorColumn.Width.Value <= 0)
            OnToggleNavigatorClick(null, new RoutedEventArgs());

        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _bound is null)
            return;

        if (_bound.SearchCommand.CanExecute(null))
            _bound.SearchCommand.Execute(null);

        e.Handled = true;
    }
}
