using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Diagnostics.SourceNavigation;
using Avalonia.Diagnostics.ViewModels;
using Avalonia.Diagnostics.Xaml;
using Avalonia.Diagnostics.Runtime;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
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
        var coordinator = new SelectionCoordinator();
        using var treeViewModel = new TreePageViewModel(mainViewModel, VisualTreeNode.Create(root), new HashSet<string>(), sourceInfoService, sourceNavigator, workspace, new RuntimeMutationCoordinator(), null, null, coordinator, "Test.Tree");

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
        var coordinator = new SelectionCoordinator();
        using var treeViewModel = new TreePageViewModel(mainViewModel, VisualTreeNode.Create(root), new HashSet<string>(), sourceInfoService, sourceNavigator, workspace, new RuntimeMutationCoordinator(), null, null, coordinator, "Test.Tree");

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
        var coordinator = new SelectionCoordinator();
        using var treeViewModel = new TreePageViewModel(mainViewModel, VisualTreeNode.Create(root), new HashSet<string>(), sourceInfoService, sourceNavigator, workspace, new RuntimeMutationCoordinator(), null, null, coordinator, "Test.Tree");

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
        var coordinator = new SelectionCoordinator();
        using var treeViewModel = new TreePageViewModel(mainViewModel, VisualTreeNode.Create(root), new HashSet<string>(), sourceInfoService, sourceNavigator, workspace, new RuntimeMutationCoordinator(), null, null, coordinator, "Test.Tree");

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
        var coordinator = new SelectionCoordinator();
        using var treeViewModel = new TreePageViewModel(mainViewModel, VisualTreeNode.Create(root), new HashSet<string>(), sourceInfoService, sourceNavigator, workspace, new RuntimeMutationCoordinator(), null, null, coordinator, "Test.Tree");

        treeViewModel.SelectedTreeSearchField = TreeSearchField.Classes;
        treeViewModel.TreeFilter.FilterString = ".highlighted";

        var rootNode = Assert.Single(treeViewModel.Nodes);
        Assert.True(rootNode.IsVisible);
        var childNode = Assert.Single(rootNode.Children);
        Assert.True(childNode.IsVisible);
    }

    [AvaloniaFact]
    public async Task RemoteSource_HydratesAstSelection()
    {
        var xaml = """
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <StackPanel x:Name="RootPanel">
    <Button x:Name="RemoteButton" Content="Hello" />
  </StackPanel>
</UserControl>
""";
        var tempFile = Path.Combine(Path.GetTempPath(), $"RemotePreview_{Guid.NewGuid():N}.axaml");
        await File.WriteAllTextAsync(tempFile, xaml);

        try
        {
            var remoteUri = new Uri(tempFile);
            var root = new StackPanel { Name = "RootPanel" };
            var button = new Button { Name = "RemoteButton" };
            root.Children.Add(button);

            var infoMap = new Dictionary<AvaloniaObject, SourceInfo>
            {
                [root] = new SourceInfo(null, remoteUri, 2, 3, 5, 1, SourceOrigin.SourceLink),
                [button] = new SourceInfo(null, remoteUri, 3, 5, 3, 40, SourceOrigin.SourceLink)
            };

            var service = new DelegatingSourceInfoService(
                objectResolver: obj => infoMap.TryGetValue(obj, out var info) ? info : null);
            var navigator = new StubSourceNavigator();

            using var mainViewModel = new MainViewModel(root, service, navigator);
            using var workspace = new XamlAstWorkspace();
            var coordinator = new SelectionCoordinator();
            using var treeViewModel = new TreePageViewModel(
                mainViewModel,
                VisualTreeNode.Create(root),
                new HashSet<string>(),
                service,
                navigator,
                workspace,
                new RuntimeMutationCoordinator(),
                null,
                null,
                coordinator,
                "Test.Tree");

            var rootNode = Assert.Single(treeViewModel.Nodes);
            var buttonNode = Assert.Single(rootNode.Children);

            treeViewModel.SelectedNode = buttonNode;
            await WaitForAsync(() => treeViewModel.SelectedNodeXaml?.Node is not null);

            var selection = treeViewModel.SelectedNodeXaml;
            Assert.NotNull(selection);
            Assert.NotNull(selection!.Node);
            Assert.Equal(Path.GetFullPath(tempFile), selection.Document.Path);

            var cacheField = typeof(TreePageViewModel).GetField("_cachedDocumentRemoteIndex", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(cacheField);
            var cache = (System.Collections.Concurrent.ConcurrentDictionary<string, string>)cacheField!.GetValue(treeViewModel)!;
            var normalized = Path.GetFullPath(tempFile);
            Assert.True(cache.TryGetValue(normalized, out var remoteKey));
            Assert.Equal(remoteUri.AbsoluteUri, remoteKey);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [AvaloniaFact]
    public async Task PreviewSelection_EscapesScope_AndCachesDescriptor()
    {
        var xaml = """
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <StackPanel x:Name="RootPanel">
    <Button x:Name="FirstButton" Content="First" />
    <Button x:Name="SecondButton" Content="Second" />
  </StackPanel>
</UserControl>
""";

        var tempFile = Path.Combine(Path.GetTempPath(), $"ScopedPreview_{Guid.NewGuid():N}.axaml");
        await File.WriteAllTextAsync(tempFile, xaml);

        try
        {
            var root = new StackPanel { Name = "RootPanel" };
            var first = new Button { Name = "FirstButton" };
            var second = new Button { Name = "SecondButton" };
            root.Children.Add(first);
            root.Children.Add(second);

            var infoMap = new Dictionary<AvaloniaObject, SourceInfo>
            {
                [root] = new SourceInfo(tempFile, null, 2, 3, 5, 1, SourceOrigin.Local),
                [first] = new SourceInfo(tempFile, null, 3, 5, 3, 40, SourceOrigin.Local),
                [second] = new SourceInfo(tempFile, null, 4, 5, 4, 40, SourceOrigin.Local)
            };
            Assert.True(infoMap.TryGetValue(second, out var expectedInfo));
            Assert.Equal(4, expectedInfo.StartLine);

            var service = new DelegatingSourceInfoService(
                objectResolver: obj => infoMap.TryGetValue(obj, out var info) ? info : null);
            var resolvedInfo = await service.GetForAvaloniaObjectAsync(second);
            Assert.Equal(4, resolvedInfo?.StartLine);
            var navigator = new StubSourceNavigator();

            using var mainViewModel = new MainViewModel(root, service, navigator);
            using var workspace = new XamlAstWorkspace();
            var coordinator = new SelectionCoordinator();
            using var treeViewModel = new TreePageViewModel(
                mainViewModel,
                VisualTreeNode.Create(root),
                new HashSet<string>(),
                service,
                navigator,
                workspace,
                new RuntimeMutationCoordinator(),
                null,
                null,
                coordinator,
                "Test.Tree");

            var rootNode = Assert.Single(treeViewModel.Nodes);
            var firstNode = treeViewModel.Nodes[0].Children[0];
            var secondNode = treeViewModel.Nodes[0].Children[1];
            Assert.True(ReferenceEquals(secondNode.Visual, second));

            treeViewModel.SelectedNode = firstNode;
            await WaitForAsync(() => treeViewModel.SelectedNodeXaml?.Node is not null);

            treeViewModel.ScopeToSubTree();
            Assert.True(treeViewModel.IsScoped);

            var document = await workspace.GetDocumentAsync(tempFile);
            var index = await workspace.GetIndexAsync(tempFile);
            var secondDescriptor = index.Nodes.First(node => string.Equals(node.XamlName, "SecondButton", StringComparison.Ordinal));
            var selection = new XamlAstSelection(document, secondDescriptor, index.Nodes.ToList());

            var ensureMethod = typeof(TreePageViewModel).GetMethod("EnsureSourceInfoAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(ensureMethod);
            var ensureTask = (Task<SourceInfo?>)ensureMethod!.Invoke(treeViewModel, new object?[] { secondNode })!;
            var ensuredInfo = await ensureTask;
            Assert.Equal(4, ensuredInfo?.StartLine);

            var findMethod = typeof(TreePageViewModel).GetMethod("FindNodeBySelectionAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(findMethod);
            var lookupTask = (Task<TreeNode?>)findMethod!.Invoke(treeViewModel, new object?[] { selection })!;
            var locatedNode = await lookupTask;
            Assert.NotNull(secondNode.SourceInfo);
            Assert.Equal(4, secondNode.SourceInfo!.StartLine);
            Assert.Equal(4, locatedNode?.SourceInfo?.StartLine);
            Assert.Same(secondNode, locatedNode);

            var syncMethod = typeof(TreePageViewModel).GetMethod("SynchronizeSelectionFromPreview", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(syncMethod);

            await Dispatcher.UIThread.InvokeAsync(() => syncMethod!.Invoke(treeViewModel, new object?[] { selection }));
            await WaitForAsync(() => ReferenceEquals(treeViewModel.SelectedNode?.Visual, second));

            Assert.False(treeViewModel.IsScoped);
            Assert.Same(second, treeViewModel.SelectedNode?.Visual);

            var mapField = typeof(TreePageViewModel).GetField("_nodesByXamlId", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(mapField);
            var nodeMap = (Dictionary<XamlAstNodeId, TreeNode>)mapField!.GetValue(treeViewModel)!;
            Assert.True(nodeMap.TryGetValue(secondDescriptor.Id, out var mappedNode));
            Assert.Same(treeViewModel.SelectedNode, mappedNode);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMilliseconds(500));

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                break;
            }

            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        }
    }
}
