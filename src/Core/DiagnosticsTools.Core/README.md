# DiagnosticsTools.Core

Shared utility layer for DiagnosticsTools and related packages.  
Contains common converters, extensions, and helper APIs that were previously internal to the DevTools assembly.

## Getting Started

```xml
<PackageReference Include="DiagnosticsTools.Core" Version="*" />
```

```csharp
var typeName = typeof(Dictionary<string, int?>).GetTypeName();
var converter = new BoolToOpacityConverter { Opacity = 0.4 };
```

## Key Features

- Converters used across DevTools (image, opacity, sparkline, metric brushes, etc.).
- `TypeExtensions.GetTypeName` for human-friendly type formatting.
- `VisualExtensions.RenderTo` helper for capturing visuals into streams.

## Upgrade Notes

- Update XAML namespaces from `assembly=DiagnosticsTools` to `assembly=DiagnosticsTools.Core` for converter resources.
- `VisualExtensions.RenderTo` now ships here; screenshot handlers should reference this package explicitly.
- This assembly has no UI shell dependencies and can be reused by other tooling projects.
