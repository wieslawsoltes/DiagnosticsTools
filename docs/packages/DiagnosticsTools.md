# DiagnosticsTools

The flagship package that bundles the Avalonia DevTools experience: visual tree inspection, live property editing, layout exploration, event monitoring, and screenshot tooling. Include it in your application to provide a comprehensive diagnostics overlay for developers and testers.

## Installation

```bash
dotnet add package DiagnosticsTools
```

Add the DevTools overlay during application startup with the provided extension methods:

```csharp
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Diagnostics;

public partial class App : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            desktop.MainWindow.AttachDevTools(); // F12 by default
        }

        base.OnFrameworkInitializationCompleted();
    }
}
```

## Quick Start

1. Attach DevTools in your `App.axaml.cs` bootstrap.
2. Launch your Avalonia app in `Debug` configuration.
3. Press `F12` (default) to toggle the DiagnosticsTools overlay.
4. Use the visual tree explorer to select controls, inspect properties, and preview layout bounds.

### Configuring Hotkeys

```csharp
using Avalonia.Input;

desktop.MainWindow.AttachDevTools(new KeyGesture(Key.F12, KeyModifiers.Control));
```

### Integrating Screenshots and Telemetry

- Connect [DiagnosticsTools.Screenshots](./DiagnosticsTools.Screenshots.md) handlers to automatically persist captures.
- Implement `MutationTelemetry` interfaces from [DiagnosticsTools.PropertyEditing](./DiagnosticsTools.PropertyEditing.md) to collect performance metrics when users edit properties.

## In-Depth Usage

### Application Embedding

- `Application.AttachDevTools()` wires the tooling into your application lifetime.  
  Call it in `OnFrameworkInitializationCompleted` after configuring the main window.
- Provide a custom launcher (menu item, command palette entry, etc.) that calls `DevTools.Attach(window, options)` if you want manual control.

### Runtime Customisation

- Tune `DevToolsOptions` (gesture, window sizing, startup screen, theme) to control the toolbox experience.
- Provide a custom `HotKeyConfiguration` through `DevToolsOptions.HotKeys` to align shortcuts with the rest of your app.
- Register an `ISourceInfoService` or `ISourceNavigator` to integrate SourceLink or IDE navigation.

### Production Considerations

- Gate the tooling behind compilation constants or configuration flags so it loads only in authorized builds.
- Combine with the release pipeline to ensure symbol packages are published, enabling SourceLink navigation within DevTools.

## Key APIs

- `DevTools`
- `DevToolsOptions`
- `DevToolsExtensions`
- `DevToolsWindow`

## See Also

- [DiagnosticsTools.Core](./DiagnosticsTools.Core.md) – shared converters used across the DevTools UI.
- [DiagnosticsTools.PropertyEditing](./DiagnosticsTools.PropertyEditing.md) – property inspector infrastructure.
- [DiagnosticsTools.VirtualizedTreeView](./DiagnosticsTools.VirtualizedTreeView.md) – tree view control backing the visual tree explorer.
