using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Avalonia.Diagnostics.Metrics
{
    internal sealed class MetricsListenerService : IDisposable
    {
        private readonly MeterListener _meterListener;
        private readonly ActivityListener _activityListener;
        private readonly SynchronizationContext? _syncContext;
        private readonly ConcurrentQueue<Action> _pendingActions = new();
        private readonly SemaphoreSlim _workSignal = new(0);
        private readonly CancellationTokenSource _workerCts = new();
        private readonly Task _workerTask;

        private readonly ConcurrentDictionary<string, HistogramStats> _histograms = new();
        private readonly ConcurrentDictionary<string, ObservableGaugeSnapshot> _gauges = new();
        private readonly ConcurrentDictionary<string, RingBuffer<ActivitySample>> _activities = new();

        private readonly int _gaugeHistoryCapacity;
        private readonly int _histogramCapacity;
        private readonly int _activityCapacity;
        private int _isPaused;
    private volatile bool _isDisposed;

        private static readonly SendOrPostCallback RunBatchCallback = state => ((Action)state!).Invoke();

        public MetricsListenerService(
            int histogramCapacity = 120,
            int gaugeHistoryCapacity = 60,
            int activityCapacity = 512)
        {
            DiagnosticInstrumentation.EnsureInitialized();

            _histogramCapacity = histogramCapacity;
            _gaugeHistoryCapacity = gaugeHistoryCapacity;
            _activityCapacity = activityCapacity;

            _meterListener = new MeterListener
            {
                InstrumentPublished = OnInstrumentPublished
            };

            _meterListener.SetMeasurementEventCallback<double>(OnHistogramMeasurementRecorded);
            _meterListener.SetMeasurementEventCallback<long>(OnGaugeMeasurementRecorded);
            _meterListener.SetMeasurementEventCallback<int>(OnGaugeMeasurementRecorded);

            _activityListener = new ActivityListener
            {
                ShouldListenTo = ShouldListenToActivitySource,
                Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
                ActivityStopped = OnActivityStopped
            };

            _syncContext = SynchronizationContext.Current;
            _workerTask = Task.Run(ProcessQueueAsync);

            ActivitySource.AddActivityListener(_activityListener);
            _meterListener.Start();
        }

        public event EventHandler? MetricsUpdated;
        public event EventHandler? GaugesUpdated;
        public event EventHandler? ActivitiesUpdated;

        public IReadOnlyCollection<HistogramStats> HistogramSnapshots => _histograms.Values.ToArray();

        public IReadOnlyCollection<ObservableGaugeSnapshot> GaugeSnapshots => _gauges.Values.ToArray();

        public IReadOnlyDictionary<string, IReadOnlyCollection<ActivitySample>> ActivitySnapshots =>
            _activities.ToDictionary(pair => pair.Key, pair => (IReadOnlyCollection<ActivitySample>)pair.Value.ToArray());

        public bool IsPaused => Volatile.Read(ref _isPaused) == 1;

        public bool Pause()
        {
            return Interlocked.Exchange(ref _isPaused, 1) == 0;
        }

        public bool Resume()
        {
            return Interlocked.Exchange(ref _isPaused, 0) == 1;
        }

        public void Clear()
        {
            EnqueueOnContext(() =>
            {
                _histograms.Clear();
                _gauges.Clear();
                _activities.Clear();

                MetricsUpdated?.Invoke(this, EventArgs.Empty);
                GaugesUpdated?.Invoke(this, EventArgs.Empty);
                ActivitiesUpdated?.Invoke(this, EventArgs.Empty);
            });
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _meterListener.Dispose();
            // ActivityListener removed automatically when disposed
            _activityListener.Dispose();

            _workerCts.Cancel();
            _workSignal.Release();

            try
            {
                _workerTask.Wait(TimeSpan.FromSeconds(1));
            }
            catch (OperationCanceledException)
            {
            }
            catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
            {
            }

            _workSignal.Dispose();
            _workerCts.Dispose();
        }

        private void OnInstrumentPublished(Instrument instrument, MeterListener listener)
        {
            if (!instrument.Meter.Name.StartsWith(MetricIdentifiers.MeterPrefix))
            {
                return;
            }

            listener.EnableMeasurementEvents(instrument);
        }

        private void OnHistogramMeasurementRecorded(Instrument instrument, double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        {
            if (Volatile.Read(ref _isPaused) == 1)
            {
                return;
            }

            EnqueueOnContext(() =>
            {
                var stats = _histograms.GetOrAdd(instrument.Name, name => new HistogramStats(name, _histogramCapacity));
                stats.Add(measurement, DateTimeOffset.UtcNow);
                MetricsUpdated?.Invoke(this, EventArgs.Empty);
            });
        }

        private void OnGaugeMeasurementRecorded(Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        {
            OnGaugeMeasurementRecorded(instrument, (double)measurement, tags, state);
        }

        private void OnGaugeMeasurementRecorded(Instrument instrument, int measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        {
            OnGaugeMeasurementRecorded(instrument, (double)measurement, tags, state);
        }

        private void OnGaugeMeasurementRecorded(Instrument instrument, double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        {
            if (Volatile.Read(ref _isPaused) == 1)
            {
                return;
            }

            EnqueueOnContext(() =>
            {
                var snapshot = _gauges.GetOrAdd(instrument.Name, name => new ObservableGaugeSnapshot(name, _gaugeHistoryCapacity));
                snapshot.Update(measurement, DateTimeOffset.UtcNow);
                GaugesUpdated?.Invoke(this, EventArgs.Empty);
            });
        }

        private bool ShouldListenToActivitySource(ActivitySource source)
        {
            return source.Name.StartsWith(MetricIdentifiers.MeterPrefix);
        }

        private void OnActivityStopped(Activity activity)
        {
            if (activity.Duration == TimeSpan.Zero)
            {
                return;
            }

            if (Volatile.Read(ref _isPaused) == 1)
            {
                return;
            }

            EnqueueOnContext(() =>
            {
                var buffer = _activities.GetOrAdd(activity.OperationName, name => new RingBuffer<ActivitySample>(_activityCapacity));
                buffer.Add(new ActivitySample(
                    activity.OperationName,
                    activity.Duration,
                    activity.StartTimeUtc,
                    activity.ParentId,
                    activity.Id));
                ActivitiesUpdated?.Invoke(this, EventArgs.Empty);
            });
        }

        private void EnqueueOnContext(Action action)
        {
            if (_isDisposed)
            {
                return;
            }

            _pendingActions.Enqueue(action);
            _workSignal.Release();
        }

        private async Task ProcessQueueAsync()
        {
            try
            {
                while (true)
                {
                    await _workSignal.WaitAsync(_workerCts.Token).ConfigureAwait(false);

                    var batch = new List<Action>();

                    while (_pendingActions.TryDequeue(out var action))
                    {
                        batch.Add(action);
                    }

                    if (batch.Count == 0)
                    {
                        continue;
                    }

                    DispatchBatch(batch);
                }
            }
            catch (OperationCanceledException)
            {
                // Worker is shutting down.
            }
        }

        private void DispatchBatch(List<Action> batch)
        {
            if (batch.Count == 0)
            {
                return;
            }

            var actions = batch.ToArray();

            void RunActions()
            {
                foreach (var action in actions)
                {
                    action();
                }
            }

            if (_syncContext is not null)
            {
                _syncContext.Post(RunBatchCallback, (Action)RunActions);
                return;
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                RunActions();
                return;
            }

            Dispatcher.UIThread.Post(RunActions, DispatcherPriority.Background);
        }

        private sealed class RingBuffer<T>
        {
            private readonly T[] _items;
            private int _nextIndex;
            private int _count;

            public RingBuffer(int capacity)
            {
                _items = new T[capacity];
            }

            public void Add(T item)
            {
                _items[_nextIndex] = item;
                _nextIndex = (_nextIndex + 1) % _items.Length;
                if (_count < _items.Length)
                {
                    _count++;
                }
            }

            public IReadOnlyCollection<T> ToArray()
            {
                var result = new T[_count];
                for (var i = 0; i < _count; i++)
                {
                    var index = (_nextIndex - _count + i + _items.Length) % _items.Length;
                    result[i] = _items[index];
                }

                return result;
            }
        }
    }
}
