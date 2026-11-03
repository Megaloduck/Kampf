using Avalonia.Controls;
using Avalonia.Input;
using Kampf.ViewModels;

namespace Kampf.Views;

/// <summary>Idea-Craft: the question box and the cited transcript.</summary>
public partial class ChatView : UserControl
{
    public ChatView()
    {
        InitializeComponent();
    }

    private void OnQuestionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not MainViewModel viewModel)
            return;

        if (viewModel.AskIdeaCraftCommand.CanExecute(null))
            viewModel.AskIdeaCraftCommand.Execute(null);

        e.Handled = true;
    }
}
