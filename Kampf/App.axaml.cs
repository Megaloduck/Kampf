using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using Kampf.Infrastructure;
using Kampf.ViewModels;
using Kampf.Views;

namespace Kampf;

public partial class App : Avalonia.Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit.
            DisableAvaloniaDataAnnotationValidation();
            var window = new MainWindow();
            desktop.MainWindow = window;

            // An encrypted vault is gated before the shell is built, so no view
            // model exists (and no query runs) until the passphrase is accepted.
            if (Vault.IsEncryptedVault())
            {
                window.ShowLockScreen(Vault.DefaultRootPath, async passphrase =>
                {
                    var vault = await Vault.OpenAsync(passphrase: passphrase);
                    await AttachAsync(vault, window.SetViewModel);
                    window.HideLockScreen();
                });
            }
            else
            {
                _ = OpenVaultAsync(window.SetViewModel, window.ShowFailure);
            }
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            var view = new MainView();
            singleViewPlatform.MainView = view;

            if (Vault.IsEncryptedVault())
            {
                view.Content = new TextBlock
                {
                    Text = "This vault is encrypted. Unlocking it is only available in the desktop app for now.",
                    Margin = new Thickness(24),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                };
                base.OnFrameworkInitializationCompleted();
                return;
            }

            _ = OpenVaultAsync(
                viewModel => view.DataContext = viewModel,
                ex => view.Content = new TextBlock
                {
                    Text = "Kampf could not open its vault: " + ex.Message,
                    Margin = new Thickness(24),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                });
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task OpenVaultAsync(Action<MainViewModel> apply, Action<Exception> fail)
    {
        try
        {
            var vault = await Vault.OpenAsync();
            await AttachAsync(vault, apply);
        }
        catch (Exception ex)
        {
            fail(ex);
        }
    }

    private static async Task AttachAsync(Vault vault, Action<MainViewModel> apply)
    {
        var viewModel = new MainViewModel(vault);
        apply(viewModel);
        await viewModel.InitializeAsync();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
