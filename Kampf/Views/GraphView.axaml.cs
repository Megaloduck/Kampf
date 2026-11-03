using System;
using Avalonia.Controls;
using Kampf.Controls;
using Kampf.ViewModels;

namespace Kampf.Views;

/// <summary>The graph workspace: filters, canvas and the vault path.</summary>
public partial class GraphView : UserControl
{
    public GraphView()
    {
        InitializeComponent();

        GraphCanvas.NodeSelected += OnNodeSelected;
        GraphCanvas.NodeActivated += OnNodeActivated;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnNodeSelected(object? sender, GraphNodeEventArgs e) => ViewModel?.HighlightNode(e.Node);

    private void OnNodeActivated(object? sender, GraphNodeEventArgs e) => ViewModel?.OpenNode(e.Node);
}
