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
