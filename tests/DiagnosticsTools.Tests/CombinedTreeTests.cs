using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Diagnostics.ViewModels;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

            using var mainViewModel = new MainViewModel(control);
            var pinned = new HashSet<string>();
            var combinedTree = CombinedTreePageViewModel.FromRoot(mainViewModel, control, pinned);

            var root = Assert.IsType<CombinedTreeNode>(combinedTree.Nodes.Single());
            var templateGroup = Assert.Single(root.Children.OfType<CombinedTreeTemplateGroupNode>());
            var templateNode = templateGroup.Children.OfType<CombinedTreeNode>()
                .First(node => node.Role == CombinedTreeNode.CombinedNodeRole.Template);

            combinedTree.SelectControl((Control)templateNode.Visual);

            Assert.Same(templateNode, combinedTree.SelectedNode);
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
