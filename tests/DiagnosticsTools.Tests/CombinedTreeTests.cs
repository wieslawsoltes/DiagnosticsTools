using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Diagnostics.ViewModels;
using Avalonia.Headless;
using Avalonia.Threading;
using Xunit;

namespace DiagnosticsTools.Tests
{
    public class CombinedTreeTests : IDisposable
    {
        private static readonly object s_sync = new();
        private static bool s_initialized;

        public CombinedTreeTests()
        {
            lock (s_sync)
            {
                if (!s_initialized)
                {
                    AppBuilder.Configure<TestApp>()
                        .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                        .SetupWithoutStarting();
                    s_initialized = true;
                }
            }
        }

        [Fact]
        public void CombinedTreeNode_tracks_logical_children()
        {
            var panel = new StackPanel();
            var child = new Button();
            panel.Children.Add(child);

            var root = CombinedTreeNode.Create(panel).Single();
            var children = root.Children.ToList();

            Assert.Contains(children, node => ReferenceEquals(node.Visual, child));
        }

        [Fact]
        public void CombinedTreeNode_tracks_template_parts()
        {
            var control = new TestTemplatedControl();
            control.ApplyTemplate();
            Dispatcher.UIThread.RunJobs();

            var root = CombinedTreeNode.Create(control).Single();
            var templateNodes = root.Children.OfType<CombinedTreeNode>()
                .Where(node => node.Role == CombinedTreeNode.CombinedNodeRole.Template)
                .ToList();

            Assert.Contains(templateNodes, node => node.TemplateName == "PART_Content" && ReferenceEquals(node.TemplateOwner, control));
        }

        [Fact]
        public void CombinedTreePageViewModel_selects_template_part()
        {
            var control = new TestTemplatedControl();
            control.ApplyTemplate();
            Dispatcher.UIThread.RunJobs();

            using var mainViewModel = new MainViewModel(control);
            var pinned = new HashSet<string>();
            var combinedTree = CombinedTreePageViewModel.FromRoot(mainViewModel, control, pinned);

            var root = Assert.IsType<CombinedTreeNode>(combinedTree.Nodes.Single());
            var templateNode = root.Children.OfType<CombinedTreeNode>()
                .First(node => node.Role == CombinedTreeNode.CombinedNodeRole.Template);

            combinedTree.SelectControl((Control)templateNode.Visual);

            Assert.Same(templateNode, combinedTree.SelectedNode);
        }

        public void Dispose()
        {
            Dispatcher.UIThread.RunJobs();
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

        private class TestApp : Application
        {
        }
    }
}
