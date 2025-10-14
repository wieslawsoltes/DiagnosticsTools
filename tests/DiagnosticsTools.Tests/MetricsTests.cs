using System;
using System.Diagnostics.Metrics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Diagnostics.Metrics;
using Avalonia.Diagnostics.ViewModels;
using Avalonia.Diagnostics.ViewModels.Metrics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace DiagnosticsTools.Tests
{
    public sealed class MetricsTests
    {
        [AvaloniaFact]
        public async Task MetricsPageViewModel_flags_critical_histogram()
        {
            using var listener = new MetricsListenerService();
            using var meter = new Meter("Avalonia.Diagnostics.Tests", "1.0");
            var histogram = meter.CreateHistogram<double>("avalonia.ui.render.time");
            using var viewModel = new MetricsPageViewModel(listener, TimeSpan.Zero);

            histogram.Record(45);
            await PumpDispatcherAsync();

            var metric = Assert.Single(viewModel.Histograms);
            Assert.True(metric.IsCritical);
            Assert.True(metric.IsWarning);
        }

        [AvaloniaFact]
        public async Task MetricsPageViewModel_pause_blocks_new_samples()
        {
            using var listener = new MetricsListenerService();
            using var meter = new Meter("Avalonia.Diagnostics.Tests", "1.0");
            var histogram = meter.CreateHistogram<double>("avalonia.ui.render.time");
            using var viewModel = new MetricsPageViewModel(listener, TimeSpan.Zero);

            viewModel.IsCapturePaused = true;
            histogram.Record(45);
            await PumpDispatcherAsync();

            Assert.Empty(viewModel.Histograms);

            viewModel.IsCapturePaused = false;
            histogram.Record(20);
            await PumpDispatcherAsync();

            Assert.Single(viewModel.Histograms);
        }

        [AvaloniaFact]
        public async Task MetricsPageViewModel_clear_removes_snapshots()
        {
            using var listener = new MetricsListenerService();
            using var meter = new Meter("Avalonia.Diagnostics.Tests", "1.0");
            var histogram = meter.CreateHistogram<double>("avalonia.ui.render.time");
            using var viewModel = new MetricsPageViewModel(listener, TimeSpan.Zero);

            histogram.Record(10);
            await PumpDispatcherAsync();
            Assert.Single(viewModel.Histograms);

            viewModel.ClearCommand.Execute(null);
            await PumpDispatcherAsync();

            Assert.Empty(viewModel.Histograms);
        }

        [AvaloniaFact]
        public async Task MetricsPageViewModel_export_raises_event()
        {
            using var listener = new MetricsListenerService();
            using var meter = new Meter("Avalonia.Diagnostics.Tests", "1.0");
            var histogram = meter.CreateHistogram<double>("avalonia.ui.render.time");
            using var viewModel = new MetricsPageViewModel(listener, TimeSpan.Zero);

            histogram.Record(12);
            await PumpDispatcherAsync();
            Assert.NotEmpty(viewModel.Histograms);

            string? exported = null;
            viewModel.SnapshotRequested += (_, args) => exported = args.Json;

            viewModel.ExportSnapshotCommand.Execute(null);
            await PumpDispatcherAsync();

            Assert.False(string.IsNullOrWhiteSpace(exported));
            Assert.Contains("avalonia.ui.render.time", exported!, StringComparison.OrdinalIgnoreCase);
        }

        [AvaloniaFact]
        public void MainViewModel_exposes_metrics_tab()
        {
            var control = new Button();
            using var mainViewModel = new MainViewModel(control);

            mainViewModel.SelectedTab = 4;

            Assert.IsType<MetricsPageViewModel>(mainViewModel.Content);
        }

        private static async Task PumpDispatcherAsync()
        {
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        }
    }
}
