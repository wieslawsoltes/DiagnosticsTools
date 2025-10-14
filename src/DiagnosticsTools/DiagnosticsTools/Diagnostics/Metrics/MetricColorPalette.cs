using System;
using System.Collections.Generic;
using Avalonia.Media;

namespace Avalonia.Diagnostics.Metrics
{
    internal static class MetricColorPalette
    {
        private static readonly Dictionary<string, Color> s_histogramColors = new(StringComparer.OrdinalIgnoreCase)
        {
            { MetricIdentifiers.Histograms.CompositionRenderTime, Color.FromRgb(0x4C, 0xAF, 0x50) },
            { MetricIdentifiers.Histograms.CompositionUpdateTime, Color.FromRgb(0x8B, 0xC3, 0x4A) },
            { MetricIdentifiers.Histograms.UiMeasureTime, Color.FromRgb(0x21, 0x96, 0xF3) },
            { MetricIdentifiers.Histograms.UiArrangeTime, Color.FromRgb(0x03, 0xA9, 0xF4) },
            { MetricIdentifiers.Histograms.UiRenderTime, Color.FromRgb(0x9C, 0x27, 0xB0) },
            { MetricIdentifiers.Histograms.UiInputTime, Color.FromRgb(0xFF, 0xC1, 0x07) },
        };

        private static readonly Dictionary<string, Color> s_gaugeColors = new(StringComparer.OrdinalIgnoreCase)
        {
            { MetricIdentifiers.Gauges.UiEventHandlerCount, Color.FromRgb(0xFF, 0x57, 0x22) },
            { MetricIdentifiers.Gauges.UiVisualCount, Color.FromRgb(0x00, 0x96, 0x88) },
            { MetricIdentifiers.Gauges.UiDispatcherTimerCount, Color.FromRgb(0x79, 0x55, 0x48) },
        };

        private static readonly Color s_defaultHistogramColor = Color.FromRgb(0x56, 0x7D, 0xE0);
        private static readonly Color s_defaultGaugeColor = Color.FromRgb(0xFF, 0xA7, 0x26);

        public static Color GetHistogramColor(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return s_defaultHistogramColor;
            }

            if (s_histogramColors.TryGetValue(name, out var color))
            {
                return color;
            }

            return s_defaultHistogramColor;
        }

        public static Color GetGaugeColor(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return s_defaultGaugeColor;
            }

            if (s_gaugeColors.TryGetValue(name, out var color))
            {
                return color;
            }

            return s_defaultGaugeColor;
        }
    }
}
