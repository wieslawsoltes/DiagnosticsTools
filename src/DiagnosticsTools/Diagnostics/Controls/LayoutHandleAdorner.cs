using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Reactive;
using Avalonia.Utilities;

namespace Avalonia.Diagnostics.Controls;

internal sealed class LayoutHandleAdorner : Control
{
    private static readonly ImmutablePen BorderPen = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0x1E, 0x90, 0xFF)), 1).ToImmutable();
    private static readonly ImmutablePen HandlePen = new Pen(new SolidColorBrush(Color.FromArgb(0xFF, 0x1E, 0x90, 0xFF)), 1).ToImmutable();
    private static readonly IBrush HandleFill = new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)).ToImmutable();

    private readonly ILayoutHandleBehavior _behavior;
    private readonly double _handleSize;

    private LayoutHandleAdorner(ILayoutHandleBehavior behavior, double handleSize)
    {
        _behavior = behavior ?? throw new ArgumentNullException(nameof(behavior));
        _handleSize = handleSize;
        Focusable = false;
        IsHitTestVisible = true;
        Cursor = new Cursor(StandardCursorType.SizeAll);
        ClipToBounds = false;
        AdornerLayer.SetIsClipEnabled(this, false);
        ZIndex = int.MaxValue;
    }

    public static IDisposable? Add(Visual visual, ILayoutHandleBehavior behavior, double handleSize = 6)
    {
        if (visual is null)
        {
            throw new ArgumentNullException(nameof(visual));
        }

        if (behavior is null)
        {
            throw new ArgumentNullException(nameof(behavior));
        }

        if (AdornerLayer.GetAdornerLayer(visual) is { } layer)
        {
            var adorner = new LayoutHandleAdorner(behavior, handleSize)
            {
                [AdornerLayer.AdornedElementProperty] = visual
            };

            layer.Children.Add(adorner);

            return Disposable.Create((layer, adorner), static state =>
            {
                state.layer.Children.Remove(state.adorner);
            });
        }

        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _behavior.OnPointerPressed(this, e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _behavior.OnPointerMoved(this, e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _behavior.OnPointerReleased(this, e);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _behavior.OnPointerCaptureLost(this, e);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = new Rect(Bounds.Size).Deflate(0.5);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        context.DrawRectangle(null, BorderPen, bounds);

        foreach (var rect in _behavior.GetHandleRects(bounds, _handleSize))
        {
            context.DrawRectangle(HandleFill, HandlePen, rect);
        }
    }

    internal interface ILayoutHandleBehavior
    {
        IEnumerable<Rect> GetHandleRects(Rect bounds, double handleSize);
        void OnPointerPressed(LayoutHandleAdorner sender, PointerPressedEventArgs e);
        void OnPointerMoved(LayoutHandleAdorner sender, PointerEventArgs e);
        void OnPointerReleased(LayoutHandleAdorner sender, PointerReleasedEventArgs e);
        void OnPointerCaptureLost(LayoutHandleAdorner sender, PointerCaptureLostEventArgs e);
    }
}
