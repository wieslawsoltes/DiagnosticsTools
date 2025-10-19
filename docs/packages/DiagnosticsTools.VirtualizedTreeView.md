# DiagnosticsTools.VirtualizedTreeView

A high-performance tree view control that renders large hierarchical datasets without sacrificing scrolling performance. The package contains the same components used inside DiagnosticsTools' visual tree inspector.

## Installation

```bash
dotnet add package DiagnosticsTools.VirtualizedTreeView
```

Import the control templates into your application:

```xml
<ResourceInclude Source="avares://DiagnosticsTools.VirtualizedTreeView/Controls/VirtualizedTreeView/VirtualizedTreeView.axaml" />
```

## Quick Start

```xml
<v:VirtualizedTreeView Items="{Binding Nodes}"
                       SelectedItem="{Binding SelectedNode}"
                       IsHierarchical="{Binding HasChildren}">
  <v:VirtualizedTreeView.ItemTemplate>
    <DataTemplate>
      <TextBlock Text="{Binding DisplayName}" />
    </DataTemplate>
  </v:VirtualizedTreeView.ItemTemplate>
</v:VirtualizedTreeView>
```

Back your view-model with a `FlatTree` to translate hierarchical data into a lazily expanded collection:

```csharp
using DiagnosticsTools.VirtualizedTreeView.FlatTree;

var root = FlatTreeBuilder.Create(rootNode, getChildren: node => node.Children);
var source = new FlatTreeDataGridSource(root);
treeView.Items = source;
```

## In-Depth Usage

### Flattening Strategies

- `FlatTreeBuilder` accepts eager or lazy child factories. Use lazy factories when retrieving children is expensive (e.g., remote diagnostics).
- Configure `FlatTreeNodeOptions` to set expansion behavior, auto-select children, or append placeholder items while loading.

### Item Containers & Styling

- `VirtualizedTreeViewItem` surfaces `IsExpanded`, `Depth`, and `ToggleCommand` for styling the tree representation.
- Override the `TreeViewItemStyle` resource to customise indentation, glyphs, or focus visuals.
- Hook into `RequestBringIntoView` to keep nodes visible during incremental expansion.

### Keyboard & Pointer Interaction

- Built-in commands handle left/right/space expansion, multi-selection, and context menu gestures.
- Use the `SelectionAdapter` to map `SelectionChanged` back to view-model commands or to integrate with diagnostics panes.

## Key APIs

- `VirtualizedTreeView`
- `VirtualizedTreeViewItem`
- `FlatTreeBuilder`
- `FlatTreeDataGridSource`

## See Also

- [DiagnosticsTools.Runtime](./DiagnosticsTools.Runtime.md) – pair tree edits with undo/redo tracking.
- [DiagnosticsTools.XamlAst](./DiagnosticsTools.XamlAst.md) – produce node metadata for tree view presentation.
