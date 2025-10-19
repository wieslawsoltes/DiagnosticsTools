# DiagnosticsTools

Cross-platform diagnostics, inspection, and tooling for Avalonia UI applications.

[![CI](https://github.com/wieslawsoltes/DiagnosticsTools/actions/workflows/ci.yml/badge.svg)](https://github.com/wieslawsoltes/DiagnosticsTools/actions/workflows/ci.yml)
[![Release](https://github.com/wieslawsoltes/DiagnosticsTools/actions/workflows/release.yml/badge.svg)](https://github.com/wieslawsoltes/DiagnosticsTools/actions/workflows/release.yml)

## Overview

DiagnosticsTools originated from the `Avalonia.Diagnostics` module inside the [AvaloniaUI](https://github.com/AvaloniaUI/Avalonia) repository.  
The tooling has been extracted into a dedicated solution so that developers can compose the DevTools experience, or reuse the supporting libraries, without taking a hard dependency on the main Avalonia repo.

The solution ships a suite of NuGet packages targeting `netstandard2.0`, `net6.0`, and `net8.0`. Each package is self-contained and can be consumed independently.

## Package Catalog

| Package | Description | Latest |
| --- | --- | --- |
| DiagnosticsTools | Full DevTools experience with visual tree inspection, live property editing, and layout analyzers. | [![NuGet](https://img.shields.io/nuget/v/DiagnosticsTools.svg?style=flat&label=NuGet)](https://www.nuget.org/packages/DiagnosticsTools) |
| DiagnosticsTools.Core | Shared converters, value helpers, and rendering extensions consumed across the stack. | [![NuGet](https://img.shields.io/nuget/v/DiagnosticsTools.Core.svg?style=flat&label=NuGet)](https://www.nuget.org/packages/DiagnosticsTools.Core) |
| DiagnosticsTools.PropertyEditing | XAML mutation dispatcher, property inspector emitters, and telemetry hooks. | [![NuGet](https://img.shields.io/nuget/v/DiagnosticsTools.PropertyEditing.svg?style=flat&label=NuGet)](https://www.nuget.org/packages/DiagnosticsTools.PropertyEditing) |
| DiagnosticsTools.Runtime | Runtime mutation coordinator with undo/redo and tree abstractions. | [![NuGet](https://img.shields.io/nuget/v/DiagnosticsTools.Runtime.svg?style=flat&label=NuGet)](https://www.nuget.org/packages/DiagnosticsTools.Runtime) |
| DiagnosticsTools.Input | Hot-key registration, command routing helpers, and input gesture abstractions. | [![NuGet](https://img.shields.io/nuget/v/DiagnosticsTools.Input.svg?style=flat&label=NuGet)](https://www.nuget.org/packages/DiagnosticsTools.Input) |
| DiagnosticsTools.VirtualizedTreeView | High-performance virtualized tree view control and related helpers. | [![NuGet](https://img.shields.io/nuget/v/DiagnosticsTools.VirtualizedTreeView.svg?style=flat&label=NuGet)](https://www.nuget.org/packages/DiagnosticsTools.VirtualizedTreeView) |
| DiagnosticsTools.Screenshots | Screenshot capture helpers and default file picker integrations. | [![NuGet](https://img.shields.io/nuget/v/DiagnosticsTools.Screenshots.svg?style=flat&label=NuGet)](https://www.nuget.org/packages/DiagnosticsTools.Screenshots) |
| DiagnosticsTools.SourceNavigation | Portable PDB + SourceLink resolution and source info APIs. | [![NuGet](https://img.shields.io/nuget/v/DiagnosticsTools.SourceNavigation.svg?style=flat&label=NuGet)](https://www.nuget.org/packages/DiagnosticsTools.SourceNavigation) |
| DiagnosticsTools.XamlAst | XAML workspace, diagnostics, and mutation primitives. | [![NuGet](https://img.shields.io/nuget/v/DiagnosticsTools.XamlAst.svg?style=flat&label=NuGet)](https://www.nuget.org/packages/DiagnosticsTools.XamlAst) |

See the [per-package documentation](#documentation) for details, quick-start guides, and deep dives.

## Feature Highlights

- Visual tree inspection, live property editing, and layout analysis for Avalonia apps.
- Reusable runtime mutation, telemetry, and source navigation infrastructure.
- Production-ready virtualized tree view control capable of handling large hierarchies.
- Headless testing utilities for asserting diagnostics outside of a UI shell.
- Targets modern TFMs with deterministic builds, SourceLink, and symbol packages.

## Quick Start

Install the primary DiagnosticsTools package and attach the DevTools window to your application:

```bash
dotnet add package DiagnosticsTools
```

```csharp
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Diagnostics;
using Avalonia.Input;

public partial class App : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            desktop.MainWindow.AttachDevTools(new KeyGesture(Key.F12));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
```

Need deeper customization (property editing, source navigation, runtime undo/redo)?  
See the [package documentation](#documentation) for guides covering each helper library.

For a complete end-to-end example, explore the [DiagnosticsToolsSample](./samples/DiagnosticsToolsSample) application.

## Documentation

- [DiagnosticsTools](./docs/packages/DiagnosticsTools.md)
- [DiagnosticsTools.Core](./docs/packages/DiagnosticsTools.Core.md)
- [DiagnosticsTools.PropertyEditing](./docs/packages/DiagnosticsTools.PropertyEditing.md)
- [DiagnosticsTools.Runtime](./docs/packages/DiagnosticsTools.Runtime.md)
- [DiagnosticsTools.Input](./docs/packages/DiagnosticsTools.Input.md)
- [DiagnosticsTools.VirtualizedTreeView](./docs/packages/DiagnosticsTools.VirtualizedTreeView.md)
- [DiagnosticsTools.Screenshots](./docs/packages/DiagnosticsTools.Screenshots.md)
- [DiagnosticsTools.SourceNavigation](./docs/packages/DiagnosticsTools.SourceNavigation.md)
- [DiagnosticsTools.XamlAst](./docs/packages/DiagnosticsTools.XamlAst.md)

## Feature Comparison

| Feature | DiagnosticsTools | Avalonia built-in (`Avalonia.Diagnostics`) |
| --- | --- | --- |
| Distribution model | Standalone repository with modular NuGet packages (`DiagnosticsTools.*`), SourceLink, and symbol publishing | Integrated into the Avalonia source tree as a single assembly |
| Source navigation APIs | ✔ `DiagnosticsTools.SourceNavigation` exposes PortablePdbResolver, SourceLinkMap, and SourceNavigator | ✖ No public source navigation helpers |
| XAML AST workspace | ✔ `DiagnosticsTools.XamlAst` provides workspace, diffing, and folding services | ✖ Visual tree parsing remains internal to the DevTools window |
| Property editing SDK | ✔ `DiagnosticsTools.PropertyEditing` ships mutation dispatcher, telemetry hooks, and journal APIs | ✖ Property editing infrastructure is scoped to the built-in tooling |
| Runtime mutation utilities | ✔ `DiagnosticsTools.Runtime` offers undo/redo coordination and tree abstractions | ✖ Runtime mutation helpers are not exposed |
| Virtualized tree view control | ✔ `DiagnosticsTools.VirtualizedTreeView` publishes the DevTools tree as a reusable control | ✖ Virtualized tree remains bundled within the Avalonia DevTools UI |
| Documentation | ✔ Comprehensive guides for every package under `docs/packages` | ◑ Primarily inline XML comments within the Avalonia repo |
| Release automation | ✔ GitHub Actions CI and tag-triggered NuGet release pipeline | ◑ Ships as part of Avalonia’s core build and release process |

## Building & Testing

```bash
dotnet build DiagnosticsTools.sln
dotnet test DiagnosticsTools.sln
```

Continuous Integration runs on every push and pull request via [GitHub Actions](https://github.com/AvaloniaUI/DiagnosticsTools/actions).

## Release Management

Publishing is automated from a tag-based pipeline. Create a tag such as `v1.2.3` and push it to trigger packing and NuGet publication.

## Contributing

Issues and pull requests are welcome. Please discuss significant changes via an issue first and ensure that tests pass locally before submitting.

## License & Attribution

DiagnosticsTools is distributed under the [MIT License](./LICENSE.TXT). Portions of the codebase originate from the Avalonia Diagnostics tooling in the [AvaloniaUI/Avalonia](https://github.com/AvaloniaUI/Avalonia) repository.
