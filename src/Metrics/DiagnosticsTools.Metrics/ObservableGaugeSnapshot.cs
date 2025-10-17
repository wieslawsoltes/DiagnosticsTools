using System;
using System.Collections.Generic;
using System.Linq;

namespace Avalonia.Diagnostics.Metrics
{
    public sealed class ObservableGaugeSnapshot
    {
        private readonly Queue<TimedSample> _history;
        private readonly int _capacity;

        public ObservableGaugeSnapshot(string name, int capacity = 60)
        {
            Name = name;
            _capacity = capacity;
            _history = new Queue<TimedSample>(capacity);
        }

        public string Name { get; }

        public double Current { get; private set; }

        public double Minimum { get; private set; }

        public double Maximum { get; private set; }

        public IReadOnlyCollection<double> History => ToValuesArray(_history);

        public IReadOnlyCollection<TimedSample> Timeline => _history.ToArray();

        public void Update(double value)
        {
            Update(value, DateTimeOffset.UtcNow);
        }

        public void Update(double value, DateTimeOffset timestamp)
        {
            Current = value;

            if (_history.Count == 0)
            {
                Minimum = Maximum = value;
            }
            else
            {
                Minimum = Math.Min(Minimum, value);
                Maximum = Math.Max(Maximum, value);
            }

            if (_history.Count == _capacity)
            {
                _history.Dequeue();
            }

            _history.Enqueue(new TimedSample(timestamp, value));
        }

        private static double[] ToValuesArray(IEnumerable<TimedSample> samples)
        {
            return samples.Select(sample => sample.Value).ToArray();
        }
    }
}
