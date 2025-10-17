# DiagnosticsTools.Metrics

Metrics and telemetry primitives used by DiagnosticsTools. Consume them directly to build dashboards or to instrument other tooling features.

## Getting Started

```xml
<PackageReference Include="DiagnosticsTools.Metrics" Version="*" />
```

```csharp
var listener = new MetricsListenerService();
listener.Start();

listener.MetricsUpdated += (_, __) =>
{
    var snapshot = listener.CurrentHistograms;
    // render snapshot in your UI
};
```

## Key Types

- `MetricsListenerService` – aggregates histogram/gauge/activity streams.
- `MetricsSnapshotService` – exports a serialised snapshot for sharing.
- `MetricColorPalette` & `MetricPresentation` – helpers for consistent theming.

## Upgrade Notes

- The diagnostics UI now references this package instead of internal classes in `Avalonia.Diagnostics`.
- Consumers who previously accessed metrics through `MainViewModel` should reference this package and use the listener/snapshot services directly.
- XAML converters moved alongside the package under the `Avalonia.Diagnostics.Converters` namespace (see `DiagnosticsTools.Metrics` assembly).
