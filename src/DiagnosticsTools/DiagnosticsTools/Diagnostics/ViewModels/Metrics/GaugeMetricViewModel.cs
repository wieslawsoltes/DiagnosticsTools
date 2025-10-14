using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Diagnostics.Metrics;

namespace Avalonia.Diagnostics.ViewModels.Metrics
{
    internal class GaugeMetricViewModel : ViewModelBase
    {
        private double _current;
        private double _minimum;
        private double _maximum;
        private double[] _history = Array.Empty<double>();
        private TimedSample[] _timeline = Array.Empty<TimedSample>();
        private DateTimeOffset? _lastSampleTimestamp;
        private double? _warningThreshold;
        private double? _criticalThreshold;
        private bool _isWarning;
        private bool _isCritical;
        private double _delta;
        private bool _hasPrevious;
        private string _thresholdDescription = string.Empty;

        public GaugeMetricViewModel(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public double Current
        {
            get => _current;
            private set => RaiseAndSetIfChanged(ref _current, value);
        }

        public double Minimum
        {
            get => _minimum;
            private set => RaiseAndSetIfChanged(ref _minimum, value);
        }

        public double Maximum
        {
            get => _maximum;
            private set => RaiseAndSetIfChanged(ref _maximum, value);
        }

        public double[] History
        {
            get => _history;
            private set => RaiseAndSetIfChanged(ref _history, value);
        }

        public TimedSample[] Timeline
        {
            get => _timeline;
            private set => RaiseAndSetIfChanged(ref _timeline, value);
        }

        public DateTimeOffset? LastSampleTimestamp
        {
            get => _lastSampleTimestamp;
            private set => RaiseAndSetIfChanged(ref _lastSampleTimestamp, value);
        }

        public double? WarningThreshold
        {
            get => _warningThreshold;
            private set => RaiseAndSetIfChanged(ref _warningThreshold, value);
        }

        public double? CriticalThreshold
        {
            get => _criticalThreshold;
            private set => RaiseAndSetIfChanged(ref _criticalThreshold, value);
        }

        public bool IsWarning
        {
            get => _isWarning;
            private set => RaiseAndSetIfChanged(ref _isWarning, value);
        }

        public bool IsCritical
        {
            get => _isCritical;
            private set => RaiseAndSetIfChanged(ref _isCritical, value);
        }

        public double Delta
        {
            get => _delta;
            private set => RaiseAndSetIfChanged(ref _delta, value);
        }

        public string ThresholdDescription
        {
            get => _thresholdDescription;
            private set => RaiseAndSetIfChanged(ref _thresholdDescription, value);
        }

        public void Update(ObservableGaugeSnapshot snapshot)
        {
            if (_hasPrevious)
            {
                Delta = snapshot.Current - Current;
            }
            else
            {
                Delta = 0;
            }

            Current = snapshot.Current;
            Minimum = snapshot.Minimum;
            Maximum = snapshot.Maximum;
            History = ToArray(snapshot.History);
            Timeline = ToTimeline(snapshot.Timeline);
            LastSampleTimestamp = Timeline.Length > 0
                ? Timeline[Timeline.Length - 1].Timestamp.ToLocalTime()
                : null;
            _hasPrevious = true;
            UpdateStatus();
        }

        internal void SetThresholds(double? warning, double? critical)
        {
            WarningThreshold = warning;
            CriticalThreshold = critical;
            UpdateStatus();
        }

        private static double[] ToArray(IReadOnlyCollection<double> source)
        {
            if (source.Count == 0)
            {
                return Array.Empty<double>();
            }

            return source as double[] ?? source.ToArray();
        }

        private static TimedSample[] ToTimeline(IReadOnlyCollection<TimedSample> timeline)
        {
            if (timeline.Count == 0)
            {
                return Array.Empty<TimedSample>();
            }

            return timeline as TimedSample[] ?? timeline.ToArray();
        }

        private void UpdateStatus()
        {
            var value = Current;
            var isCritical = CriticalThreshold.HasValue && value >= CriticalThreshold.Value;
            var isWarning = WarningThreshold.HasValue && value >= WarningThreshold.Value;

            IsCritical = isCritical;
            IsWarning = isCritical || isWarning;

            if (CriticalThreshold.HasValue || WarningThreshold.HasValue)
            {
                var warningText = WarningThreshold.HasValue ? $"Warning ≥ {WarningThreshold.Value:F0}" : null;
                var criticalText = CriticalThreshold.HasValue ? $"Critical ≥ {CriticalThreshold.Value:F0}" : null;
                if (warningText is not null && criticalText is not null)
                {
                    ThresholdDescription = $"{warningText}, {criticalText}";
                }
                else
                {
                    ThresholdDescription = warningText ?? criticalText ?? string.Empty;
                }
            }
            else
            {
                ThresholdDescription = string.Empty;
            }
        }
    }
}
