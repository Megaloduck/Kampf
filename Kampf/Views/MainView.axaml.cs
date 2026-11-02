using System;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Kampf.Controls;
using Kampf.ViewModels;

namespace Kampf.Views;

public partial class MainView : UserControl
{
    private MainViewModel? _bound;

    public MainView()
    {
        InitializeComponent();

        GraphView.NodeSelected += OnGraphNodeSelected;
        GraphView.NodeActivated += OnGraphNodeActivated;
        DataContextChanged += OnDataContextChanged;

        // The markdown preview resolves its brushes when it is built.
        ActualThemeVariantChanged += (_, _) => RebuildPreview();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_bound is not null)
            _bound.PropertyChanged -= OnViewModelPropertyChanged;

        _bound = DataContext as MainViewModel;

        if (_bound is not null)
            _bound.PropertyChanged += OnViewModelPropertyChanged;

        RebuildPreview();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.EditorBody) or nameof(MainViewModel.HasActiveNote))
            RebuildPreview();
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

    private void OnGraphNodeSelected(object? sender, GraphNodeEventArgs e) => _bound?.HighlightNode(e.Node);

    private void OnGraphNodeActivated(object? sender, GraphNodeEventArgs e) => _bound?.OpenNode(e.Node);

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _bound is null)
            return;

        if (_bound.SearchCommand.CanExecute(null))
            _bound.SearchCommand.Execute(null);

        e.Handled = true;
    }

    private void OnCapturePageClick(object? sender, RoutedEventArgs e)
    {
        if (_bound is null || sender is not Button button || button.DataContext is not PageViewModel page)
            return;

        var card = button.FindAncestorOfType<Border>();
        var textBlock = card?.GetVisualDescendants().OfType<SelectableTextBlock>().FirstOrDefault();

        _ = _bound.CreateNoteFromHighlightAsync(page.Number, ExtractSelection(textBlock));
    }

    private static string ExtractSelection(SelectableTextBlock? textBlock)
    {
        if (textBlock?.Text is null)
            return string.Empty;

        var start = Math.Min(textBlock.SelectionStart, textBlock.SelectionEnd);
        var end = Math.Max(textBlock.SelectionStart, textBlock.SelectionEnd);

        if (end <= start || end > textBlock.Text.Length)
            return string.Empty;

        var text = textBlock.Text.Substring(start, end - start).Trim();
        textBlock.SelectionStart = 0;
        textBlock.SelectionEnd = 0;
        return text;
    }
}
