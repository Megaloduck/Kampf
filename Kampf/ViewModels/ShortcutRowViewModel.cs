using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kampf.Controls;

namespace Kampf.ViewModels;

/// <summary>One rebindable shortcut row in Settings.</summary>
public sealed partial class ShortcutRowViewModel : ObservableObject
{
    private readonly MainViewModel _owner;

    public ShortcutRowViewModel(ShortcutAction action, MainViewModel owner)
    {
        Action = action;
        _owner = owner;
        Title = TitleFor(action);
        Detail = DetailFor(action);
    }

    public ShortcutAction Action { get; }

    public string Title { get; }

    /// <summary>What the shortcut does, so a row is readable without the docs.</summary>
    public string Detail { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptureLabel))]
    private string _gesture = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptureLabel))]
    private bool _isCapturing;

    /// <summary>The key chip: the current gesture, or a prompt while capturing.</summary>
    public string CaptureLabel => IsCapturing ? "Press keys..." : Gesture;

    [RelayCommand]
    private void BeginCapture() => _owner.BeginShortcutCapture(this);

    [RelayCommand]
    private void CancelCapture() => _owner.CancelShortcutCapture();

    public static string TitleFor(ShortcutAction action) => action switch
    {
        ShortcutAction.SaveNote => "Save note",
        ShortcutAction.NewNote => "New note",
        ShortcutAction.ImportPdf => "Import PDF",
        ShortcutAction.FocusSearch => "Search the vault",
        ShortcutAction.ShowGraph => "Open the graph",
        ShortcutAction.ShowEditor => "Open the note editor",
        ShortcutAction.ShowReader => "Open the reader",
        ShortcutAction.ShowChat => "Open Idea Craft",
        ShortcutAction.ShowSettings => "Open Settings",
        ShortcutAction.ToggleTheme => "Toggle theme",
        ShortcutAction.Dismiss => "Dismiss",
        _ => action.ToString(),
    };

    public static string DetailFor(ShortcutAction action) => action switch
    {
        ShortcutAction.SaveNote => "Write the open note to the vault.",
        ShortcutAction.NewNote => "Start a blank note.",
        ShortcutAction.ImportPdf => "Choose PDFs to copy into the vault.",
        ShortcutAction.FocusSearch => "Put the caret in the navigator's search box.",
        ShortcutAction.ShowGraph => "Switch to the graph canvas.",
        ShortcutAction.ShowEditor => "Show the editor for the selected note.",
        ShortcutAction.ShowReader => "Show the selected paper.",
        ShortcutAction.ShowChat => "Ask the vault a question.",
        ShortcutAction.ShowSettings => "Appearance, encryption and shortcuts.",
        ShortcutAction.ToggleTheme => "Switch between light and dark.",
        ShortcutAction.Dismiss => "Close the topmost search, brief or chat answer.",
        _ => string.Empty,
    };
}
