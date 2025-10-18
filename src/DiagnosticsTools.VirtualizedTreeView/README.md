# DiagnosticsTools.VirtualizedTreeView

Virtualized tree view controls extracted from the DiagnosticsTools application.

## Contents
- `VirtualizedTreeView` – templated control that renders tree nodes over a flattened backing collection.
- `VirtualizedTreeViewItem` – item container with keyboard navigation, expand/collapse gestures, and templated visuals.
- `FlatTree` utilities – helper types that materialize hierarchical data into a lazily expanded flat list.

## Usage
Reference the NuGet package (once published) or the project directly:

```bash
dotnet add package DiagnosticsTools.VirtualizedTreeView
```

```xml
<ResourceInclude Source="avares://DiagnosticsTools.VirtualizedTreeView/Controls/VirtualizedTreeView/VirtualizedTreeView.axaml" />
```

The controls live under the namespace `Avalonia.Diagnostics.Controls.VirtualizedTreeView`.
