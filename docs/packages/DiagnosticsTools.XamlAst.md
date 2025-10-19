# DiagnosticsTools.XamlAst

A lightweight XAML workspace that indexes documents, exposes diagnostics, and produces mutations for Avalonia projects. It underpins the DiagnosticsTools property editor and tree views.

## Installation

```bash
dotnet add package DiagnosticsTools.XamlAst
```

## Quick Start

```csharp
using DiagnosticsTools.XamlAst;

var provider = new XmlParserXamlAstProvider();
await using var workspace = new XamlAstWorkspace(provider, NullXamlAstInstrumentation.Instance);

var document = await workspace.GetDocumentAsync("Views/MainWindow.axaml");

foreach (var diagnostic in XamlDiagnosticMapper.CollectDiagnostics(document.Syntax))
{
    Console.WriteLine($"{diagnostic.Message} @ {diagnostic.LineNumber}");
}
```

## In-Depth Usage

### Workspace Lifetime & Instrumentation

- `XamlAstWorkspace` caches parsed documents and raises `DocumentChanged` events when the provider detects updates.
- Implement `IXamlAstInstrumentation` to trace parsing durations, node counts, or emit telemetry for editor features.
- Provide a custom `IXamlAstProvider` if you need to source documents from memory buffers or remote editors.

### Node Indexing & Diffing

- `XamlAstIndex` materialises a searchable index of XAML nodes. Use it to perform quick lookups (by `x:Name`, type, or position).
- `XamlAstNodeDiffer` compares two indices and emits `XamlAstNodeChange` entries (Added/Removed/Changed) for live diff visualisations.
- Combine the differ with [DiagnosticsTools.VirtualizedTreeView](./DiagnosticsTools.VirtualizedTreeView.md) to stream updates into a UI.

### Folding & Structure Views

- `XamlAstFoldingBuilder` calculates foldable regions. Feed its results into code editors to drive column folding or outline panes.
- `IXamlDocumentLocator` integrations allow the workspace to resolve includes and merged dictionaries beyond the primary file.

### Mutation Support

- `XamlTextEdit` and `XamlMutationEditBuilder` help construct minimal text edits for property changes or structural mutations.
- Use them alongside `DiagnosticsTools.PropertyEditing` to apply changes while keeping the workspace up to date.

## Key APIs

- `XamlAstWorkspace`
- `IXamlAstProvider`
- `XamlAstIndex`
- `XamlAstNodeDiffer`
- `XamlDiagnosticMapper`

## See Also

- [DiagnosticsTools.PropertyEditing](./DiagnosticsTools.PropertyEditing.md) – transports workspace mutations to disk.
- [DiagnosticsTools.SourceNavigation](./DiagnosticsTools.SourceNavigation.md) – maps resolved nodes back to source files for navigation.
