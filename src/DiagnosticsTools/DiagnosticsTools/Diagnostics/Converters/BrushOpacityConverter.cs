using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Avalonia.Diagnostics.Converters
{
    internal sealed class BrushOpacityConverter : IValueConverter
    {
        public double Opacity { get; set; } = 0.25;

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is ISolidColorBrush solid)
            {
                var alpha = ClampOpacity(Opacity);
                var color = Color.FromArgb((byte)Math.Round(alpha * 255d), solid.Color.R, solid.Color.G, solid.Color.B);
                return new ImmutableSolidColorBrush(color);
            }

            if (value is Color colorValue)
            {
                var alpha = ClampOpacity(Opacity);
                var color = Color.FromArgb((byte)Math.Round(alpha * 255d), colorValue.R, colorValue.G, colorValue.B);
                return new ImmutableSolidColorBrush(color);
            }

            return BindingNotification.UnsetValue;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return BindingNotification.UnsetValue;
        }

        private static double ClampOpacity(double value)
        {
            if (value < 0d)
            {
                return 0d;
            }

            if (value > 1d)
            {
                return 1d;
            }

            return value;
        }
    }
}
