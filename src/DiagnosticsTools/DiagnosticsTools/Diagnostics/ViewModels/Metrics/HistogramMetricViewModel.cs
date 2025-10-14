using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Diagnostics.Metrics;

namespace Avalonia.Diagnostics.ViewModels.Metrics
{
    internal class HistogramMetricViewModel : ViewModelBase
    {
        private double _minimum;
        private double _maximum;
        private double _average;
        private double _percentile95;
        private double[] _samples = Array.Empty<double>();
        private TimedSample[] _timeline = Array.Empty<TimedSample>();
        private DateTimeOffset? _lastSampleTimestamp;
        private double? _warningThreshold;
        private double? _criticalThreshold;
        private bool _isWarning;
        private bool _isCritical;
        private string _thresholdDescription = string.Empty;

        public HistogramMetricViewModel(string name)
        {
            Name = name;
        }

        public string Name { get; }

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

        public double Average
        {
            get => _average;
            private set => RaiseAndSetIfChanged(ref _average, value);
        }

        public double Percentile95
        {
            get => _percentile95;
            private set => RaiseAndSetIfChanged(ref _percentile95, value);
        }

        public double[] Samples
        {
            get => _samples;
            private set => RaiseAndSetIfChanged(ref _samples, value);
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

        public string ThresholdDescription
        {
            get => _thresholdDescription;
            private set => RaiseAndSetIfChanged(ref _thresholdDescription, value);
        }

        public void Update(HistogramStats stats)
        {
            Minimum = stats.Minimum;
            Maximum = stats.Maximum;
            Average = stats.Average;
            Percentile95 = stats.Percentile95;
            Samples = ToArray(stats.Snapshot);
            Timeline = ToTimeline(stats.Timeline);
            LastSampleTimestamp = Timeline.Length > 0
                ? Timeline[Timeline.Length - 1].Timestamp.ToLocalTime()
                : null;
            UpdateStatus();
        }

        internal void SetThresholds(double? warning, double? critical)
        {
            WarningThreshold = warning;
            CriticalThreshold = critical;
            UpdateStatus();
        }

        private static double[] ToArray(IReadOnlyCollection<double> snapshot)
        {
            if (snapshot.Count == 0)
            {
                return Array.Empty<double>();
            }

            return snapshot as double[] ?? snapshot.ToArray();
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
            var value = Percentile95;

            var isCritical = CriticalThreshold.HasValue && value >= CriticalThreshold.Value;
            var isWarning = WarningThreshold.HasValue && value >= WarningThreshold.Value;

            IsCritical = isCritical;
            IsWarning = isCritical || isWarning;

            if (CriticalThreshold.HasValue || WarningThreshold.HasValue)
            {
                var warningText = WarningThreshold.HasValue ? $"Warning ≥ {WarningThreshold.Value:F1} ms" : null;
                var criticalText = CriticalThreshold.HasValue ? $"Critical ≥ {CriticalThreshold.Value:F1} ms" : null;
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
