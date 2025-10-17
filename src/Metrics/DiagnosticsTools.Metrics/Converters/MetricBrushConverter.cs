using System;
using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Diagnostics.Metrics;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Avalonia.Diagnostics.Converters;

public sealed class MetricBrushConverter : IValueConverter
{
    private readonly ConcurrentDictionary<(string Name, double Alpha), IBrush> _cache = new();

    public MetricBrushKind Kind { get; set; }

    public double Alpha { get; set; } = 1d;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string name)
        {
            return BindingOperations.DoNothing;
        }

        var key = (name, Alpha);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var color = Kind switch
        {
            MetricBrushKind.Gauge => MetricColorPalette.GetGaugeColor(name),
            _ => MetricColorPalette.GetHistogramColor(name)
        };

        var alpha = Alpha;
        if (alpha < 0d)
        {
            alpha = 0d;
        }
        else if (alpha > 1d)
        {
            alpha = 1d;
        }

        if (alpha < 1d)
        {
            color = Color.FromArgb((byte)Math.Round(255d * alpha), color.R, color.G, color.B);
        }

        var brush = new ImmutableSolidColorBrush(color);
        _cache[key] = brush;
        return brush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingNotification.UnsetValue;
}

public enum MetricBrushKind
{
    Histogram,
    Gauge
}

