using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Avalonia.Diagnostics.Controls;

internal sealed class SnapGuideAdorner : Control
{
    private static readonly ImmutablePen VerticalPen = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0x1E, 0x90, 0xFF)), 1)
    {
        DashStyle = new DashStyle(new double[] { 4, 4 }, 0)
    }.ToImmutable();

    private static readonly ImmutablePen HorizontalPen = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0x1E, 0x90, 0xFF)), 1)
    {
        DashStyle = new DashStyle(new double[] { 4, 4 }, 0)
    }.ToImmutable();

    private IReadOnlyList<GuideSegment> _segments = Array.Empty<GuideSegment>();

    private SnapGuideAdorner()
    {
        IsHitTestVisible = false;
        AdornerLayer.SetIsClipEnabled(this, false);
        ZIndex = int.MaxValue - 1;
        IsVisible = false;
    }

    public static Handle? Add(Visual visual)
    {
        if (visual is null)
        {
            throw new ArgumentNullException(nameof(visual));
        }

        if (AdornerLayer.GetAdornerLayer(visual) is { } layer)
        {
            var adorner = new SnapGuideAdorner
            {
                [AdornerLayer.AdornedElementProperty] = visual
            };

            layer.Children.Add(adorner);

            return new Handle(layer, adorner);
        }

        return null;
    }

    public void UpdateGuides(IReadOnlyList<GuideSegment> segments)
    {
        _segments = segments ?? Array.Empty<GuideSegment>();
        IsVisible = _segments.Count > 0;
        InvalidateVisual();
    }

    public void Clear()
    {
        if (_segments.Count == 0 && !IsVisible)
        {
            return;
        }

        _segments = Array.Empty<GuideSegment>();
        IsVisible = false;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (_segments.Count == 0)
        {
            return;
        }

        foreach (var segment in _segments)
        {
            if (segment.IsVertical)
            {
                var start = new Point(segment.Position, segment.Start);
                var end = new Point(segment.Position, segment.End);
                context.DrawLine(VerticalPen, start, end);
            }
            else
            {
                var start = new Point(segment.Start, segment.Position);
                var end = new Point(segment.End, segment.Position);
                context.DrawLine(HorizontalPen, start, end);
            }
        }
    }

    internal readonly record struct GuideSegment(bool IsVertical, double Position, double Start, double End);

    internal sealed class Handle : IDisposable
    {
        private readonly AdornerLayer _layer;
        private readonly SnapGuideAdorner _adorner;

        public Handle(AdornerLayer layer, SnapGuideAdorner adorner)
        {
            _layer = layer ?? throw new ArgumentNullException(nameof(layer));
            _adorner = adorner ?? throw new ArgumentNullException(nameof(adorner));
        }

        public SnapGuideAdorner Adorner => _adorner;

        public void Dispose()
        {
            if (_layer.Children.Contains(_adorner))
            {
                _layer.Children.Remove(_adorner);
            }
        }
    }
}
