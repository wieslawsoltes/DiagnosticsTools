using System.Globalization;
using System.IO;
using Avalonia.Diagnostics;
using Avalonia.Diagnostics.Converters;
using Avalonia.Controls;
using Avalonia.Media;
using Xunit;

namespace Core.Tests;

public class CoreUtilitiesTests
{
    [Fact]
    public void GetTypeName_ExpandsGenericArguments()
    {
        var name = typeof(System.Collections.Generic.Dictionary<string, int?>).GetTypeName();

        Assert.Equal("Dictionary<String,Int32?>", name);
    }

    [Fact]
    public void BoolToOpacityConverter_UsesConfiguredFallback()
    {
        var converter = new BoolToOpacityConverter { Opacity = 0.25 };

        var whenFalse = converter.Convert(false, typeof(double), null, CultureInfo.InvariantCulture);
        var whenTrue = converter.Convert(true, typeof(double), null, CultureInfo.InvariantCulture);

        Assert.Equal(0.25, whenFalse);
        Assert.Equal(1d, whenTrue);
    }

    [Fact]
    public void RenderTo_WithDetachedControl_DoesNotThrow()
    {
        var border = new Border
        {
            Width = 50,
            Height = 50,
            Background = Brushes.Blue
        };

        using var stream = new MemoryStream();

        border.RenderTo(stream);

        Assert.Equal(0, stream.Length);
    }
}
