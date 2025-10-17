using System.Globalization;
using Avalonia.Diagnostics.Converters;
using Avalonia.Diagnostics.Metrics;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Xunit;

namespace Metrics.Tests;

public class MetricBrushConverterTests
{
    [Fact]
    public void Convert_ReturnsCachedBrush()
    {
        var converter = new MetricBrushConverter { Kind = MetricBrushKind.Histogram, Alpha = 0.5 };

        var first = converter.Convert("renderer", typeof(IBrush), null, CultureInfo.InvariantCulture);
        var second = converter.Convert("renderer", typeof(IBrush), null, CultureInfo.InvariantCulture);

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void Convert_ClampsAlpha()
    {
        var converter = new MetricBrushConverter { Kind = MetricBrushKind.Gauge, Alpha = 2d };

        var brush = (IBrush?)converter.Convert("metric", typeof(IBrush), null, CultureInfo.InvariantCulture);

        Assert.NotNull(brush);
        var solid = Assert.IsType<ImmutableSolidColorBrush>(brush);
        Assert.Equal(255, solid.Color.A);
    }
}
