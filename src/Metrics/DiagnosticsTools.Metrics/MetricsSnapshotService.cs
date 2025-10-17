using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Avalonia.Diagnostics.Metrics
{
    public sealed class MetricsSnapshotService
    {
        private static readonly JsonSerializerOptions s_serializerOptions = new(JsonSerializerDefaults.General)
        {
            WriteIndented = true
        };

        public MetricsSnapshot Capture(MetricsListenerService listener)
        {
            if (listener is null)
            {
                throw new ArgumentNullException(nameof(listener));
            }

            var histograms = listener.HistogramSnapshots
                .Select(static stats => new HistogramSnapshot(
                    stats.Name,
                    stats.Minimum,
                    stats.Maximum,
                    stats.Average,
                    stats.Percentile95,
                    stats.Snapshot.ToArray()))
                .ToArray();

            var gauges = listener.GaugeSnapshots
                .Select(static snapshot => new GaugeSnapshot(
                    snapshot.Name,
                    snapshot.Current,
                    snapshot.Minimum,
                    snapshot.Maximum,
                    snapshot.History.ToArray()))
                .ToArray();

            var activities = listener.ActivitySnapshots
                .Select(static pair => new ActivityGroupSnapshot(
                    pair.Key,
                    pair.Value
                        .Select(sample => new ActivityItemSnapshot(
                            sample.Name,
                            sample.Duration,
                            sample.StartTime,
                            sample.ParentId,
                            sample.Id))
                        .ToArray()))
                .ToArray();

            return new MetricsSnapshot(histograms, gauges, activities);
        }

        public string Serialize(MetricsSnapshot snapshot)
        {
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            return JsonSerializer.Serialize(snapshot, s_serializerOptions);
        }
    }

    public sealed class MetricsSnapshot
    {
        public MetricsSnapshot(
            IReadOnlyList<HistogramSnapshot> histograms,
            IReadOnlyList<GaugeSnapshot> gauges,
            IReadOnlyList<ActivityGroupSnapshot> activities)
        {
            Histograms = histograms;
            Gauges = gauges;
            Activities = activities;
        }

        public IReadOnlyList<HistogramSnapshot> Histograms { get; }

        public IReadOnlyList<GaugeSnapshot> Gauges { get; }

        public IReadOnlyList<ActivityGroupSnapshot> Activities { get; }
    }

    public sealed class HistogramSnapshot
    {
        public HistogramSnapshot(
            string name,
            double minimum,
            double maximum,
            double average,
            double percentile95,
            double[] samples)
        {
            Name = name;
            Minimum = minimum;
            Maximum = maximum;
            Average = average;
            Percentile95 = percentile95;
            Samples = samples;
        }

        public string Name { get; }

        public double Minimum { get; }

        public double Maximum { get; }

        public double Average { get; }

        public double Percentile95 { get; }

        public double[] Samples { get; }
    }

    public sealed class GaugeSnapshot
    {
        public GaugeSnapshot(
            string name,
            double current,
            double minimum,
            double maximum,
            double[] history)
        {
            Name = name;
            Current = current;
            Minimum = minimum;
            Maximum = maximum;
            History = history;
        }

        public string Name { get; }

        public double Current { get; }

        public double Minimum { get; }

        public double Maximum { get; }

        public double[] History { get; }
    }

    public sealed class ActivityGroupSnapshot
    {
        public ActivityGroupSnapshot(string name, IReadOnlyList<ActivityItemSnapshot> items)
        {
            Name = name;
            Items = items;
        }

        public string Name { get; }

        public IReadOnlyList<ActivityItemSnapshot> Items { get; }
    }

    public sealed class ActivityItemSnapshot
    {
        public ActivityItemSnapshot(string name, TimeSpan duration, DateTimeOffset startTime, string? parentId, string? id)
        {
            Name = name;
            Duration = duration;
            StartTime = startTime;
            ParentId = parentId;
            Id = id;
        }

        public string Name { get; }

        public TimeSpan Duration { get; }

        public DateTimeOffset StartTime { get; }

        public string? ParentId { get; }

        public string? Id { get; }
    }
}
