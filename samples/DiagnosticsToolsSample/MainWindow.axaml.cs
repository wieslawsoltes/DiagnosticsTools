using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Diagnostics;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DiagnosticsToolsSample;

public partial class MainWindow : Window
{
#if DEBUG
    private readonly bool _probeEnabled;
#endif

    public MainWindow()
    {
        InitializeComponent();

#if DEBUG
        _probeEnabled = string.Equals(
            Environment.GetEnvironmentVariable("DIAGNOSTICS_PROBE"),
            "1",
            StringComparison.Ordinal);

        if (_probeEnabled)
        {
            Opened += OnOpened;
        }
#endif
    }

#if DEBUG
    private void OnOpened(object? sender, EventArgs e)
    {
        if (!_probeEnabled)
        {
            return;
        }

        try
        {
            DumpValueStore("Window", this);

            if (Content is AvaloniaObject contentRoot)
            {
                DumpValueStore("Content", contentRoot);
            }

            var descendants = this.GetVisualDescendants()
                .OfType<StyledElement>()
                .Take(20)
                .ToList();

            for (var index = 0; index < descendants.Count; index++)
            {
                var visual = descendants[index];
                DumpValueStore($"Desc[{index:D2}] {visual.GetType().Name}", visual);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Diagnostics] Failed to capture value store diagnostics: {ex}");
        }
        finally
        {
            Opened -= OnOpened;
            Dispatcher.UIThread.Post(Close);
        }
    }

    private static void DumpValueStore(string label, AvaloniaObject avaloniaObject)
    {
        var diagnostic = avaloniaObject.GetValueStoreDiagnostic();
        Console.WriteLine($"[Diagnostics] {label} => {avaloniaObject.GetType().FullName}");

        var appliedFrames = diagnostic.GetType().GetProperty("AppliedFrames", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(diagnostic) as IEnumerable;
        if (appliedFrames is null)
        {
            Console.WriteLine("  (AppliedFrames metadata unavailable)");
            return;
        }

        foreach (var frame in appliedFrames)
        {
            var frameType = GetPropertyValue(frame, "Type");
            var priority = GetPropertyValue(frame, "Priority");
            var isActive = GetPropertyValue(frame, "IsActive");
            var source = GetPropertyValue(frame, "Source");
            var values = GetPropertyValue(frame, "Values") as IEnumerable;

            Console.WriteLine(
                $"  • FrameType={frameType}, Priority={priority}, Active={isActive}, Source={DescribeSource(source)}");

            if (values is null)
            {
                continue;
            }

            foreach (var entry in values)
            {
                var property = GetPropertyValue(entry, "Property");
                var propertyName = property is AvaloniaProperty avaloniaProperty ? avaloniaProperty.Name : property?.ToString();
                var value = GetPropertyValue(entry, "Value");
                Console.WriteLine($"     ↳ {propertyName} = {value}");
            }
        }
    }

    private static string DescribeSource(object? source)
    {
        if (source is null)
        {
            return "<null>";
        }

        if (source is Style style)
        {
            return $"Style[{style.Selector}]";
        }

        if (source is ControlTheme theme)
        {
            return $"ControlTheme[{theme.TargetType?.Name}]";
        }

        if (source is SetterBase setter)
        {
            return $"SetterSource[{setter}]";
        }

        if (source is object obj)
        {
            var type = obj.GetType();
            var document = GetSourceDocument(type);
            return document is null ? type.FullName ?? type.Name : $"{type.FullName} @ {document}";
        }

        return source.ToString() ?? "<unknown>";
    }

    private static string? GetSourceDocument(Type type)
    {
        var attrs = type.GetCustomAttributes(inherit: false);
        var uriAttribute = attrs.FirstOrDefault(a => a.GetType().Name == "XamlCompilationAttribute");
        return uriAttribute?.ToString();
    }

    private static object? GetPropertyValue(object? instance, string propertyName)
    {
        if (instance is null)
        {
            return null;
        }

        var type = instance.GetType();
        return type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance);
    }
#endif
}