using Avalonia.Controls;
using Avalonia.Diagnostics.Runtime;
using Avalonia.Media;
using Avalonia.Layout;
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

    [Fact]
    public void PointerGestureSession_CoalescesPropertyChanges()
    {
        var coordinator = new RuntimeMutationCoordinator();
        var border = new Border { Width = 100 };

        using (var gesture = coordinator.BeginPointerGesture())
        {
            var oldWidth = border.Width;
            border.Width = 120;
            coordinator.RegisterPropertyChange(border, Layoutable.WidthProperty, oldWidth, border.Width);

            oldWidth = border.Width;
            border.Width = 150;
            coordinator.RegisterPropertyChange(border, Layoutable.WidthProperty, oldWidth, border.Width);

            gesture.Complete();
        }

        Assert.True(coordinator.HasPendingMutations);

        coordinator.ApplyUndo();
        Assert.Equal(100, border.Width);

        coordinator.ApplyRedo();
        Assert.Equal(150, border.Width);
    }

    [Fact]
    public void PointerGestureSession_Cancel_DiscardsChanges()
    {
        var coordinator = new RuntimeMutationCoordinator();
        var border = new Border { Width = 100 };

        using (var gesture = coordinator.BeginPointerGesture())
        {
            var oldWidth = border.Width;
            border.Width = 140;
            coordinator.RegisterPropertyChange(border, Layoutable.WidthProperty, oldWidth, border.Width);

            gesture.Cancel();
        }

        Assert.False(coordinator.HasPendingMutations);
        Assert.Equal(140, border.Width);
    }
}
