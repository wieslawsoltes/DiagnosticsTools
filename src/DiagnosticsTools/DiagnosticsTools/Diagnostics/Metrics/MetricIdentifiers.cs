namespace Avalonia.Diagnostics.Metrics
{
    internal static class MetricIdentifiers
    {
        public const string MeterPrefix = "Avalonia";

        public static class Histograms
        {
            public const string CompositionRenderTime = "avalonia.comp.render.time";
            public const string CompositionUpdateTime = "avalonia.comp.update.time";
            public const string UiMeasureTime = "avalonia.ui.measure.time";
            public const string UiArrangeTime = "avalonia.ui.arrange.time";
            public const string UiRenderTime = "avalonia.ui.render.time";
            public const string UiInputTime = "avalonia.ui.input.time";
        }

        public static class Gauges
        {
            public const string UiEventHandlerCount = "avalonia.ui.event.handler.count";
            public const string UiVisualCount = "avalonia.ui.visual.count";
            public const string UiDispatcherTimerCount = "avalonia.ui.dispatcher.timer.count";
        }

        public static class Activities
        {
            public const string AttachingStyle = "Avalonia.AttachingStyle";
            public const string FindingResource = "Avalonia.FindingResource";
            public const string EvaluatingStyle = "Avalonia.EvaluatingStyle";
            public const string MeasuringLayoutable = "Avalonia.MeasuringLayoutable";
            public const string ArrangingLayoutable = "Avalonia.ArrangingLayoutable";
            public const string PerformingHitTest = "Avalonia.PerformingHitTest";
            public const string RaisingRoutedEvent = "Avalonia.RaisingRoutedEvent";
        }
    }
}
