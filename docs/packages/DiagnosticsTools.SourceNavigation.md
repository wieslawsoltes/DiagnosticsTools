# DiagnosticsTools.SourceNavigation

Portable PDB and SourceLink utilities that map runtime stack frames back to source files. Leverage it to implement “Go To Source” experiences or augment diagnostics with file/line metadata.

## Installation

```bash
dotnet add package DiagnosticsTools.SourceNavigation
```

## Quick Start

```csharp
using DiagnosticsTools.SourceNavigation;

var resolver = new SourceInfoResolver();
var info = await resolver.GetForMemberAsync(typeof(MyControl));

if (info?.LocalPath is { } path)
{
    await SourceNavigator.OpenDocumentAsync(path, info);
}
```

## In-Depth Usage

### Working with Portable PDBs

- `PortablePdbResolver` reads sequence points from embedded or on-disk PDBs.
- Provide a custom `IPdbLocator` if assemblies are loaded from remote locations or shadow copies.
- Use `PortablePdbResolver.GetSequencePointsAsync` to pre-cache locations for frequently accessed types.

### SourceLink Integration

- `SourceLinkMap` parses `*.sourcelink.json` documents and maps repository-relative paths to remote URIs.
- When `SourceInfo.LocalPath` is absent, fall back to `SourceInfo.RemoteUri` to open sources directly from GitHub or Azure DevOps.
- Cache resolved maps per assembly to avoid repeatedly hitting external endpoints.

### XAML Document Resolution

- Compose `XamlSourceResolver` with the `DiagnosticsTools.XamlAst` workspace to map generated code-behind frames back to `.axaml` documents.
- Implement `IXamlDocumentLocator` to plug in IDE buffers or watch files in real time.

### UI Integration

`SourceNavigator` exposes virtual methods for opening documents. Derive from it to integrate with your editor of choice:

```csharp
public sealed class RiderNavigator : SourceNavigator
{
    protected override Task OpenAsync(SourceInfo info) =>
        rider.OpenFileAsync(info.LocalPath!, info.LineNumber);
}
```

## Key APIs

- `SourceInfoResolver`
- `PortablePdbResolver`
- `SourceLinkMap`
- `SourceNavigator`
- `XamlSourceResolver`

## See Also

- [DiagnosticsTools.XamlAst](./DiagnosticsTools.XamlAst.md) – provide XAML AST services to complement source navigation.
- [DiagnosticsTools.Runtime](./DiagnosticsTools.Runtime.md) – enrich runtime mutations with source metadata for richer undo/redo UX.
