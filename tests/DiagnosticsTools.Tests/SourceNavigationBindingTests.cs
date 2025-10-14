using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Diagnostics.SourceNavigation;
using Avalonia.Diagnostics.ViewModels;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace DiagnosticsTools.Tests
{
    public sealed class SourceNavigationBindingTests
    {
    [AvaloniaFact]
    public async Task TreePageViewModel_populates_source_info_for_selected_node()
        {
            var root = new StackPanel { Name = "RootPanel" };
            var child = new Button { Name = "ChildButton" };
            root.Children.Add(child);

            var sourceInfo = new SourceInfo(
                LocalPath: "/tmp/MainWindow.axaml",
                RemoteUri: null,
                StartLine: 42,
                StartColumn: 5,
                EndLine: null,
                EndColumn: null,
                Origin: SourceOrigin.Local);

            var infoService = new DelegatingSourceInfoService(
                objectResolver: obj => ReferenceEquals(obj, child) ? sourceInfo : null);
            var navigator = new StubSourceNavigator();

            using var mainViewModel = new MainViewModel(root, infoService, navigator);
            using var treeViewModel = new TreePageViewModel(
                mainViewModel,
                VisualTreeNode.Create(root),
                new HashSet<string>(),
                infoService,
                navigator);

            var rootNode = Assert.Single(treeViewModel.Nodes);
            var childNode = Assert.Single(rootNode.Children);

            treeViewModel.SelectedNode = childNode;
            await WaitForAsync(() => treeViewModel.HasSelectedNodeSource);

            Assert.True(treeViewModel.HasSelectedNodeSource);
            Assert.True(treeViewModel.CanNavigateToSource);
            Assert.Equal(sourceInfo, treeViewModel.SelectedNodeSourceInfo);
            Assert.Equal(sourceInfo, childNode.SourceInfo);
            Assert.True(childNode.HasSource);
        }

        [AvaloniaFact]
        public async Task TreePageViewModel_navigate_command_invokes_navigator()
        {
            var root = new StackPanel { Name = "RootPanel" };
            var child = new Button { Name = "ChildButton" };
            root.Children.Add(child);

            var sourceInfo = new SourceInfo(
                LocalPath: "/tmp/MainWindow.axaml",
                RemoteUri: null,
                StartLine: 12,
                StartColumn: 3,
                EndLine: null,
                EndColumn: null,
                Origin: SourceOrigin.Local);

            var infoService = new DelegatingSourceInfoService(
                objectResolver: obj => ReferenceEquals(obj, child) ? sourceInfo : null);
            var navigator = new RecordingSourceNavigator();

            using var mainViewModel = new MainViewModel(root, infoService, navigator);
            using var treeViewModel = new TreePageViewModel(
                mainViewModel,
                VisualTreeNode.Create(root),
                new HashSet<string>(),
                infoService,
                navigator);

            var rootNode = Assert.Single(treeViewModel.Nodes);
            var childNode = Assert.Single(rootNode.Children);

            treeViewModel.SelectedNode = childNode;
            await WaitForAsync(() => treeViewModel.HasSelectedNodeSource);

            treeViewModel.NavigateToSource();
            await WaitForAsync(() => navigator.LastNavigation is not null);

            Assert.Equal(sourceInfo, navigator.LastNavigation);
        }

        private sealed class RecordingSourceNavigator : ISourceNavigator
        {
            public SourceInfo? LastNavigation { get; private set; }

            public ValueTask NavigateAsync(SourceInfo sourceInfo)
            {
                LastNavigation = sourceInfo;
                return ValueTask.CompletedTask;
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
}
