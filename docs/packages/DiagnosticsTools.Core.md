# DiagnosticsTools.Core

Shared utility layer that powers the DiagnosticsTools ecosystem. It bundles converters, value helpers, and rendering extensions that can be reused in any Avalonia application.

## Installation

```bash
dotnet add package DiagnosticsTools.Core
```

Add the assembly namespace to your XAML resources:

```xml
xmlns:dt="clr-namespace:Avalonia.Diagnostics.Converters;assembly=DiagnosticsTools.Core"
```

## Quick Start

```xml
<Window.Resources>
  <dt:BoolToOpacityConverter x:Key="HiddenToOpacity" Opacity="0.25" />
</Window.Resources>

<Border Opacity="{Binding IsHidden, Converter={StaticResource HiddenToOpacity}}" />
```

Capture any visual into a stream:

```csharp
using DiagnosticsTools.Core.Extensions;

await using var stream = File.Create("preview.png");
await myControl.RenderToPngAsync(stream, scaling: 2);
```

## In-Depth Usage

### Converter Catalog

- `BoolToOpacityConverter`, `BoolToVisibilityConverter`, and `BoolToThicknessConverter` simplify toggling visuals from bindings.
- `BrushToBitmapConverter` and `ImageBrushToBitmapConverter` expose paint data to value converters for previews.
- `CompositeConverter` lets you chain primitive converters in XAML without writing code-behind.

Declare converters globally in `App.axaml` and reuse them across multiple view hierarchies.

### Visual Capture Utilities

`VisualExtensions.RenderTo` and friends render any `Visual` into bitmap streams or `RenderTargetBitmap` instances. You can:

1. Render controls for screenshot generation.
2. Pipe the result through `Base64BitmapConverter` to surface thumbnails inside diagnostics panes.
3. Apply the helpers to non-interactive visuals such as `DrawingPresenter`.

### Type and Reflection Helpers

- `TypeExtensions.GetTypeName()` formats CLR types with nullable annotations and generic arguments.
- `TypeExtensions.IsAssignableToGeneric()` enables matching open generic services when walking the visual tree.
- `DispatcherExtensions.InvokeAsync()` exposes ergonomic async dispatcher helpers compatible with Avalonia threading primitives.

## Key APIs

- `Avalonia.Diagnostics.Converters.BoolToOpacityConverter`
- `Avalonia.Diagnostics.Extensions.VisualExtensions`
- `Avalonia.Diagnostics.Extensions.DispatcherExtensions`
- `Avalonia.Diagnostics.Extensions.TypeExtensions`

## See Also

- [DiagnosticsTools.PropertyEditing](./DiagnosticsTools.PropertyEditing.md) – consumes converters for property inspectors.
- [DiagnosticsTools.Screenshots](./DiagnosticsTools.Screenshots.md) – uses the rendering helpers for capture scenarios.
