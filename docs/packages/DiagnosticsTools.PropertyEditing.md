# DiagnosticsTools.PropertyEditing

Mutation orchestration and telemetry infrastructure for live editing Avalonia XAML. The package powers the DiagnosticsTools property inspector and can be embedded in custom design surfaces or hot-reload workflows.

## Installation

```bash
dotnet add package DiagnosticsTools.PropertyEditing
```

## Quick Start

```csharp
using DiagnosticsTools.PropertyEditing;
using DiagnosticsTools.XamlAst;

// Create a workspace that understands your XAML sources.
var workspace = new XamlAstWorkspace(new XmlParserXamlAstProvider());

// Bridge XAML edits to the underlying document.
var dispatcher = new XamlMutationDispatcher(workspace, provider: null);

// Emit mutation envelopes from UI controls or tooling.
var emitter = new PropertyInspectorChangeEmitter(dispatcher)
{
    Telemetry = new MutationTelemetry()
};

emitter.MutationCompleted += (_, args) =>
{
    Console.WriteLine($"Applied {args.MutationId} to {args.Path}");
};
```

## In-Depth Usage

### Mutation Pipeline

1. Collect contextual information with `PropertyChangeContext` (control instance, property metadata, optional string representation).
2. Build mutation envelopes via `PropertyInspectorChangeEmitter.CreatePropertyChange(...)`.
3. Dispatch serialized edits through `XamlMutationDispatcher`, which applies safe diffs against the backing document.

The dispatcher automatically normalises whitespace and guards against conflicting edits through `XamlMutationJournal`.

### Journaling & Undo

`XamlMutationJournal` records the edits that transit through the dispatcher.  
You can replay or roll back changes by calling `journal.TryPop(out var entry)` and feeding the inverse mutation back to the dispatcher.

### Telemetry Hooks

Assign a custom implementation of `MutationTelemetry` to capture end-to-end timings, error conditions, or emit analytics events:

```csharp
emitter.Telemetry = new DelegatingMutationTelemetry(
    onDispatched: e => metrics.TrackDispatch(e.Path),
    onCompleted: e => metrics.TrackCompletion(e.Duration));
```

Telemetry runs on background tasks to avoid blocking the UI thread.

### External Document Integration

`ExternalDocumentChangedEventArgs` and `IXamlDocumentLocator` contracts let you connect the dispatcher to IDE buffers or remote storage.  
Raise `dispatcher.OnExternalDocumentChanged(...)` to keep the journal in sync when documents change outside the tooling surface.

## Key APIs

- `PropertyInspectorChangeEmitter`
- `XamlMutationDispatcher`
- `XamlMutationJournal`
- `MutationTelemetry`
- `MutationProvenance`

## See Also

- [DiagnosticsTools.XamlAst](./DiagnosticsTools.XamlAst.md) – supplies the workspace and AST services used by the dispatcher.
- [DiagnosticsTools.Runtime](./DiagnosticsTools.Runtime.md) – coordinate runtime undo/redo with property editing pipelines.
