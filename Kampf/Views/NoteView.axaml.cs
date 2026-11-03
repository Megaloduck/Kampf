using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Kampf.Controls;
using Kampf.ViewModels;

namespace Kampf.Views;

/// <summary>The note workspace: title, tags and one body pane shared by editor and preview.</summary>
public partial class NoteView : UserControl
{
    private MainViewModel? _bound;

    public NoteView()
    {
        InitializeComponent();

        // The markdown preview resolves its brushes when it is built.
        ActualThemeVariantChanged += (_, _) => RebuildPreview();

        BodyEditor.TextChanged += (_, _) => RaiseHistoryChanged();
        DataContextChanged += OnDataContextChanged;

        UpdateBodyPane();
    }

    /// <summary>Raised whenever the editor's undo history changes.</summary>
    public event EventHandler? HistoryChanged;

    public bool CanUndo => BodyEditor.CanUndo;

    public bool CanRedo => BodyEditor.CanRedo;

    public void Undo() => BodyEditor.Undo();

    public void Redo() => BodyEditor.Redo();

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_bound is not null)
        {
            _bound.PropertyChanged -= OnViewModelPropertyChanged;
            _bound.EditorReloaded -= OnEditorReloaded;
        }

        _bound = DataContext as MainViewModel;

        if (_bound is not null)
        {
            _bound.PropertyChanged += OnViewModelPropertyChanged;
            _bound.EditorReloaded += OnEditorReloaded;
        }

        RebuildPreview();
        RaiseHistoryChanged();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.EditorBody):
                RebuildPreview();
                break;
            case nameof(MainViewModel.HasActiveNote):
                RebuildPreview();
                RaiseHistoryChanged();
                break;
        }
    }

    /// <summary>A freshly loaded note starts a new history, so undo can't cross notes.</summary>
    private void OnEditorReloaded(object? sender, EventArgs e)
    {
        TextBoxHistory.Clear(BodyEditor);
        RaiseHistoryChanged();
    }

    private void RebuildPreview()
    {
        if (_bound is null)
        {
            PreviewHost.Content = null;
            return;
        }

        var viewModel = _bound;
        PreviewHost.Content = MarkdownPreview.Build(
            viewModel.EditorBody,
            target => _ = viewModel.OpenWikiLinkAsync(target));
    }

    private void OnPreviewToggleChanged(object? sender, RoutedEventArgs e) => UpdateBodyPane();

    /// <summary>The body is one pane: the toggle swaps the editor for the preview.</summary>
    private void UpdateBodyPane()
    {
        var showPreview = PreviewToggle.IsChecked == true;

        BodyEditor.IsVisible = !showPreview;
        PreviewPane.IsVisible = showPreview;
    }

    private void RaiseHistoryChanged() => HistoryChanged?.Invoke(this, EventArgs.Empty);
}
