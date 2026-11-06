using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace Kampf.Views;

/// <summary>
/// The window's own minimize, maximize/restore and close buttons. They sit in
/// the top-right corner of the shell's header (and on the lock screen) because
/// the window is drawn without system chrome; on platforms that keep their
/// native title bar the control hides itself.
/// </summary>
public partial class WindowControls : UserControl
{
    private Window? _window;

    public WindowControls()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _window = TopLevel.GetTopLevel(this) as Window;

        // Any other window still has the platform's caption buttons; drawing a
        // second set inside the header would be two controls for one action.
        if (_window is not { ExtendClientAreaChromeHints: ExtendClientAreaChromeHints.NoChrome })
            return;

        IsVisible = true;
        _window.PropertyChanged += OnWindowPropertyChanged;
        UpdateMaximizeGlyph();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_window is not null)
            _window.PropertyChanged -= OnWindowPropertyChanged;

        _window = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
            UpdateMaximizeGlyph();
    }

    private void UpdateMaximizeGlyph()
    {
        var maximized = _window?.WindowState == WindowState.Maximized;

        MaximizeIcon.IsVisible = !maximized;
        RestoreIcon.IsVisible = maximized;
        ToolTip.SetTip(MaximizeButton, maximized ? "Restore" : "Maximize");
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        if (_window is { } window)
            window.WindowState = WindowState.Minimized;
    }

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) => ToggleMaximize(_window);

    private void OnCloseClick(object? sender, RoutedEventArgs e) => _window?.Close();

    // ------------------------------------------------------- shared chrome

    /// <summary>
    /// True when a press landed on empty chrome rather than on one of the
    /// controls living in it: a button in the header must click, not move the
    /// window.
    /// </summary>
    public static bool IsDragSurface(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Button or TextBox or ScrollBar)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Moves the window from a press on its own title-bar area. The OS runs the
    /// drag, so snapping and multi-monitor behaviour stay native.
    /// </summary>
    public static void BeginDrag(TopLevel? topLevel, PointerPressedEventArgs e)
    {
        // A double click is the maximize gesture, handled on DoubleTapped.
        if (e.ClickCount > 1 || !IsDragSurface(e.Source))
            return;

        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
            return;

        if (topLevel is Window { ExtendClientAreaChromeHints: ExtendClientAreaChromeHints.NoChrome } window)
            window.BeginMoveDrag(e);
    }

    /// <summary>Maximizes a chrome-less window, or restores it when maximized.</summary>
    public static void ToggleMaximize(Window? window)
    {
        if (window is not { CanResize: true } target)
            return;

        target.WindowState = target.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }
}
