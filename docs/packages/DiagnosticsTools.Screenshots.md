# DiagnosticsTools.Screenshots

Screenshot capture abstractions used by DiagnosticsTools. The package ships pluggable handlers so you can snapshot Avalonia visuals and persist them using your own storage or UI.

## Installation

```bash
dotnet add package DiagnosticsTools.Screenshots
```

## Quick Start

```csharp
using DiagnosticsTools.Screenshots;

IScreenshotHandler handler = new FilePickerHandler();

await handler.CaptureAsync(
    target: devTools.SelectedControl,
    context: new ScreenshotContext { SuggestedFileName = "Control.png" },
    cancellationToken: CancellationToken.None);
```

Swap in a custom renderer to keep screenshots in memory:

```csharp
var handler = new BaseRenderToStreamHandler(
    renderAsync: async (visual, stream, ct) =>
    {
        await visual.RenderToPngAsync(stream, scaling: 2);
        stream.Position = 0;
        // Upload to your telemetry service here.
    });
```

## In-Depth Usage

### Handler Pipeline

- `IScreenshotHandler.CaptureAsync` receives the current `Visual` and a `ScreenshotContext`.
- Use `ScreenshotContext` to surface additional metadata (file names, format hints, timestamps).
- `BaseRenderToStreamHandler` centralises the capture logic; override the delegates to customise storage.

### Integrating File Pickers

`FilePickerHandler` prompts the user for a location using Avalonia's storage provider abstractions.  
Inject a mock storage provider when running automated tests to verify that capture flows execute.

### Extending Capture Scenarios

- Compose handlers that fall back to different strategies (e.g., clipboard copy, disk persistence, HTTP upload).
- Chain the rendering helpers from [DiagnosticsTools.Core](./DiagnosticsTools.Core.md) to apply additional post-processing before the image is finalised.

## Key APIs

- `IScreenshotHandler`
- `BaseRenderToStreamHandler`
- `FilePickerHandler`
- `ScreenshotContext`

## See Also

- [DiagnosticsTools.Core](./DiagnosticsTools.Core.md) – exposes the rendering extensions used by the handlers.
- [DiagnosticsTools.Input](./DiagnosticsTools.Input.md) – wire screenshot shortcuts through the shared hot-key configuration.
