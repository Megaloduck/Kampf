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
            _ = OpenVaultAsync(window.SetViewModel, window.ShowFailure);
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            var view = new MainView();
            singleViewPlatform.MainView = view;
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
            var viewModel = new MainViewModel(vault);
            apply(viewModel);
            await viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            fail(ex);
        }
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
