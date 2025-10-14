using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Diagnostics.ViewModels;
using Avalonia.Headless.XUnit;
using Xunit;

namespace DiagnosticsTools.Tests;

public class TreePageViewModelTests
{
    [AvaloniaFact]
    public void TreeFilter_ShowsChildren_WhenParentMatches()
    {
        var root = new StackPanel { Name = "RootPanel" };
        var child = new Button { Name = "ChildButton" };
        root.Children.Add(child);

        using var mainViewModel = new MainViewModel(root);
        using var treeViewModel = new TreePageViewModel(mainViewModel, VisualTreeNode.Create(root), new HashSet<string>());

        treeViewModel.TreeFilter.FilterString = "StackPanel";

        var rootNode = Assert.Single(treeViewModel.Nodes);
        Assert.True(rootNode.IsVisible);
        Assert.True(rootNode.IsExpanded);
        var childNode = Assert.Single(rootNode.Children);
        Assert.False(childNode.IsVisible);
    }

    [AvaloniaFact]
    public void TreeFilter_KeepsAncestors_WhenChildMatches()
    {
        var root = new StackPanel { Name = "RootPanel" };
        var child = new Button { Name = "ChildButton" };
        root.Children.Add(child);

        using var mainViewModel = new MainViewModel(root);
        using var treeViewModel = new TreePageViewModel(mainViewModel, VisualTreeNode.Create(root), new HashSet<string>());

        treeViewModel.TreeFilter.FilterString = "Button";

        var rootNode = Assert.Single(treeViewModel.Nodes);
        Assert.True(rootNode.IsVisible);
        var childNode = Assert.Single(rootNode.Children);
        Assert.True(childNode.IsVisible);
    }

    [AvaloniaFact]
    public void TreeFilter_ClearsSelection_WhenNodeHidden()
    {
        var root = new StackPanel { Name = "RootPanel" };
        var child = new Button { Name = "ChildButton" };
        root.Children.Add(child);

        using var mainViewModel = new MainViewModel(root);
        using var treeViewModel = new TreePageViewModel(mainViewModel, VisualTreeNode.Create(root), new HashSet<string>());

        var rootNode = Assert.Single(treeViewModel.Nodes);
        var childNode = Assert.Single(rootNode.Children);
        treeViewModel.SelectedNode = childNode;

        treeViewModel.TreeFilter.FilterString = "NoMatch";

        Assert.Null(treeViewModel.SelectedNode);
        var rootNodeAfterFilter = Assert.Single(treeViewModel.Nodes);
        Assert.False(rootNodeAfterFilter.IsVisible);
        Assert.All(rootNodeAfterFilter.Children, n => Assert.False(n.IsVisible));
    }
}
