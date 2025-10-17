# DiagnosticsTools.Runtime

Runtime mutation helpers extracted from DiagnosticsTools.  
Use this package to record and replay runtime changes without referencing the DevTools UI assembly.

## Getting Started

```xml
<PackageReference Include="DiagnosticsTools.Runtime" Version="*" />
```

```csharp
var coordinator = new RuntimeMutationCoordinator();
coordinator.RegisterPropertyChange(control, Control.BackgroundProperty, oldValue, newValue);
coordinator.ApplyUndo();
```

## Key Types

- `RuntimeMutationCoordinator` – tracks undo/redo stacks for runtime edits.
- `IMutableTreeNode` – lightweight abstraction for tree nodes that support removal/undo.

## Upgrade Notes

- Move existing calls from `Avalonia.Diagnostics.RuntimeMutationCoordinator` (inside DevTools) to this package.
- Tree view models should implement `IMutableTreeNode` instead of relying on internal DevTools types.
- No UI dependencies are shipped with the package; hosts provide their own view models or adapters.
