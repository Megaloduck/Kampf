using System;
using System.Linq;
using Avalonia;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kampf.Controls;
using Kampf.ViewModels;

namespace Kampf.Views;

/// <summary>
/// The paper reader: metadata, rendered pages with their captured highlights,
/// and the notes anchored to them.
/// </summary>
public partial class ReaderView : UserControl
{
    private MainViewModel? _viewModel;

    public ReaderView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.PageJumpRequested -= OnPageJumpRequested;

        _viewModel = ViewModel;

        if (_viewModel is not null)
            _viewModel.PageJumpRequested += OnPageJumpRequested;
    }

    /// <summary>
    /// A citation or search hit names a page: scroll the reader to it once the
    /// newly loaded pages have been laid out. The cited passage itself is
    /// marked on the page by the view model.
    /// </summary>
    private async void OnPageJumpRequested(object? sender, PageJump jump)
    {
        await Task.Yield();
        Dispatcher.UIThread.Post(() => ScrollToPage(jump), DispatcherPriority.Background);
    }

    private void ScrollToPage(PageJump jump)
    {
        var card = PageCard(jump.Page);
        if (card is null)
            return;

        var top = card.TranslatePoint(new Point(0, 0), PagesList)?.Y ?? 0;
        var height = card.Bounds.Height;

        if (height <= 0)
        {
            // Not laid out yet (still a placeholder for an unrendered page).
            card.BringIntoView();
            return;
        }

        var target = top + height * Math.Clamp(jump.Offset, 0, 1);
        PagesScroller.Offset = new Vector(PagesScroller.Offset.X, Math.Max(0, target));
    }

    private Border? PageCard(int page) =>
        PagesList
            .GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(border => border.DataContext is PageViewModel candidate && candidate.Number == page);

    /// <summary>Pages are sized from the reader's width, so they follow the window.</summary>
    private void OnPagesSizeChanged(object? sender, SizeChangedEventArgs e) =>
        ViewModel?.SetReaderViewport(
            PagesScroller.Viewport.Width > 0 ? PagesScroller.Viewport.Width : e.NewSize.Width - 12);

    /// <summary>
    /// Tracks the page at the top of the viewport, so leaving the paper resumes
    /// there instead of at page one.
    /// </summary>
    private void OnPagesScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
            return;

        var offsetY = PagesScroller.Offset.Y;
        PageViewModel? top = null;
        var cardTop = 0.0;
        var cardHeight = 0.0;

        foreach (var card in PagesList.GetVisualDescendants().OfType<Border>())
        {
            if (card.DataContext is not PageViewModel page || card.Bounds.Height <= 0)
                continue;

            var topY = card.TranslatePoint(new Point(0, 0), PagesList)?.Y;
            if (topY is null || topY > offsetY + 4)
                continue;

            top = page;
            cardTop = topY.Value;
            cardHeight = card.Bounds.Height;
        }

        if (top is null)
            return;

        viewModel.UpdateReadingPosition(
            top.Number,
            cardHeight > 0 ? (offsetY - cardTop) / cardHeight : 0);
    }

    /// <summary>Enter steps to the next hit; Escape clears the search.</summary>
    private void OnPageSearchKeyDown(object? sender, KeyEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
            return;

        if (e.Key == Key.Enter)
        {
            viewModel.NextReaderMatchCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            viewModel.ReaderSearchQuery = string.Empty;
            e.Handled = true;
        }
    }

    /// <summary>Clicking the percentage returns to 100%.</summary>
    private void OnZoomLabelPressed(object? sender, PointerPressedEventArgs e) =>
        ViewModel?.ResetZoomCommand.Execute(null);

    /// <summary>Remembers the selection the page control just finished.</summary>
    private static void OnPageSelectionChanged(object? sender, EventArgs e)
    {
        if (sender is PdfPageView view && view.DataContext is PageViewModel page)
            page.SelectedText = view.SelectedText;
    }

    /// <summary>Double-clicking a captured passage opens the note it backs.</summary>
    private void OnPageNoteRequested(object? sender, string noteId) =>
        ViewModel?.OpenAnchoredNote(noteId);

    private void OnCapturePageClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null || sender is not Button button || button.DataContext is not PageViewModel page)
            return;

        var card = button.FindAncestorOfType<Border>();
        var text = string.Empty;
        var start = -1;
        var length = 0;

        // The rendered page knows exactly which characters were dragged over.
        var pageView = card?.GetVisualDescendants().OfType<PdfPageView>().FirstOrDefault();
        if (pageView is not null && !string.IsNullOrWhiteSpace(pageView.SelectedText))
        {
            (start, length) = pageView.SelectionRange;
            text = pageView.SelectedText;
            pageView.ClearSelection();
        }
        else
        {
            // Text mode: the selection lives in the text box. Pressing the
            // button moves focus off it, and SelectableTextBlock clears its
            // selection on focus loss, so fall back to the snapshot taken when
            // the pointer was released.
            var textBlock = card?.GetVisualDescendants().OfType<SelectableTextBlock>().FirstOrDefault();
            var live = ReadSelection(textBlock);
            text = string.IsNullOrWhiteSpace(live) ? page.SelectedText : live;
            textBlock?.ClearSelection();
        }

        page.SelectedText = string.Empty;

        _ = viewModel.CreateNoteFromHighlightAsync(page.Number, text, start, length);
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
