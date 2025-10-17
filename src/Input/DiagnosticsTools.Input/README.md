# DiagnosticsTools.Input

Hot key configuration helpers and behaviours used by DiagnosticsTools, now available without the DevTools application assembly.

## Getting Started

```xml
<PackageReference Include="DiagnosticsTools.Input" Version="*" />
```

```csharp
var options = new DevToolsOptions
{
    HotKeys = new HotKeyConfiguration
    {
        ScreenshotSelectedControl = new KeyGesture(Key.F9)
    }
};
```

## Key Types

- `HotKeyConfiguration` – centralises all gestures used by DiagnosticsTools.
- `KeyGestureExtensions` – matching helpers that respect keypad aliases.
- `Avalonia.Diagnostics.Behaviors.ColumnDefinition` – attached property for toggling grid column visibility.

## Upgrade Notes

- Replace old references to `Avalonia.Diagnostics.HotKeyConfiguration` inside the app project with this package.
- `KeyGestureExtensions` is now public and can be used by host applications when wiring raw key events.
- Behaviours were moved out of DevTools so XAML consumers reference `assembly=DiagnosticsTools.Input` instead of `DiagnosticsTools`.
