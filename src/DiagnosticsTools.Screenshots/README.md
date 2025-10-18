# DiagnosticsTools.Screenshots

Screenshot abstractions extracted from the DevTools application. Use them to plug your own storage/rendering into Avalonia tooling.

## Getting Started

```xml
<PackageReference Include="DiagnosticsTools.Screenshots" Version="*" />
```

```csharp
IScreenshotHandler handler = new FilePickerHandler();
await handler.Take(control);
```

## Key Types

- `IScreenshotHandler` – minimal contract for triggering a capture.
- `BaseRenderToStreamHandler` – helper base that renders a control into a `Stream`.
- `FilePickerHandler` – default implementation that prompts the user for a target path.

## Upgrade Notes

- Replace `Avalonia.Diagnostics.Screenshots.FilePickerHandler` references with this package.
- `BaseRenderToStreamHandler` relies on `VisualExtensions.RenderTo` from `DiagnosticsTools.Core`; ensure the core package is referenced when custom handlers are implemented.
- `DevToolsOptions.ScreenshotHandler` can now be configured without referencing the full DevTools assembly.
