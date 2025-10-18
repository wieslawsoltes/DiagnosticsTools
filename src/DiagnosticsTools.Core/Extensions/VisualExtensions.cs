using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace Avalonia.Diagnostics;

/// <summary>
/// Provides utility helpers for working with visuals.
/// </summary>
public static class VisualExtensions
{
    /// <summary>
    /// Renders the specified control to the destination stream.
    /// </summary>
    /// <param name="source">Control to be rendered.</param>
    /// <param name="destination">Destination stream.</param>
    /// <param name="dpi">DPI resolution.</param>
    public static void RenderTo(this Control source, Stream destination, double dpi = 96)
    {
        var transform = source.CompositionVisual?.TryGetServerGlobalTransform();
        if (transform is null)
        {
            return;
        }

        var rect = new Rect(source.Bounds.Size).TransformToAABB(transform.Value);
        var top = rect.TopLeft;
        var pixelSize = new PixelSize((int)rect.Width, (int)rect.Height);
        var dpiVector = new Vector(dpi, dpi);

        var root = (source.VisualRoot ?? source.GetVisualRoot()) as Control ?? source;

        IDisposable? clipSetter = null;
        IDisposable? clipToBoundsSetter = null;
        IDisposable? renderTransformOriginSetter = null;
        IDisposable? renderTransformSetter = null;

        try
        {
            var clipRegion = new RectangleGeometry(rect);
            clipToBoundsSetter = root.SetValue(Visual.ClipToBoundsProperty, true, BindingPriority.Animation);
            clipSetter = root.SetValue(Visual.ClipProperty, clipRegion, BindingPriority.Animation);

            renderTransformOriginSetter = root.SetValue(
                Visual.RenderTransformOriginProperty,
                new RelativePoint(top, RelativeUnit.Absolute),
                BindingPriority.Animation);

            renderTransformSetter = root.SetValue(
                Visual.RenderTransformProperty,
                new TranslateTransform(-top.X, -top.Y),
                BindingPriority.Animation);

            using var bitmap = new RenderTargetBitmap(pixelSize, dpiVector);
            bitmap.Render(root);
            bitmap.Save(destination);
        }
        finally
        {
            renderTransformSetter?.Dispose();
            renderTransformOriginSetter?.Dispose();
            clipSetter?.Dispose();
            clipToBoundsSetter?.Dispose();
            source.InvalidateVisual();
        }
    }
}
