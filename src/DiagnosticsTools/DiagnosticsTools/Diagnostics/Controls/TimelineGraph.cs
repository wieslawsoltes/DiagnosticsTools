using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Avalonia.Diagnostics.Controls
{
    internal sealed class TimelineGraph : Control
    {
        private static readonly DashStyle s_defaultBaselineDash = new(new double[] { 2d, 2d }, 0d);

        public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
            AvaloniaProperty.Register<TimelineGraph, IReadOnlyList<double>?>(nameof(Values));

        public static readonly StyledProperty<IBrush?> StrokeProperty =
            AvaloniaProperty.Register<TimelineGraph, IBrush?>(nameof(Stroke));

        public static readonly StyledProperty<IBrush?> AreaFillProperty =
            AvaloniaProperty.Register<TimelineGraph, IBrush?>(nameof(AreaFill));

        public static readonly StyledProperty<double> StrokeThicknessProperty =
            AvaloniaProperty.Register<TimelineGraph, double>(nameof(StrokeThickness), 1.5);

        public static readonly StyledProperty<int> GridLineCountProperty =
            AvaloniaProperty.Register<TimelineGraph, int>(nameof(GridLineCount), 4);

        public static readonly StyledProperty<IBrush?> GridBrushProperty =
            AvaloniaProperty.Register<TimelineGraph, IBrush?>(nameof(GridBrush));

        public static readonly StyledProperty<IBrush?> BaselineBrushProperty =
            AvaloniaProperty.Register<TimelineGraph, IBrush?>(nameof(BaselineBrush));

        static TimelineGraph()
        {
            AffectsRender<TimelineGraph>(
                ValuesProperty,
                StrokeProperty,
                AreaFillProperty,
                StrokeThicknessProperty,
                GridLineCountProperty,
                GridBrushProperty,
                BaselineBrushProperty);
        }

        public IReadOnlyList<double>? Values
        {
            get => GetValue(ValuesProperty);
            set => SetValue(ValuesProperty, value);
        }

        public IBrush? Stroke
        {
            get => GetValue(StrokeProperty);
            set => SetValue(StrokeProperty, value);
        }

        public IBrush? AreaFill
        {
            get => GetValue(AreaFillProperty);
            set => SetValue(AreaFillProperty, value);
        }

        public double StrokeThickness
        {
            get => GetValue(StrokeThicknessProperty);
            set => SetValue(StrokeThicknessProperty, value);
        }

        public int GridLineCount
        {
            get => GetValue(GridLineCountProperty);
            set => SetValue(GridLineCountProperty, value);
        }

        public IBrush? GridBrush
        {
            get => GetValue(GridBrushProperty);
            set => SetValue(GridBrushProperty, value);
        }

        public IBrush? BaselineBrush
        {
            get => GetValue(BaselineBrushProperty);
            set => SetValue(BaselineBrushProperty, value);
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            var bounds = Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            DrawGridLines(context, bounds);

            var values = Values;
            if (values is null || values.Count == 0)
            {
                DrawBaseline(context, bounds);
                return;
            }

            var pointArray = BuildPoints(bounds, values);
            if (pointArray.Length == 0)
            {
                DrawBaseline(context, bounds);
                return;
            }

            DrawArea(context, bounds, pointArray);
            DrawLine(context, pointArray);
            DrawBaseline(context, bounds);
        }

        private void DrawGridLines(DrawingContext context, Rect bounds)
        {
            var brush = GridBrush;
            var lineCount = GridLineCount;

            if (brush is null || lineCount <= 0)
            {
                return;
            }

            var pen = new Pen(brush, 1d);
            var step = bounds.Height / lineCount;

            for (var i = 1; i < lineCount; i++)
            {
                var y = bounds.Top + i * step;
                context.DrawLine(pen, new Point(bounds.Left, y), new Point(bounds.Right, y));
            }
        }

        private static Point[] BuildPoints(Rect bounds, IReadOnlyList<double> values)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return Array.Empty<Point>();
            }

            var min = values.Min();
            var max = values.Max();
            if (double.IsNaN(min) || double.IsNaN(max) || double.IsInfinity(min) || double.IsInfinity(max))
            {
                return Array.Empty<Point>();
            }

            var range = max - min;
            if (range <= double.Epsilon)
            {
                range = Math.Abs(min) > double.Epsilon ? Math.Abs(min) : 1d;
            }

            var count = values.Count;
            if (count == 0)
            {
                return Array.Empty<Point>();
            }

            var width = bounds.Width;
            var height = bounds.Height;
            var points = new Point[count];

            for (var i = 0; i < count; i++)
            {
                var value = values[i];
                if (double.IsNaN(value) || double.IsInfinity(value))
                {
                    value = min;
                }

                var x = count == 1 ? bounds.Left : bounds.Left + (i / (double)(count - 1)) * width;
                var normalized = (value - min) / range;
                normalized = Clamp(normalized, 0d, 1d);
                var y = bounds.Bottom - normalized * height;
                points[i] = new Point(x, y);
            }

            return points;
        }

        private void DrawArea(DrawingContext context, Rect bounds, IReadOnlyList<Point> points)
        {
            if (points.Count == 0)
            {
                return;
            }

            var fill = AreaFill;
            if (fill is null)
            {
                return;
            }

            var geometry = new StreamGeometry();
            using (var geometryContext = geometry.Open())
            {
                var first = points[0];
                var last = points[points.Count - 1];

                geometryContext.BeginFigure(new Point(first.X, bounds.Bottom), true);
                for (var i = 0; i < points.Count; i++)
                {
                    geometryContext.LineTo(points[i]);
                }
                geometryContext.LineTo(new Point(last.X, bounds.Bottom));
                geometryContext.EndFigure(true);
            }

            context.DrawGeometry(fill, null, geometry);
        }

        private void DrawLine(DrawingContext context, IReadOnlyList<Point> points)
        {
            if (points.Count == 0)
            {
                return;
            }

            var stroke = Stroke;
            if (stroke is null)
            {
                return;
            }

            var geometry = new StreamGeometry();
            using (var geometryContext = geometry.Open())
            {
                geometryContext.BeginFigure(points[0], false);
                for (var i = 1; i < points.Count; i++)
                {
                    geometryContext.LineTo(points[i]);
                }
                geometryContext.EndFigure(false);
            }

            var pen = new Pen(stroke, StrokeThickness);
            context.DrawGeometry(null, pen, geometry);
        }

        private void DrawBaseline(DrawingContext context, Rect bounds)
        {
            var brush = BaselineBrush ?? GridBrush;
            if (brush is null)
            {
                return;
            }

            var y = bounds.Bottom;
            var pen = new Pen(brush, 1d, s_defaultBaselineDash);
            context.DrawLine(pen, new Point(bounds.Left, y), new Point(bounds.Right, y));
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min)
            {
                return min;
            }

            if (value > max)
            {
                return max;
            }

            return value;
        }
    }
}
