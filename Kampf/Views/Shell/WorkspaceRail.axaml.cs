using Avalonia.Controls;

namespace Kampf.Views;

/// <summary>
/// The workspace rail. It holds no state of its own: every entry is a command on
/// <see cref="ViewModels.MainViewModel"/>, reached through the inherited data context.
/// </summary>
public partial class WorkspaceRail : UserControl
{
    public WorkspaceRail() => InitializeComponent();
}
