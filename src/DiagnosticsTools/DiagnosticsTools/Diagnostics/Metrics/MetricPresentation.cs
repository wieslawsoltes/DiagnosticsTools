using System;
using System.Collections.Generic;

namespace Avalonia.Diagnostics.Metrics
{
    internal static class MetricPresentation
    {
        private static readonly Dictionary<string, ThresholdPair> s_histogramThresholds = new(StringComparer.OrdinalIgnoreCase)
        {
            { "avalonia.comp.render.time", new ThresholdPair(16.0, 33.0) },
            { "avalonia.comp.update.time", new ThresholdPair(8.0, 16.0) },
            { "avalonia.ui.measure.time", new ThresholdPair(8.0, 16.0) },
            { "avalonia.ui.arrange.time", new ThresholdPair(8.0, 16.0) },
            { "avalonia.ui.render.time", new ThresholdPair(16.0, 33.0) },
            { "avalonia.ui.input.time", new ThresholdPair(4.0, 8.0) },
        };

        private static readonly Dictionary<string, ThresholdPair> s_gaugeThresholds = new(StringComparer.OrdinalIgnoreCase)
        {
            { "avalonia.ui.event.handler.count", new ThresholdPair(200.0, 400.0) },
            { "avalonia.ui.visual.count", new ThresholdPair(2000.0, 4000.0) },
            { "avalonia.ui.dispatcher.timer.count", new ThresholdPair(25.0, 50.0) },
        };

        public static ThresholdPair GetHistogramThresholds(string name)
        {
            if (s_histogramThresholds.TryGetValue(name, out var thresholds))
            {
                return thresholds;
            }

            return ThresholdPair.Empty;
        }

        public static ThresholdPair GetGaugeThresholds(string name)
        {
            if (s_gaugeThresholds.TryGetValue(name, out var thresholds))
            {
                return thresholds;
            }

            return ThresholdPair.Empty;
        }

        internal readonly struct ThresholdPair
        {
            public ThresholdPair(double? warning, double? critical)
            {
                Warning = warning;
                Critical = critical;
            }

            public double? Warning { get; }

            public double? Critical { get; }

            public static ThresholdPair Empty { get; } = new ThresholdPair(null, null);
        }
    }
}
