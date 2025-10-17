using Avalonia.Controls;
using Avalonia.Diagnostics.Runtime;
using Avalonia.Media;
using Xunit;

namespace Runtime.Tests;

public class RuntimeMutationCoordinatorTests
{
    [Fact]
    public void RegisterPropertyChange_TracksUndoableMutation()
    {
        var coordinator = new RuntimeMutationCoordinator();
        var border = new Border();

        coordinator.RegisterPropertyChange(border, Border.BackgroundProperty, Brushes.Transparent, Brushes.Red);

        Assert.True(coordinator.HasPendingMutations);
    }

    [Fact]
    public void Clear_RemovesTrackedMutations()
    {
        var coordinator = new RuntimeMutationCoordinator();
        var border = new Border();

        coordinator.RegisterPropertyChange(border, Border.BackgroundProperty, Brushes.Transparent, Brushes.Red);
        coordinator.Clear();

        Assert.False(coordinator.HasPendingMutations);
    }

    [Fact]
    public void ApplyUndoRedo_RestoresPropertyValues()
    {
        var coordinator = new RuntimeMutationCoordinator();
        var border = new Border { Background = Brushes.Red };

        border.Background = Brushes.Blue;

        coordinator.RegisterPropertyChange(border, Border.BackgroundProperty, Brushes.Red, Brushes.Blue);

        coordinator.ApplyUndo();
        Assert.Equal(Brushes.Red, border.Background);

        coordinator.ApplyRedo();
        Assert.Equal(Brushes.Blue, border.Background);
    }
}
