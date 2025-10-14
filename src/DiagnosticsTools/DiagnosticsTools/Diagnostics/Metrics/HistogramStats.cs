using System;
using System.Collections.Generic;
using System.Linq;

namespace Avalonia.Diagnostics.Metrics
{
    internal sealed class HistogramStats
    {
        private readonly Queue<double> _samples;
        private readonly int _capacity;

        public HistogramStats(string name, int capacity = 120)
        {
            Name = name;
            _capacity = capacity;
            _samples = new Queue<double>(capacity);
        }

        public string Name { get; }

        public double Minimum { get; private set; }

        public double Maximum { get; private set; }

        public double Average { get; private set; }

        public double Percentile95 { get; private set; }

        public IReadOnlyCollection<double> Snapshot => _samples.ToArray();

        public void Add(double value)
        {
            if (_samples.Count == _capacity)
            {
                _samples.Dequeue();
            }

            _samples.Enqueue(value);

            var ordered = _samples.OrderBy(v => v).ToArray();
            Minimum = ordered.FirstOrDefault();
            Maximum = ordered.LastOrDefault();
            Average = ordered.Average();
            var percentileIndex = Clamp((int)Math.Ceiling(ordered.Length * 0.95) - 1, 0, ordered.Length - 1);
            Percentile95 = ordered[percentileIndex];
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
