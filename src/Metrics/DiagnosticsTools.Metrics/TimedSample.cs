using System;

namespace Avalonia.Diagnostics.Metrics
{
    public readonly struct TimedSample
    {
        public TimedSample(DateTimeOffset timestamp, double value)
        {
            Timestamp = timestamp;
            Value = value;
        }

        public DateTimeOffset Timestamp { get; }

        public double Value { get; }
    }
}
