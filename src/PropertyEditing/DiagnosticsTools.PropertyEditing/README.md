# DiagnosticsTools.PropertyEditing

Property editing infrastructure extracted from the DiagnosticsTools application.  
Provides mutation orchestration, journaling, and telemetry abstractions without depending on the DevTools shell.

## Getting Started

Reference the project or package and use the emitted contracts to drive XAML mutations:

```xml
<PackageReference Include="DiagnosticsTools.PropertyEditing" Version="*" />
```

```csharp
var dispatcher = new XamlMutationDispatcher(workspace, documentProvider);
var emitter = new PropertyInspectorChangeEmitter(dispatcher);
```

## Key Types

- `PropertyInspectorChangeEmitter` – composes mutation envelopes and raises completion events.
- `XamlMutationDispatcher` – applies serialized edits to XAML documents.
- `MutationTelemetry` / `MutationInstrumentation` – hooks for instrumentation sinks.

## Upgrade Notes

- All property editing APIs moved from `Avalonia.Diagnostics` to `Avalonia.Diagnostics.PropertyEditing`.
- DevTools now consumes these contracts via project reference; third parties can do the same.
- Replace calls to internal DevTools helpers with the exposed dispatcher/emitter combinations shown above.
