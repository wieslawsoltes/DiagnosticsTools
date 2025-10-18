# DiagnosticsTools

A standalone diagnostic tools library for Avalonia UI applications, extracted from the core Avalonia repository.

## About

This project is based on the original `Avalonia.Diagnostics` code from the [AvaloniaUI repository](https://github.com/AvaloniaUI/Avalonia). It has been extracted into a standalone project that uses Avalonia NuGet packages instead of project references, making it easier to develop and maintain diagnostic tools independently.

## Original Source

The original code can be found in the main Avalonia repository:
- **Repository**: https://github.com/AvaloniaUI/Avalonia
- **Original Location**: `src/Avalonia.Diagnostics`

## License

This project is distributed under the MIT License. See [LICENSE.TXT](./LICENSE.TXT) for details.

Portions of the code were ported from the Avalonia Diagnostics tooling in the AvaloniaUI/Avalonia repository.

## Features

- DevTools window with visual tree inspection
- Property editor for runtime UI manipulation
- Layout explorer with visual guidelines
- Event monitoring and debugging
- Hot key configuration
- Style and resource inspection

## Reusable Libraries

DiagnosticsTools now ships a suite of standalone libraries that can be reused outside the DevTools application. Most packages target `netstandard2.0`, `net6.0`, and `net8.0`.

- [`DiagnosticsTools.Core`](./src/Core/DiagnosticsTools.Core/README.md) – Shared converters, visual helpers, and utility extensions consumed across the tooling stack.
- [`DiagnosticsTools.PropertyEditing`](./src/PropertyEditing/DiagnosticsTools.PropertyEditing/README.md) – XAML mutation orchestration, telemetry hooks, and guard helpers used by the property inspector (`net6.0`/`net8.0`).
- [`DiagnosticsTools.Runtime`](./src/Runtime/DiagnosticsTools.Runtime/README.md) – Runtime undo/redo coordinator and tree abstractions for hot reload scenarios.
- [`DiagnosticsTools.Input`](./src/Input/DiagnosticsTools.Input/README.md) – Reusable hot key configuration and behaviours.
- [`DiagnosticsTools.Screenshots`](./src/Screenshots/DiagnosticsTools.Screenshots/README.md) – Screenshot handler interfaces and default file picker implementation.
- [`DiagnosticsTools.SourceNavigation`](./src/SourceNavigation/DiagnosticsTools.SourceNavigation/README.md) – Portable PDB + SourceLink resolution with high-level helpers such as `SourceInfoResolver` and `XamlSourceResolver`.
- [`DiagnosticsTools.XamlAst`](./src/XamlAst/DiagnosticsTools.XamlAst/README.md) – A lightweight XAML workspace that indexes documents, raises change notifications, and surfaces diagnostic information.

### Referencing from your project

```bash
dotnet add package DiagnosticsTools.SourceNavigation
dotnet add package DiagnosticsTools.XamlAst
```

If you prefer to reference local projects instead (for example when contributing to DiagnosticsTools), add project references to `src/SourceNavigation/DiagnosticsTools.SourceNavigation.csproj` and `src/XamlAst/DiagnosticsTools.XamlAst.csproj` in your application.

```csharp
using Avalonia.Diagnostics.SourceNavigation;
using Avalonia.Diagnostics.Xaml;

var resolver = new SourceInfoResolver();
var sourceInfo = await resolver.GetForMemberAsync(typeof(MyControl));

using var workspace = new XamlAstWorkspace();
var document = await workspace.GetDocumentAsync("Views/MyControl.axaml");
```

For a full integration example, see the [sample project](./samples/DiagnosticsToolsSample).

## Usage

See the [sample project](./samples/DiagnosticsToolsSample) for an example of how to integrate DiagnosticsTools into your Avalonia application.

## Building

```bash
dotnet build DiagnosticsTools.sln
```

## Attribution

This project contains code originally authored by the AvaloniaUI team and contributors. We are grateful for their work in creating these powerful diagnostic tools for the Avalonia community.

- Credit to [BAndysc](https://github.com/BAndysc) for the VirtualizedTreeView work contributed in [AvaloniaUI/Avalonia#14417](https://github.com/AvaloniaUI/Avalonia/pull/14417).
