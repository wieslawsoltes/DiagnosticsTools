using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows.Input;
using Avalonia.Diagnostics.Metrics;
using Avalonia.Diagnostics.ViewModels;
using Avalonia.Threading;

namespace Avalonia.Diagnostics.ViewModels.Metrics
{
    internal sealed class MetricsPageViewModel : ViewModelBase, IDisposable
    {
        private readonly MetricsListenerService _listener;
        private readonly ObservableCollection<HistogramMetricViewModel> _histograms = new();
        private readonly ObservableCollection<GaugeMetricViewModel> _gauges = new();
        private readonly Dictionary<string, HistogramMetricViewModel> _histogramIndex = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GaugeMetricViewModel> _gaugeIndex = new(StringComparer.Ordinal);
        private readonly DelegateCommand _pauseCommand;
        private readonly DelegateCommand _resumeCommand;
        private readonly DelegateCommand _clearCommand;
        private readonly DelegateCommand _exportCommand;
        private readonly MetricsSnapshotService _snapshotService = new();
        private readonly object _updateLock = new();
        private bool _histogramsDirty;
        private bool _gaugesDirty;
        private bool _activitiesDirty;
        private readonly TimeSpan _throttleInterval;
        private bool _isCapturePaused;
        private DispatcherTimer? _updateTimer;

        public MetricsPageViewModel(MetricsListenerService listener, TimeSpan? throttleInterval = null)
        {
            _listener = listener;
            _throttleInterval = throttleInterval ?? TimeSpan.FromMilliseconds(100);
            Histograms = new ReadOnlyObservableCollection<HistogramMetricViewModel>(_histograms);
            Gauges = new ReadOnlyObservableCollection<GaugeMetricViewModel>(_gauges);
            Timeline = new ActivityTimelineViewModel();

            _pauseCommand = new DelegateCommand(() => IsCapturePaused = true, () => !IsCapturePaused);
            _resumeCommand = new DelegateCommand(() => IsCapturePaused = false, () => IsCapturePaused);
            _clearCommand = new DelegateCommand(ClearMetrics);
            _exportCommand = new DelegateCommand(ExportSnapshot);
            PauseCommand = _pauseCommand;
            ResumeCommand = _resumeCommand;
            ClearCommand = _clearCommand;
            ExportSnapshotCommand = _exportCommand;

            _listener.MetricsUpdated += OnHistogramsDirty;
            _listener.GaugesUpdated += OnGaugesDirty;
            _listener.ActivitiesUpdated += OnActivitiesDirty;

            IsCapturePaused = _listener.IsPaused;
        }

        public ReadOnlyObservableCollection<HistogramMetricViewModel> Histograms { get; }

        public ReadOnlyObservableCollection<GaugeMetricViewModel> Gauges { get; }

        public ActivityTimelineViewModel Timeline { get; }

        public ICommand PauseCommand { get; }

        public ICommand ResumeCommand { get; }

        public ICommand ClearCommand { get; }

        public ICommand ExportSnapshotCommand { get; }

        public event EventHandler<MetricsSnapshotEventArgs>? SnapshotRequested;

        public bool IsCapturePaused
        {
            get => _isCapturePaused;
            set
            {
                if (!RaiseAndSetIfChanged(ref _isCapturePaused, value))
                {
                    return;
                }

                ApplyCaptureState();
            }
        }

        public void Dispose()
        {
            _listener.MetricsUpdated -= OnHistogramsDirty;
            _listener.GaugesUpdated -= OnGaugesDirty;
            _listener.ActivitiesUpdated -= OnActivitiesDirty;

            if (_updateTimer is not null)
            {
                _updateTimer.Stop();
                _updateTimer.Tick -= OnUpdateTimerTick;
                _updateTimer = null;
            }
        }

        private void OnHistogramsDirty(object? sender, EventArgs e)
        {
            RequestUpdate(UpdateKind.Histograms);
        }

        private void OnGaugesDirty(object? sender, EventArgs e)
        {
            RequestUpdate(UpdateKind.Gauges);
        }

        private void OnActivitiesDirty(object? sender, EventArgs e)
        {
            RequestUpdate(UpdateKind.Activities);
        }

        private void ApplyCaptureState()
        {
            if (_isCapturePaused)
            {
                _listener.Pause();
            }
            else
            {
                _listener.Resume();
            }

            Timeline.IsPaused = _isCapturePaused;
            _pauseCommand.RaiseCanExecuteChanged();
            _resumeCommand.RaiseCanExecuteChanged();
        }

        private void RequestUpdate(UpdateKind kind)
        {
            lock (_updateLock)
            {
                switch (kind)
                {
                    case UpdateKind.Histograms:
                        _histogramsDirty = true;
                        break;
                    case UpdateKind.Gauges:
                        _gaugesDirty = true;
                        break;
                    case UpdateKind.Activities:
                        _activitiesDirty = true;
                        break;
                }

                EnsureUpdateScheduled();
            }
        }

        private void EnsureUpdateScheduled()
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(EnsureUpdateScheduled);
                return;
            }

            if (_updateTimer is null)
            {
                _updateTimer = new DispatcherTimer
                {
                    Interval = _throttleInterval
                };
                _updateTimer.Tick += OnUpdateTimerTick;
            }

            _updateTimer.Stop();
            _updateTimer.Start();
        }

        private void OnUpdateTimerTick(object? sender, EventArgs e)
        {
            _updateTimer?.Stop();

            bool updateHistograms;
            bool updateGauges;
            bool updateActivities;

            lock (_updateLock)
            {
                updateHistograms = _histogramsDirty;
                updateGauges = _gaugesDirty;
                updateActivities = _activitiesDirty;
                _histogramsDirty = _gaugesDirty = _activitiesDirty = false;
            }

            if (!updateHistograms && !updateGauges && !updateActivities)
            {
                return;
            }

            // Capture snapshots outside of further dispatcher scheduling to keep timing predictable.
            var histogramSnapshots = updateHistograms ? _listener.HistogramSnapshots : null;
            var gaugeSnapshots = updateGauges ? _listener.GaugeSnapshots : null;
            IReadOnlyList<ActivityTimelineViewModel.ActivityGroupViewModel>? activityGroups = null;

            if (updateActivities && !Timeline.IsPaused)
            {
                var activitySnapshots = _listener.ActivitySnapshots;
                activityGroups = Timeline.BuildGroups(activitySnapshots);
            }
            else
            {
                updateActivities = false;
            }

            if (updateHistograms && histogramSnapshots is not null)
            {
                SynchronizeHistograms(histogramSnapshots);
            }

            if (updateGauges && gaugeSnapshots is not null)
            {
                SynchronizeGauges(gaugeSnapshots);
            }

            if (updateActivities && activityGroups is not null)
            {
                Timeline.ApplyGroups(activityGroups);
            }
        }

        private void ClearMetrics()
        {
            _listener.Clear();
            Timeline.Clear();
        }

        private void ExportSnapshot()
        {
            var snapshot = _snapshotService.Capture(_listener);
            var json = _snapshotService.Serialize(snapshot);
            SnapshotRequested?.Invoke(this, new MetricsSnapshotEventArgs(json));
        }

        private void SynchronizeHistograms(IReadOnlyCollection<HistogramStats> snapshots)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var stats in snapshots.OrderBy(s => s.Name))
            {
                var vm = GetOrCreateHistogram(stats.Name);
                vm.Update(stats);
                seen.Add(stats.Name);
            }

            RemoveAbsent(_histograms, _histogramIndex, seen);
        }

        private void SynchronizeGauges(IReadOnlyCollection<ObservableGaugeSnapshot> snapshots)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var snapshot in snapshots.OrderBy(s => s.Name))
            {
                var vm = GetOrCreateGauge(snapshot.Name);
                vm.Update(snapshot);
                seen.Add(snapshot.Name);
            }

            RemoveAbsent(_gauges, _gaugeIndex, seen);
        }

        private HistogramMetricViewModel GetOrCreateHistogram(string name)
        {
            if (_histogramIndex.TryGetValue(name, out var existing))
            {
                return existing;
            }

            var vm = new HistogramMetricViewModel(name);
            var thresholds = MetricPresentation.GetHistogramThresholds(name);
            vm.SetThresholds(thresholds.Warning, thresholds.Critical);
            _histogramIndex[name] = vm;
            InsertSorted(_histograms, vm, (left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
            return vm;
        }

        private GaugeMetricViewModel GetOrCreateGauge(string name)
        {
            if (_gaugeIndex.TryGetValue(name, out var existing))
            {
                return existing;
            }

            var vm = new GaugeMetricViewModel(name);
            var thresholds = MetricPresentation.GetGaugeThresholds(name);
            vm.SetThresholds(thresholds.Warning, thresholds.Critical);
            _gaugeIndex[name] = vm;
            InsertSorted(_gauges, vm, (left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
            return vm;
        }

        private static void RemoveAbsent<TVm>(ObservableCollection<TVm> collection, Dictionary<string, TVm> index, HashSet<string> seen)
            where TVm : class
        {
            for (var i = collection.Count - 1; i >= 0; i--)
            {
                var vm = collection[i];
                var name = GetName(vm);
                if (!seen.Contains(name))
                {
                    collection.RemoveAt(i);
                    index.Remove(name);
                }
            }
        }

        private static string GetName<TVm>(TVm vm)
        {
            return vm switch
            {
                HistogramMetricViewModel histogram => histogram.Name,
                GaugeMetricViewModel gauge => gauge.Name,
                _ => string.Empty
            };
        }

        private static void InsertSorted<T>(ObservableCollection<T> collection, T item, Comparison<T> comparison)
        {
            var index = 0;
            while (index < collection.Count && comparison(collection[index], item) <= 0)
            {
                index++;
            }

            collection.Insert(index, item);
        }

        private enum UpdateKind
        {
            Histograms,
            Gauges,
            Activities
        }

    }
}
