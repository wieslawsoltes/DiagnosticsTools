using Avalonia.Controls;
using Avalonia.Diagnostics;
using Avalonia.Input;
using Xunit;

using ColumnVisibilityBehavior = Avalonia.Diagnostics.Behaviors.ColumnDefinition;

namespace Input.Tests;

public class HotKeyConfigurationTests
{
    [Fact]
    public void Defaults_MatchExpectedGestures()
    {
        var config = new HotKeyConfiguration();

        Assert.Equal(Key.F8, config.ScreenshotSelectedControl.Key);
        Assert.Equal(KeyModifiers.Control, config.UndoMutation.KeyModifiers);
        Assert.Equal(KeyModifiers.Control | KeyModifiers.Shift, config.RedoMutation.KeyModifiers);
    }

    [Fact]
    public void ColumnVisibility_RestoresPreviousWidth()
    {
        var column = new Avalonia.Controls.ColumnDefinition { Width = new GridLength(120) };

        ColumnVisibilityBehavior.SetIsVisible(column, false);
        Assert.Equal(new GridLength(0, GridUnitType.Pixel), column.Width);

        ColumnVisibilityBehavior.SetIsVisible(column, true);
        Assert.Equal(new GridLength(120), column.Width);
    }
}
