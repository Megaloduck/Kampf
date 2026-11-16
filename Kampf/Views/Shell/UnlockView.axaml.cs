using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Kampf.ViewModels;

namespace Kampf.Views;

public partial class UnlockView : UserControl
{
    public UnlockView()
    {
        InitializeComponent();
    }

    // The window runs without system chrome, so its title-bar area is this
    // surface: pressing it moves the window, double-clicking maximizes.
    private void OnSurfacePointerPressed(object? sender, PointerPressedEventArgs e) =>
        WindowControls.BeginDrag(TopLevel.GetTopLevel(this), e);

    private void OnSurfaceDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (WindowControls.IsDragSurface(e.Source))
            WindowControls.ToggleMaximize(TopLevel.GetTopLevel(this) as Window);
    }

    private void OnPassphraseKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        if (DataContext is UnlockViewModel viewModel && viewModel.UnlockCommand.CanExecute(null))
            viewModel.UnlockCommand.Execute(null);
    }

    /// <summary>Puts the caret in the passphrase box once the overlay is on screen.</summary>
    public void FocusPassphrase()
    {
        Dispatcher.UIThread.Post(() => PassphraseBox.Focus(), DispatcherPriority.Input);
    }
}
