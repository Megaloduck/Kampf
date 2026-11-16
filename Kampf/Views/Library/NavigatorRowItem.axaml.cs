using Avalonia.Controls;

namespace Kampf.Views;

/// <summary>
/// One note or paper row in the navigator. It binds to
/// <see cref="ViewModels.NavigatorRowViewModel"/>, supplied by the row template.
/// </summary>
public partial class NavigatorRowItem : UserControl
{
    public NavigatorRowItem() => InitializeComponent();
}
