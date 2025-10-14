using System;
using System.Collections.Generic;

namespace Avalonia.Diagnostics.Metrics
{
    internal sealed class ObservableGaugeSnapshot
    {
        private readonly Queue<double> _history;
        private readonly int _capacity;

        public ObservableGaugeSnapshot(string name, int capacity = 60)
        {
            Name = name;
            _capacity = capacity;
            _history = new Queue<double>(capacity);
        }

        public string Name { get; }

        public double Current { get; private set; }

        public double Minimum { get; private set; }

        public double Maximum { get; private set; }

        public IReadOnlyCollection<double> History => _history.ToArray();

        public void Update(double value)
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

            _history.Enqueue(value);
        }
    }
}
