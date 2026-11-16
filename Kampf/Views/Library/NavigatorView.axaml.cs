using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Kampf.ViewModels;

namespace Kampf.Views;

/// <summary>
/// The navigator: search, folders and the note rows inside them. It owns the
/// search box and the new-folder box, so the shell only asks it for focus
/// instead of reaching into it.
/// </summary>
public partial class NavigatorView : UserControl
{
    public NavigatorView() => InitializeComponent();

    /// <summary>Puts the caret in the search box, query selected, ready to replace.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not MainViewModel viewModel)
            return;

        if (viewModel.SearchCommand.CanExecute(null))
            viewModel.SearchCommand.Execute(null);

        e.Handled = true;
    }

    // ------------------------------------------------------------ new folder

    /// <summary>The folder box is ready to type in as soon as the flyout is up.</summary>
    private void OnNewFolderFlyoutOpened(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Visual content)
            content.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.Focus();
    }

    private void OnNewFolderKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        CreateFolder();
        e.Handled = true;
    }

    private void OnCreateFolderClick(object? sender, RoutedEventArgs e) => CreateFolder();

    /// <summary>
    /// Creates the folder and closes the flyout; a name that was refused (empty,
    /// or already a folder) leaves the box open with the reason in the status bar.
    /// </summary>
    private void CreateFolder()
    {
        if (DataContext is not MainViewModel viewModel || viewModel.NewFolderName.Trim().Length == 0)
            return;

        if (viewModel.AddFolderCommand.CanExecute(null))
            viewModel.AddFolderCommand.Execute(null);

        if (viewModel.NewFolderName.Length == 0)
            AddFolderButton.Flyout?.Hide();
    }
}
