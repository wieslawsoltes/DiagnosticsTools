using Avalonia.Controls;
using Avalonia.Diagnostics;
using Avalonia.Diagnostics.ViewModels;
using Avalonia.Diagnostics.ViewModels.Metrics;
using Avalonia.Headless.XUnit;
using Xunit;

namespace DiagnosticsTools.Tests
{
    public sealed class MainViewModelTests
    {
        [AvaloniaFact]
        public void MainViewModel_defaults_to_combined_tab()
        {
            var root = new StackPanel();
            var sourceInfoService = new StubSourceInfoService();
            var sourceNavigator = new StubSourceNavigator();
            using var viewModel = new MainViewModel(root, sourceInfoService, sourceNavigator);

            Assert.Equal(0, viewModel.SelectedTab);
            Assert.IsType<CombinedTreePageViewModel>(viewModel.Content);
        }

        [AvaloniaFact]
        public void MainViewModel_set_options_activates_requested_tab()
        {
            var root = new Button();
            var sourceInfoService = new StubSourceInfoService();
            var sourceNavigator = new StubSourceNavigator();
            using var viewModel = new MainViewModel(root, sourceInfoService, sourceNavigator);

            viewModel.SetOptions(new DevToolsOptions
            {
                LaunchView = DevToolsViewKind.Metrics
            });

            Assert.Equal(4, viewModel.SelectedTab);
            Assert.IsType<MetricsPageViewModel>(viewModel.Content);
        }

        [AvaloniaFact]
        public void MainViewModel_request_tree_navigation_switches_tabs()
        {
            var root = new StackPanel();
            var child = new Button();
            root.Children.Add(child);

            var sourceInfoService = new StubSourceInfoService();
            var sourceNavigator = new StubSourceNavigator();
            using var viewModel = new MainViewModel(root, sourceInfoService, sourceNavigator);

            viewModel.RequestTreeNavigateTo(child, isVisualTree: false);
            Assert.Equal(1, viewModel.SelectedTab);

            viewModel.RequestTreeNavigateTo(child, isVisualTree: true);
            Assert.Equal(2, viewModel.SelectedTab);
        }
    }
}
