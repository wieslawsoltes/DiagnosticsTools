using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Collections;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Avalonia.Diagnostics.Converters;

public sealed class SparklinePointsConverter : IValueConverter
{
    public static SparklinePointsConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var samples = ExtractSamples(value);
        if (samples is null || samples.Count == 0)
        {
            return new AvaloniaList<Point> { new Point(0, 0.5), new Point(1, 0.5) };
        }

        var min = samples.Min();
        var max = samples.Max();
        if (min.Equals(double.NaN) || max.Equals(double.NaN) || double.IsInfinity(min) || double.IsInfinity(max))
        {
            return new AvaloniaList<Point> { new Point(0, 0.5), new Point(1, 0.5) };
        }

        var range = max - min;
        if (range <= double.Epsilon)
        {
            range = min != 0 ? Math.Abs(min) : 1d;
        }

        var points = new AvaloniaList<Point>();
        var count = samples.Count;
        if (count == 1)
        {
            points.Add(new Point(0, Normalize(samples[0], min, range)));
            points.Add(new Point(1, Normalize(samples[0], min, range)));
            return points;
        }

        for (var index = 0; index < count; index++)
        {
            var x = count == 1 ? 0d : index / (double)(count - 1);
            var y = Normalize(samples[index], min, range);
            points.Add(new Point(x, y));
        }

        return points;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingNotification.UnsetValue;

    private static IReadOnlyList<double>? ExtractSamples(object? value) =>
        value switch
        {
            null => null,
            double[] array => array,
            IReadOnlyList<double> list => list,
            IEnumerable<double> enumerable => enumerable.ToArray(),
            _ => null
        };

    private static double Normalize(double value, double min, double range)
    {
        var normalized = (value - min) / range;
        normalized = Clamp(normalized, 0d, 1d);
        return 1d - normalized;
    }

    private static double Clamp(double value, double min, double max)
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

