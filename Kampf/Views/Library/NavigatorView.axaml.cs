using Avalonia.Controls;
using Avalonia.Input;
using Kampf.ViewModels;

namespace Kampf.Views;

/// <summary>
/// The navigator: search, tag folders and the note rows inside them. It owns the
/// search box, so the shell only asks it for focus instead of reaching into it.
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
}
