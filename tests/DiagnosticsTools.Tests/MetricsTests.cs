using System;
using System.Diagnostics;
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
        public void MetricsListenerService_enables_diagnostics_switch()
        {
            using var listener = new MetricsListenerService();

            Assert.True(AppContext.TryGetSwitch("Avalonia.Diagnostics.Diagnostic.IsEnabled", out var enabled) && enabled);
        }

        [AvaloniaFact]
        public async Task MetricsListenerService_ignores_non_prefixed_instruments()
        {
            using var listener = new MetricsListenerService(histogramCapacity: 4);
            using var avaloniaMeter = new Meter("Avalonia.Diagnostics.Tests", "1.0");
            using var otherMeter = new Meter("Other.Diagnostics.Tests", "1.0");
            var avaloniaHistogram = avaloniaMeter.CreateHistogram<double>("avalonia.ui.measure.time");
            var otherHistogram = otherMeter.CreateHistogram<double>("other.render.time");

            avaloniaHistogram.Record(10);
            otherHistogram.Record(20);
            await PumpDispatcherAsync();

            Assert.Contains(listener.HistogramSnapshots, stats => stats.Name == "avalonia.ui.measure.time");
            Assert.DoesNotContain(listener.HistogramSnapshots, stats => stats.Name == "other.render.time");
        }

        [AvaloniaFact]
        public async Task MetricsListenerService_limits_histogram_capacity()
        {
            using var listener = new MetricsListenerService(histogramCapacity: 3);
            using var meter = new Meter("Avalonia.Diagnostics.Tests", "1.0");
            var histogram = meter.CreateHistogram<double>("avalonia.ui.arrange.time");

            for (var i = 1; i <= 5; i++)
            {
                histogram.Record(i);
            }

            await PumpDispatcherAsync();

            var stats = Assert.Single(listener.HistogramSnapshots);
            Assert.Equal(new[] { 3d, 4d, 5d }, stats.Snapshot);
            Assert.Equal(3d, stats.Minimum);
            Assert.Equal(5d, stats.Maximum);
            Assert.Equal(4d, stats.Average);
            Assert.Equal(5d, stats.Percentile95);
        }

        [AvaloniaFact]
        public async Task MetricsListenerService_captures_prefixed_activities()
        {
            using var listener = new MetricsListenerService(activityCapacity: 4);
            using var source = new ActivitySource("Avalonia.Diagnostics.Tests.Activities");

            using (var activity = source.StartActivity("Avalonia.PerformingHitTest"))
            {
                Assert.NotNull(activity);
                await Task.Delay(1);
                activity!.Stop();
            }

            await PumpDispatcherAsync();

            Assert.True(listener.ActivitySnapshots.TryGetValue("Avalonia.PerformingHitTest", out var samples));
            Assert.NotEmpty(samples);
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

            await Task.Delay(50);
            await PumpDispatcherAsync();

            Assert.Single(viewModel.Histograms);
        }

        [AvaloniaFact]
        public async Task MetricsPageViewModel_clear_removes_snapshots()
        {
            if (OperatingSystem.IsMacOS())
            {
                return;
            }

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
        public async Task MetricsPageViewModel_tracks_last_sample_timestamp()
        {
            using var listener = new MetricsListenerService();
            using var meter = new Meter("Avalonia.Diagnostics.Tests", "1.0");
            var histogram = meter.CreateHistogram<double>("avalonia.ui.arrange.time");
            using var viewModel = new MetricsPageViewModel(listener, TimeSpan.Zero);

            histogram.Record(5);
            await PumpDispatcherAsync();

            var metric = Assert.Single(viewModel.Histograms);
            Assert.NotNull(metric.LastSampleTimestamp);
            Assert.NotEmpty(metric.Timeline);
        }

        [AvaloniaFact]
        public void MainViewModel_exposes_metrics_tab()
        {
            var control = new Button();
            var sourceInfoService = new StubSourceInfoService();
            var sourceNavigator = new StubSourceNavigator();
            using var mainViewModel = new MainViewModel(control, sourceInfoService, sourceNavigator);

            mainViewModel.SelectedTab = 4;

            Assert.IsType<MetricsPageViewModel>(mainViewModel.Content);
        }

        private static async Task PumpDispatcherAsync()
        {
            await Task.Delay(10);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        }
    }
}
