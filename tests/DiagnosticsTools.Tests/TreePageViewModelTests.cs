using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Diagnostics.ViewModels;
using Avalonia.Diagnostics.Xaml;
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

        var sourceInfoService = new StubSourceInfoService();
       var sourceNavigator = new StubSourceNavigator();
       using var mainViewModel = new MainViewModel(root, sourceInfoService, sourceNavigator);
        using var workspace = new XamlAstWorkspace();
        using var treeViewModel = new TreePageViewModel(mainViewModel, VisualTreeNode.Create(root), new HashSet<string>(), sourceInfoService, sourceNavigator, workspace);

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

        var sourceInfoService = new StubSourceInfoService();
       var sourceNavigator = new StubSourceNavigator();
       using var mainViewModel = new MainViewModel(root, sourceInfoService, sourceNavigator);
        using var workspace = new XamlAstWorkspace();
        using var treeViewModel = new TreePageViewModel(mainViewModel, VisualTreeNode.Create(root), new HashSet<string>(), sourceInfoService, sourceNavigator, workspace);

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

        var sourceInfoService = new StubSourceInfoService();
        var sourceNavigator = new StubSourceNavigator();
        using var mainViewModel = new MainViewModel(root, sourceInfoService, sourceNavigator);
        using var workspace = new XamlAstWorkspace();
        using var treeViewModel = new TreePageViewModel(mainViewModel, VisualTreeNode.Create(root), new HashSet<string>(), sourceInfoService, sourceNavigator, workspace);

        var rootNode = Assert.Single(treeViewModel.Nodes);
        var childNode = Assert.Single(rootNode.Children);
        treeViewModel.SelectedNode = childNode;

        treeViewModel.TreeFilter.FilterString = "NoMatch";

        Assert.Null(treeViewModel.SelectedNode);
        var rootNodeAfterFilter = Assert.Single(treeViewModel.Nodes);
        Assert.False(rootNodeAfterFilter.IsVisible);
        Assert.All(rootNodeAfterFilter.Children, n => Assert.False(n.IsVisible));
    }

    [AvaloniaFact]
    public void TreeFilter_Matches_Name_When_Name_Field_Selected()
    {
        var root = new StackPanel { Name = "RootPanel" };
        var child = new Button { Name = "PART_Control" };
        root.Children.Add(child);

        var sourceInfoService = new StubSourceInfoService();
        var sourceNavigator = new StubSourceNavigator();
        using var mainViewModel = new MainViewModel(root, sourceInfoService, sourceNavigator);
        using var workspace = new XamlAstWorkspace();
        using var treeViewModel = new TreePageViewModel(mainViewModel, VisualTreeNode.Create(root), new HashSet<string>(), sourceInfoService, sourceNavigator, workspace);

        treeViewModel.SelectedTreeSearchField = TreeSearchField.Name;
        treeViewModel.TreeFilter.FilterString = "PART_Control";

        var rootNode = Assert.Single(treeViewModel.Nodes);
        Assert.True(rootNode.IsVisible);
        var childNode = Assert.Single(rootNode.Children);
        Assert.True(childNode.IsVisible);
    }

    [AvaloniaFact]
    public void TreeFilter_Matches_Class_When_Classes_Field_Selected()
    {
        var root = new StackPanel { Name = "RootPanel" };
        var child = new Button { Name = "ChildButton" };
        child.Classes.Add("highlighted");
        root.Children.Add(child);

        var sourceInfoService = new StubSourceInfoService();
        var sourceNavigator = new StubSourceNavigator();
        using var mainViewModel = new MainViewModel(root, sourceInfoService, sourceNavigator);
        using var workspace = new XamlAstWorkspace();
        using var treeViewModel = new TreePageViewModel(mainViewModel, VisualTreeNode.Create(root), new HashSet<string>(), sourceInfoService, sourceNavigator, workspace);

        treeViewModel.SelectedTreeSearchField = TreeSearchField.Classes;
        treeViewModel.TreeFilter.FilterString = ".highlighted";

        var rootNode = Assert.Single(treeViewModel.Nodes);
        Assert.True(rootNode.IsVisible);
        var childNode = Assert.Single(rootNode.Children);
        Assert.True(childNode.IsVisible);
    }
}
