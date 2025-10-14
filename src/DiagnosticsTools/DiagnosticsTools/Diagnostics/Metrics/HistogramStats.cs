using System;
using System.Collections.Generic;
using System.Linq;

namespace Avalonia.Diagnostics.Metrics
{
    internal sealed class HistogramStats
    {
        private readonly Queue<TimedSample> _samples;
        private readonly int _capacity;

        public HistogramStats(string name, int capacity = 120)
        {
            Name = name;
            _capacity = capacity;
            _samples = new Queue<TimedSample>(capacity);
        }

        public string Name { get; }

        public double Minimum { get; private set; }

        public double Maximum { get; private set; }

        public double Average { get; private set; }

        public double Percentile95 { get; private set; }

        public IReadOnlyCollection<double> Snapshot => ToValuesArray(_samples);

        public IReadOnlyCollection<TimedSample> Timeline => _samples.ToArray();

        public void Add(double value)
        {
            Add(value, DateTimeOffset.UtcNow);
        }

        public void Add(double value, DateTimeOffset timestamp)
        {
            if (_samples.Count == _capacity)
            {
                _samples.Dequeue();
            }

            _samples.Enqueue(new TimedSample(timestamp, value));

            var ordered = _samples
                .Select(sample => sample.Value)
                .OrderBy(v => v)
                .ToArray();

            if (ordered.Length == 0)
            {
                Minimum = Maximum = Average = Percentile95 = 0;
                return;
            }

            Minimum = ordered.First();
            Maximum = ordered.Last();
            Average = ordered.Average();
            var percentileIndex = Clamp((int)Math.Ceiling(ordered.Length * 0.95) - 1, 0, ordered.Length - 1);
            Percentile95 = ordered[percentileIndex];
        }

        private static double[] ToValuesArray(IEnumerable<TimedSample> samples)
        {
            return samples.Select(sample => sample.Value).ToArray();
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            if (value > max)
            {
                return max;
            }

            return value;
        }
    }
}
