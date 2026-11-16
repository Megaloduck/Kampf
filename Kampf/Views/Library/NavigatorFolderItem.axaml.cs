using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Kampf.ViewModels;

namespace Kampf.Views;

/// <summary>
/// One folder header in the navigator. It binds to
/// <see cref="ViewModels.NavigatorFolderViewModel"/>, which the folder template
/// supplies, and puts the caret in the name box when the folder is renamed.
/// </summary>
public partial class NavigatorFolderItem : UserControl
{
    private NavigatorFolderViewModel? _folder;

    public NavigatorFolderItem()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindFolder();
        BindFolder();
    }

    private void BindFolder()
    {
        if (_folder is not null)
            _folder.PropertyChanged -= OnFolderPropertyChanged;

        _folder = DataContext as NavigatorFolderViewModel;

        if (_folder is not null)
            _folder.PropertyChanged += OnFolderPropertyChanged;
    }

    private void OnFolderPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NavigatorFolderViewModel.IsRenaming) || _folder?.IsRenaming != true)
            return;

        // The box is only in the tree once it is visible, so the focus waits a tick.
        Dispatcher.UIThread.Post(
            () =>
            {
                RenameBox.Focus();
                RenameBox.SelectAll();
            },
            DispatcherPriority.Input);
    }

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not NavigatorFolderViewModel folder)
            return;

        if (e.Key == Key.Enter)
        {
            folder.CommitRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            // Cancel before the box loses focus, so the lost-focus commit stays out
            // of the way.
            folder.CancelRenameCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Clicking away keeps what was typed, like Enter does.</summary>
    private void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_folder?.IsRenaming == true)
            _folder.CommitRename();
    }
}
