using Avalonia.Controls;

namespace Kampf.Views;

/// <summary>Trash bin workspace: deletion is permanent, so it is always empty.</summary>
public partial class TrashView : UserControl
{
    public TrashView()
    {
        InitializeComponent();
    }
}
