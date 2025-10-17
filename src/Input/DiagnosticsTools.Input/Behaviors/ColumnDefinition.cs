using Avalonia;
using Avalonia.Controls;

namespace Avalonia.Diagnostics.Behaviors;

/// <summary>
/// Allows toggling column definition visibility without losing the configured width.
/// </summary>
public static class ColumnDefinition
{
    private static readonly GridLength s_zeroWidth = new GridLength(0, GridUnitType.Pixel);

    private static readonly AttachedProperty<GridLength?> s_lastWidthProperty =
        AvaloniaProperty.RegisterAttached<Avalonia.Controls.ColumnDefinition, GridLength?>(
            "LastWidth",
            typeof(ColumnDefinition));

    /// <summary>
    /// Attached property that controls visibility of a column definition.
    /// </summary>
    public static readonly AttachedProperty<bool> IsVisibleProperty =
        AvaloniaProperty.RegisterAttached<Avalonia.Controls.ColumnDefinition, bool>(
            "IsVisible",
            typeof(ColumnDefinition),
            defaultValue: true,
            coerce: (column, visibility) =>
            {
                var lastWidth = column.GetValue(s_lastWidthProperty);
                if (visibility && lastWidth is { })
                {
                    column.SetValue(Avalonia.Controls.ColumnDefinition.WidthProperty, lastWidth);
                }
                else if (!visibility)
                {
                    column.SetValue(s_lastWidthProperty, column.GetValue(Avalonia.Controls.ColumnDefinition.WidthProperty));
                    column.SetValue(Avalonia.Controls.ColumnDefinition.WidthProperty, s_zeroWidth);
                }

                return visibility;
            });

    public static bool GetIsVisible(Avalonia.Controls.ColumnDefinition columnDefinition) =>
        columnDefinition.GetValue(IsVisibleProperty);

    public static void SetIsVisible(Avalonia.Controls.ColumnDefinition columnDefinition, bool visibility) =>
        columnDefinition.SetValue(IsVisibleProperty, visibility);
}

