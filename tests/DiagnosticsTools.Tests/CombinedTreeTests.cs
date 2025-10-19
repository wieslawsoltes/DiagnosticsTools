using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Diagnostics.ViewModels;
using Avalonia.Diagnostics.Xaml;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Diagnostics.Runtime;
using Avalonia.Diagnostics.SourceNavigation;
using Xunit;

namespace DiagnosticsTools.Tests
{
    public class CombinedTreeTests
    {
        [AvaloniaFact]
        public void CombinedTreeNode_tracks_logical_children()
        {
            var panel = new StackPanel();
            var child = new Button();
            panel.Children.Add(child);

            var root = CombinedTreeNode.Create(panel).Single();
            var children = root.Children.ToList();

            Assert.Contains(children, node => ReferenceEquals(node.Visual, child));
        }

        [AvaloniaFact]
        public void CombinedTreeNode_tracks_template_parts()
        {
            var control = new TestTemplatedControl();
            control.ApplyTemplate();
            Dispatcher.UIThread.RunJobs();

            var root = CombinedTreeNode.Create(control).Single();
            var templateGroup = Assert.Single(root.Children.OfType<CombinedTreeTemplateGroupNode>());
            var templateNodes = templateGroup.Children.OfType<CombinedTreeNode>().ToList();

            Assert.Contains(templateNodes, node => node.TemplateName == "PART_Content" && ReferenceEquals(node.TemplateOwner, control));
        }

        [AvaloniaFact]
        public void CombinedTreeNode_traverses_nested_template_parts()
        {
            var outer = new NestedOuterControl();
            outer.ApplyTemplate();
            Dispatcher.UIThread.RunJobs();

            var inner = outer.GetVisualDescendants().OfType<NestedInnerControl>().FirstOrDefault();
            Assert.NotNull(inner);

            inner!.ApplyTemplate();
            Dispatcher.UIThread.RunJobs();

            var root = CombinedTreeNode.Create(outer).Single();

            var outerTemplateGroup = Assert.Single(root.Children.OfType<CombinedTreeTemplateGroupNode>());
            var innerNode = outerTemplateGroup.Children.OfType<CombinedTreeNode>()
                .First(node => ReferenceEquals(node.Visual, inner));

            var innerTemplateGroup = Assert.Single(innerNode.Children.OfType<CombinedTreeTemplateGroupNode>());
            var nestedTemplateNode = innerTemplateGroup.Children.OfType<CombinedTreeNode>()
                .FirstOrDefault(node => node.TemplateName == "PART_NestedContent");

            Assert.NotNull(nestedTemplateNode);
            Assert.Equal("/template/", innerTemplateGroup.Type);
        }

        [AvaloniaFact]
        public void CombinedTreePageViewModel_selects_template_part()
        {
            var control = new TestTemplatedControl();
            control.ApplyTemplate();
            Dispatcher.UIThread.RunJobs();

            var sourceInfoService = new StubSourceInfoService();
            var sourceNavigator = new StubSourceNavigator();
            using var mainViewModel = new MainViewModel(control, sourceInfoService, sourceNavigator);
            var pinned = new HashSet<string>();
            using var workspace = new XamlAstWorkspace();
            var coordinator = new SelectionCoordinator();
            var combinedTree = CombinedTreePageViewModel.FromRoot(mainViewModel, control, pinned, sourceInfoService, sourceNavigator, workspace, new RuntimeMutationCoordinator(), coordinator, "Test.Tree.Combined");

            var root = Assert.IsType<CombinedTreeNode>(combinedTree.Nodes.Single());
            var templateGroup = Assert.Single(root.Children.OfType<CombinedTreeTemplateGroupNode>());
            var templateNode = templateGroup.Children.OfType<CombinedTreeNode>()
                .First(node => node.Role == CombinedTreeNode.CombinedNodeRole.Template);

            combinedTree.SelectControl((Control)templateNode.Visual);
            Dispatcher.UIThread.RunJobs();

            Assert.Same(templateNode, combinedTree.SelectedNode);
        }

        [AvaloniaFact]
        public void CombinedTree_Filter_Logical_Only_Ignores_Template_Matches()
        {
            var control = new TestTemplatedControl();
            control.ApplyTemplate();
            Dispatcher.UIThread.RunJobs();

            var sourceInfoService = new StubSourceInfoService();
            var sourceNavigator = new StubSourceNavigator();
            using var mainViewModel = new MainViewModel(control, sourceInfoService, sourceNavigator);
            var pinned = new HashSet<string>();
            using var workspace = new XamlAstWorkspace();
            var coordinator = new SelectionCoordinator();
            var combinedTree = CombinedTreePageViewModel.FromRoot(mainViewModel, control, pinned, sourceInfoService, sourceNavigator, workspace, new RuntimeMutationCoordinator(), coordinator, "Test.Tree.Combined");

            combinedTree.TreeFilter.FilterString = "Border";

            var root = Assert.IsType<CombinedTreeNode>(combinedTree.Nodes.Single());
            var templateGroup = root.Children.OfType<CombinedTreeTemplateGroupNode>().Single();
            Assert.False(templateGroup.IsExpanded);

            combinedTree.SearchLogicalNodesOnly = false;

            Assert.True(templateGroup.IsVisible);
            Assert.True(templateGroup.IsExpanded);
        }

        [AvaloniaFact]
        public async Task CombinedTree_SynchronizeSelection_KeepsTargetNode()
        {
            var xaml = """
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <StackPanel x:Name="RootPanel">
    <Button x:Name="ChildButton" Content="Preview" />
  </StackPanel>
</UserControl>
""";

            var tempFile = Path.Combine(Path.GetTempPath(), $"CombinedPreview_{Guid.NewGuid():N}.axaml");
            await File.WriteAllTextAsync(tempFile, xaml);

            try
            {
                var root = new StackPanel { Name = "RootPanel" };
                var child = new Button { Name = "ChildButton" };
                root.Children.Add(child);

                var infoMap = new Dictionary<AvaloniaObject, SourceInfo>
                {
                    [root] = new SourceInfo(tempFile, null, 2, 3, 5, 1, SourceOrigin.Local)
                };

                var service = new DelegatingSourceInfoService(
                    objectResolver: obj => infoMap.TryGetValue(obj, out var info) ? info : null);
                var navigator = new StubSourceNavigator();

                using var mainViewModel = new MainViewModel(root, service, navigator);
                using var workspace = new XamlAstWorkspace();
                var coordinator = new SelectionCoordinator();
                var combinedTree = CombinedTreePageViewModel.FromRoot(
                    mainViewModel,
                    root,
                    new HashSet<string>(),
                    service,
                    navigator,
                    workspace,
                    new RuntimeMutationCoordinator(),
                    coordinator,
                    "Test.Tree.Combined");

                combinedTree.SearchLogicalNodesOnly = false;

                var rootNode = Assert.IsType<CombinedTreeNode>(combinedTree.Nodes.Single());
                var childNode = FindNode(rootNode, child);
                Assert.NotNull(childNode);

                var ensureMethod = typeof(TreePageViewModel).GetMethod("EnsureSourceInfoAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.NotNull(ensureMethod);
                await (Task<SourceInfo?>)ensureMethod!.Invoke(combinedTree, new object?[] { childNode })!;

                var document = await workspace.GetDocumentAsync(tempFile);
                var index = await workspace.GetIndexAsync(tempFile);
                var descriptor = index.Nodes.First(node => string.Equals(node.XamlName, "ChildButton", StringComparison.Ordinal));
                var selection = new XamlAstSelection(document, descriptor, index.Nodes.ToList());

                await Dispatcher.UIThread.InvokeAsync(() => combinedTree.SelectedNode = childNode);

                var syncMethod = typeof(TreePageViewModel).GetMethod("SynchronizeSelectionFromPreview", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.NotNull(syncMethod);
                await Dispatcher.UIThread.InvokeAsync(() => syncMethod!.Invoke(combinedTree, new object?[] { selection }));

                await WaitForAsync(() => ReferenceEquals(combinedTree.SelectedNode, childNode));

                Assert.Same(childNode, combinedTree.SelectedNode);
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }

        private static CombinedTreeNode? FindNode(CombinedTreeNode root, AvaloniaObject target)
        {
            if (ReferenceEquals(root.Visual, target))
            {
                return root;
            }

            foreach (var child in root.Children)
            {
                if (child is CombinedTreeNode combined)
                {
                    var result = FindNode(combined, target);
                    if (result is not null)
                    {
                        return result;
                    }
                }
            }

            return null;
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

        private class TestTemplatedControl : TemplatedControl
        {
            public TestTemplatedControl()
            {
                Template = new FuncControlTemplate<TestTemplatedControl>((owner, _) => new Border
                {
                    Name = "PART_Content"
                });
            }
        }

        private class NestedOuterControl : TemplatedControl
        {
            public NestedOuterControl()
            {
                Template = new FuncControlTemplate<NestedOuterControl>((owner, _) => new NestedInnerControl
                {
                    Name = "PART_Inner"
                });
            }
        }

        private class NestedInnerControl : TemplatedControl
        {
            public NestedInnerControl()
            {
                Template = new FuncControlTemplate<NestedInnerControl>((owner, _) => new Border
                {
                    Name = "PART_NestedContent"
                });
            }
        }
    }
}
