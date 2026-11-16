using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Kampf.ViewModels;

/// <summary>
/// Gates the app behind the vault passphrase. The view model never touches the
/// database itself: it hands the passphrase to <see cref="Unlock"/> and reports
/// whether that succeeded.
/// </summary>
public partial class UnlockViewModel : ViewModelBase
{
    private readonly Func<string, Task> _unlock;

    public UnlockViewModel(string vaultPath, Func<string, Task> unlock)
    {
        VaultPath = vaultPath;
        _unlock = unlock;
    }

    public string VaultPath { get; }

    [ObservableProperty] private string _passphrase = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _error = string.Empty;

    [ObservableProperty] private bool _isUnlocking;

    public bool HasError => Error.Length > 0;

    [RelayCommand]
    private async Task UnlockAsync()
    {
        if (IsUnlocking)
            return;

        if (Passphrase.Length == 0)
        {
            Error = "Enter the vault passphrase.";
            return;
        }

        IsUnlocking = true;
        Error = string.Empty;

        try
        {
            await _unlock(Passphrase);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Passphrase = string.Empty;
        }
        finally
        {
            IsUnlocking = false;
        }
    }
}
