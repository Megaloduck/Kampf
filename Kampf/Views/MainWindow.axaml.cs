using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Kampf.Controls;
using Kampf.ViewModels;

namespace Kampf.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        // The shell draws its own caption buttons in the header's top-right
        // corner, so on Windows the window runs without system chrome and its
        // content reaches the frame. macOS and Linux keep their native title
        // bar: traffic lights and window-manager decorations are the platform's
        // convention, and the header control hides itself there.
        if (OperatingSystem.IsWindows())
        {
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
        }

        // Shortcuts are handled on the way down, before a focused text box can
        // swallow them: Ctrl+S must save the note, not reach the editor as text.
        AddHandler(KeyDownEvent, OnShortcutKeyDown, RoutingStrategies.Tunnel);

        // The reader's place in the open paper and the shape of the window are
        // saved on the way out, so the next launch resumes both.
        Closing += (_, _) =>
        {
            _ = _viewModel?.SaveReadingStateAsync();
            SaveWindowSettings();
        };
    }

    public void SetViewModel(MainViewModel viewModel)
    {
        viewModel.PickPdfFiles = PickPdfFilesAsync;
        viewModel.PickPublishFolder = PickPublishFolderAsync;
        _viewModel = viewModel;
        RootView.DataContext = viewModel;
        ApplyWindowSettings(viewModel.VaultPath);
    }

    // ------------------------------------------------------------- shortcuts

    private void OnShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        // The unlock overlay owns the keyboard until the vault is open.
        if (_viewModel is null || LockView.IsVisible)
            return;

        // A pending rebind in Settings owns the keyboard until it has a chord.
        if (_viewModel.CapturingShortcut is not null)
        {
            e.Handled = _viewModel.CaptureShortcutKey(e.Key, e.KeyModifiers);
            return;
        }

        var typing = FocusManager?.GetFocusedElement() is TextBox;
        var action = _viewModel.Keymap.Resolve(e.Key, e.KeyModifiers, typing);

        if (action == ShortcutAction.None)
            return;

        e.Handled = Run(action);
    }

    /// <summary>
    /// Runs one shortcut and reports whether it did anything: an unconsumed key
    /// must still reach the view, so Escape in an empty state stays inert.
    /// </summary>
    private bool Run(ShortcutAction action)
    {
        if (_viewModel is not { } viewModel)
            return false;

        switch (action)
        {
            case ShortcutAction.SaveNote:
                viewModel.SaveNoteCommand.Execute(null);
                return true;
            case ShortcutAction.NewNote:
                viewModel.NewNoteCommand.Execute(null);
                return true;
            case ShortcutAction.ImportPdf:
                viewModel.ImportPdfCommand.Execute(null);
                return true;
            case ShortcutAction.FocusSearch:
                RootView.FocusSearch();
                return true;
            case ShortcutAction.ShowGraph:
                viewModel.ShowGraphCommand.Execute(null);
                return true;
            case ShortcutAction.ShowEditor:
                viewModel.ShowEditorCommand.Execute(null);
                return true;
            case ShortcutAction.ShowReader:
                viewModel.ShowReaderCommand.Execute(null);
                return true;
            case ShortcutAction.ShowChat:
                viewModel.ShowChatCommand.Execute(null);
                return true;
            case ShortcutAction.ShowSettings:
                viewModel.ShowSettingsCommand.Execute(null);
                return true;
            case ShortcutAction.ToggleTheme:
                viewModel.ToggleThemeCommand.Execute(null);
                return true;
            case ShortcutAction.Dismiss:
                return Dismiss();
            default:
                return false;
        }
    }

    /// <summary>Escape backs out of the most recent overlay, one layer at a time.</summary>
    private bool Dismiss()
    {
        if (_viewModel is not { } viewModel)
            return false;

        if (viewModel.IsChatting)
        {
            viewModel.CancelChatCommand.Execute(null);
            return true;
        }

        if (viewModel.IsSettingsView)
        {
            viewModel.CloseSettingsCommand.Execute(null);
            return true;
        }

        if (viewModel.ReaderBrief.Length > 0)
        {
            viewModel.DismissBriefCommand.Execute(null);
            return true;
        }

        if (viewModel.HasSearchResults)
        {
            viewModel.ClearSearchCommand.Execute(null);
            return true;
        }

        return false;
    }

    // -------------------------------------------------------- window geometry

    private void ApplyWindowSettings(string vaultPath)
    {
        var settings = AppSettings.Load(vaultPath);

        if (settings.Width >= MinWidth)
            Width = settings.Width;
        if (settings.Height >= MinHeight)
            Height = settings.Height;

        // A saved position only applies while it is still on a real screen; the
        // monitor that held it may be gone.
        if (settings is { X: { } x, Y: { } y } && Screens.All.Any(screen => screen.Bounds.Contains(new PixelPoint(x, y))))
            Position = new PixelPoint(x, y);

        if (settings.Maximized)
            WindowState = WindowState.Maximized;
    }

    private void SaveWindowSettings()
    {
        if (_viewModel is null)
            return;

        try
        {
            var saved = AppSettings.Load(_viewModel.VaultPath);
            var normal = WindowState == WindowState.Normal;

            // Maximised and minimised states report the screen's bounds, not the
            // size to restore to, so the previous size is kept for those.
            (saved with
            {
                Maximized = WindowState == WindowState.Maximized,
                Width = normal ? Width : saved.Width,
                Height = normal ? Height : saved.Height,
                X = normal ? Position.X : saved.X,
                Y = normal ? Position.Y : saved.Y,
            }).Save(_viewModel.VaultPath);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // Remembering the window is a convenience, never a reason to complain.
        }
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

    /// <summary>Folder for the static publish output.</summary>
    private async Task<string?> PickPublishFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Publish a static copy of the vault",
            AllowMultiple = false,
        });

        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
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
