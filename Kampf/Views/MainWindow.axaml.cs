using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Kampf.ViewModels;

namespace Kampf.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    public void SetViewModel(MainViewModel viewModel)
    {
        viewModel.PickPdfFiles = PickPdfFilesAsync;
        RootView.DataContext = viewModel;
    }

    /// <summary>
    /// Replaces the shell with the unlock overlay. <paramref name="unlock"/> is
    /// given the entered passphrase and is expected to either attach the shell
    /// or throw, in which case the message is shown in the overlay.
    /// </summary>
    public void ShowLockScreen(string vaultPath, Func<string, Task> unlock)
    {
        RootView.IsVisible = false;
        LockView.DataContext = new UnlockViewModel(vaultPath, unlock);
        LockView.IsVisible = true;
        LockView.FocusPassphrase();
    }

    public void HideLockScreen()
    {
        LockView.IsVisible = false;
        LockView.DataContext = null;
        RootView.IsVisible = true;
    }

    public void ShowFailure(Exception exception)
    {
        RootView.Content = new Border
        {
            Padding = new Thickness(40),
            Background = new SolidColorBrush(Color.Parse("#0B0D13")),
            Child = new StackPanel
            {
                Spacing = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Kampf could not open its vault",
                        FontSize = 20,
                        FontWeight = FontWeight.SemiBold,
                    },
                    new TextBlock
                    {
                        Text = exception.Message,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.Parse("#8C94AC")),
                    },
                    new TextBlock
                    {
                        Text = "Check that the vault folder is writable and restart the app.",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.Parse("#5A6178")),
                    },
                },
            },
        };
    }

    private async Task<IReadOnlyList<string>> PickPdfFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import PDFs into the vault",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("PDF documents")
                {
                    Patterns = new[] { "*.pdf" },
                    MimeTypes = new[] { "application/pdf" },
                },
            },
        });

        return files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => path!)
            .ToList();
    }
}
