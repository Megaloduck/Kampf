using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Kampf.ViewModels;

namespace Kampf.Views;

/// <summary>The paper reader: metadata, extracted pages and their anchored notes.</summary>
public partial class ReaderView : UserControl
{
    public ReaderView()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnCapturePageClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null || sender is not Button button || button.DataContext is not PageViewModel page)
            return;

        var card = button.FindAncestorOfType<Border>();
        var textBlock = card?.GetVisualDescendants().OfType<SelectableTextBlock>().FirstOrDefault();

        // Pressing the button moves focus off the page, and SelectableTextBlock
        // clears its selection on focus loss. Fall back to the snapshot taken
        // when the pointer was released.
        var live = ReadSelection(textBlock);
        var text = string.IsNullOrWhiteSpace(live) ? page.SelectedText : live;

        textBlock?.ClearSelection();
        page.SelectedText = string.Empty;

        _ = viewModel.CreateNoteFromHighlightAsync(page.Number, text);
    }

    /// <summary>Remembers the selection while the page still owns focus.</summary>
    private static void OnPageTextPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is SelectableTextBlock block && block.DataContext is PageViewModel page)
            page.SelectedText = ReadSelection(block);
    }

    private static string ReadSelection(SelectableTextBlock? textBlock)
    {
        if (textBlock?.Text is null)
            return string.Empty;

        var start = Math.Min(textBlock.SelectionStart, textBlock.SelectionEnd);
        var end = Math.Max(textBlock.SelectionStart, textBlock.SelectionEnd);

        if (end <= start || end > textBlock.Text.Length)
            return string.Empty;

        return textBlock.Text.Substring(start, end - start).Trim();
    }
}
