using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Avalonia.Diagnostics.Controls.VirtualizedTreeView;

internal sealed class IndentLevelToThicknessConverter : IValueConverter
{
    public double IndentSize { get; set; } = 16;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int level)
        {
            return new Thickness(level * IndentSize, 0, 0, 0);
        }

    return new Thickness(0);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return BindingOperations.DoNothing;
    }
}

internal sealed class BooleanToAngleConverter : IValueConverter
{
    public double TrueAngle { get; set; } = 90;

    public double FalseAngle { get; set; } = 0;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var angle = value is bool b && b ? TrueAngle : FalseAngle;
        return angle;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return BindingOperations.DoNothing;
    }
}

internal sealed class TreeViewItemIndentConverter : IMultiValueConverter
{
    public static readonly TreeViewItemIndentConverter Instance = new();

    public object? Convert(System.Collections.Generic.IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count == 2 && values[0] is int level && values[1] is double indentSize)
        {
            return new Thickness(level * indentSize, 0, 0, 0);
        }

        return new Thickness(0);
    }
}
