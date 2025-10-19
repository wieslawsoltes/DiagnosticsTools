# DiagnosticsTools.Runtime

Lightweight runtime mutation coordinator that records undo/redo stacks for Avalonia applications. Use it to mirror the DiagnosticsTools live editing experience inside your own tooling or custom inspectors.

## Installation

```bash
dotnet add package DiagnosticsTools.Runtime
```

## Quick Start

```csharp
using DiagnosticsTools.Runtime;

var coordinator = new RuntimeMutationCoordinator();

coordinator.RegisterPropertyChange(
    target: myControl,
    property: Control.BackgroundProperty,
    oldValue: Brushes.White,
    newValue: Brushes.CornflowerBlue);

coordinator.ApplyRedo(); // Reapplies the most recent change.
coordinator.ApplyUndo(); // Restores the previous value.
```

## In-Depth Usage

### Integrating with Property Editing

- Subscribe to `MutationCompleted` from `DiagnosticsTools.PropertyEditing` and forward the serialized operations to the runtime coordinator.
- Wrap runtime mutations inside custom `IRuntimeMutation` implementations when you need to perform non-property actions (e.g., inserting or removing tree nodes).
- Use `MutationProvenance` metadata to distinguish user-initiated edits from scripted changes.

### Tree Synchronisation

`IMutableTreeNode` abstracts the minimal operations required for tree manipulation (expand/collapse, insert, remove).  
Implement it on your view-model nodes to enable the coordinator to replay structural mutations.

```csharp
public sealed class TreeNode : IMutableTreeNode
{
    // ... backing collection omitted ...
    public void RemoveChild(object child) => Children.Remove((TreeNode)child);
    public void InsertChild(int index, object child) => Children.Insert(index, (TreeNode)child);
}
```

### Persistence & Diagnostics

- Call `coordinator.SnapshotUndoStack()` to expose diagnostics about pending operations.
- Serialize entries to disk to provide session restore or crash-recovery features.
- Inject custom logging by handling the `UndoPerformed` and `RedoPerformed` events.

## Key APIs

- `RuntimeMutationCoordinator`
- `IRuntimeMutation`
- `IMutableTreeNode`

## See Also

- [DiagnosticsTools.PropertyEditing](./DiagnosticsTools.PropertyEditing.md) – emits the mutations you can replay through the coordinator.
- [DiagnosticsTools.VirtualizedTreeView](./DiagnosticsTools.VirtualizedTreeView.md) – render mutation targets with high-performance tree controls.
