using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Kampf.Services;

namespace Kampf.Views;

/// <summary>
/// One dialog for every irreversible action. Cancel owns the initial focus, so
/// a stray Enter closes nothing; the confirm button is the only way to proceed.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
        : this(new ConfirmRequest("Confirm", string.Empty, "Confirm"))
    {
    }

    public ConfirmDialog(ConfirmRequest request)
    {
        InitializeComponent();

        TitleText.Text = request.Title;
        BodyText.Text = request.Body;
        BodyText.IsVisible = request.Body.Length > 0;

        var details = request.Details is { Count: > 0 }
            ? string.Join(Environment.NewLine, request.Details)
            : string.Empty;

        DetailText.Text = details;
        DetailPanel.IsVisible = details.Length > 0;

        ConfirmButton.Content = request.ConfirmLabel;
        ConfirmButton.Classes.Add(request.IsDanger ? "danger" : "primary");

        Opened += (_, _) => CancelButton.Focus();
        KeyDown += OnDialogKeyDown;
    }

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        Close(false);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => Close(true);
}
