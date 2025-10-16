# Combined Logical/Visual Tree View

## Objective
- Deliver a unified tree explorer that nests logical children and their visual/template descendants in one expandable hierarchy.
- Preserve existing diagnostics features (selection, context menu actions, property pane updates) while extending them to the combined view.
- Clarify template-part boundary to avoid confusing logical children with generated visuals.
- Reuse Avalonia public and internal APIs so the view stays in sync with runtime changes.

## UX Overview
- Add a **Combined Tree** tab next to the existing Logical and Visual tabs in `MainView`.
- Render logical nodes as today; expand a node to reveal immediate template/visual children tinted with a subdued foreground.
- Surface template metadata (e.g., `PART_ContentHost`, templated parent type) directly under each visual node for quick context.
- Keep the details panel and context menu identical to existing tree views to avoid retraining users.

## ViewModels & Models
- Introduce `CombinedTreePageViewModel : TreePageViewModel` that seeds its `Nodes` via a new `CombinedTreeNode.Create(root)` helper.
- Track selection across logical and template nodes so `SelectControl` and `FindNode` work for either branch.
- Cache the shared pinned property set and filters to align with `MainViewModel` expectations.

## Combined Node Hierarchy
- Implement `CombinedTreeNode : TreeNode` wrapping any `AvaloniaObject` and exposing extra state: `NodeRole` (`Logical`, `Template`, `PopupHost`), `TemplateName`, and `SourceControl`.
- Compose `TreeNodeCollection` subclasses:
  - `CombinedLogicalChildrenCollection` wires to `ILogical.LogicalChildren` and `TopLevelGroup.Items` (excluding the DevTools main window).
  - `CombinedTemplateChildrenCollection` observes `Visual.VisualChildren`, `StyledElement.TemplateAppliedEvent`, and `TemplatedParent` changes to add template parts under the owning logical node.
  - `PopupHostChildrenCollection` reuses `VisualTreeNode.VisualTreeNodeCollection` logic to surface flyouts, context menus, and tooltips.
- Ensure template descendants are appended after logical children while keeping stable ordering when collections change.

## Data Flow & Observables
- Mirror the existing `TreeNodeCollection` pattern using `AvaloniaList.ForEachItem` and `CompositeDisposable` for cleanup.
- Subscribe to `TemplateApplied` and `AttachedToVisualTree` on templated controls to refresh their template parts.
- Leverage `Control.Template?.Build` where available to discover named parts when visual children are not yet materialized.
- Guard against re-entrancy by batching node updates via `Dispatcher.UIThread.Post`.

## View Updates
- Create `CombinedTreePageView.axaml` based on `TreePageView` with:
  - A `TreeView` bound to `CombinedTreePageViewModel.Nodes` and `SelectedNode`.
  - Styles adding grey-tinted foreground for `NodeRole == Template` and italics for popup hosts.
  - Data template rows displaying type, classes, and element name with template metadata appended in brackets.
- Expose the same context menu commands by binding to the shared `TreePageViewModel` actions.

## MainViewModel Integration
- Add a `_combinedTree` field alongside `_logicalTree` and `_visualTree`.
- Extend `DevToolsViewKind` and `DevToolsOptions.LaunchView` with `CombinedTree`.
- Update `SelectedTab` switch logic and `TabStrip` order to include the new view.
- Adapt `RequestTreeNavigateTo` to optionally target the combined view, preferring whichever tree is currently active.

## Testing Strategy
- Build unit tests (new project or existing one) that:
  - Instantiate a templated control, apply the template, and assert logical vs template nodes are created with correct metadata.
  - Verify popup hosts (e.g., `ToolTip`, `ContextMenu`) appear as children when activated.
  - Ensure `SelectControl` navigates to both logical and template nodes without throwing.
- Add an integration smoke test covering `DevToolsOptions.LaunchView = CombinedTree` to confirm the correct tab bootstraps.

## Documentation & Follow-up
- Reference this document from `README.md` or a docs index listing advanced views.
- Note any limitations (e.g., dynamically generated visuals that arent part of `VisualChildren` until layout) for future enhancement.
- After implementation, capture screenshots demonstrating logical nodes with template descendants and update release notes.

## Implementation Checklist
1. [x] Add `CombinedTreeNode` and supporting collections under `Diagnostics/ViewModels`.
2. [x] Create `CombinedTreePageViewModel` and `CombinedTreePageView.axaml` with code-behind.
3. [x] Update enums, options, and `MainViewModel/MainView` to surface the new tab.
4. [ ] Extend context menu commands if additional template-focused actions are needed (not currently required).
5. [x] Add automated tests covering combined tree discovery and navigation.
6. [ ] Refresh documentation to describe usage and troubleshooting tips.
