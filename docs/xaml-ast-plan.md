# XAML AST and Diagnostics Integration Plan

## Milestone 1: Scope Alignment and Architecture Foundations

1. [x] Consolidate requirements for XAML AST navigation, source editing, and diagnostics-tree linkage with design, tooling, and runtime stakeholders.
2. [x] Produce a system diagram covering diagnostics trees, property inspector, AvaloniaEdit viewer, XmlParser-Roslyn services, and Roslyn workspaces.
3. [x] Define success metrics (latency targets, synchronization accuracy, undo/redo expectations) and document risks and contingency strategies.

### Task 1 Outcomes: Requirement Consolidation

- **Stakeholders**: runtime diagnostics team (tree, inspector), tooling UX, editor integration owners (AvaloniaEdit), platform maintainers (Avalonia core), build and packaging pipeline.
- **Functional**: parse all live XAML assets (loose files, compiled resources, templates), maintain bidirectional navigation between diagnostics tree nodes, AvaloniaEdit views, XAML AST nodes, and C# sources, allow property inspector edits to emit precise XAML updates with undo/redo, provide incremental reparse on file changes/hot reload, surface errors when linkages are missing or ambiguous.
- **Integration**: wrap `GuiLabs.Language.Xml` services behind diagnostics abstractions, respect existing Avalonia dependency injection and threading policies, support Roslyn workspace access for code-behind mapping, align AvaloniaEdit theming/input with diagnostics shell without breaking existing viewers.
- **Non-functional**: parsing round-trip under 200 ms for typical files, no noticeable UI freeze during navigation, maintain formatting fidelity on AST mutations, secure operations in read-only mode, and support telemetry hooks for health monitoring.
- **Dependencies and blockers**: availability of Roslyn workspace in standalone tooling scenarios, determining canonical mapping between runtime tree nodes and XAML (especially templates/styles), handling generated XAML (compiled bindings), coordinating with hot reload pipeline for notifications.
- **Open decisions**: persistence strategy (Roslyn workspace vs direct file IO), conflict resolution UX, scope of C# editing support, and guardrails for read-only or dev/prod modes.

### Task 2 Outcomes: Architecture Overview

```
Diagnostics Shell
├─ Tree Viewers (Logical / Visual / Object)
│   └─ Node Adapters ↔ XAML AST Lookup Service
├─ Property Inspector
│   └─ Change Dispatcher → XAML Mutation Engine → Persistence Layer
├─ Source Viewer (AvaloniaEdit)
│   ├─ Buffer Manager ↔ AST Highlight/Selection Service
│   └─ Command Bridge ↔ Navigation Coordinator
├─ Roslyn Integration
│   ├─ Workspace Access ↔ C# Symbol Resolver
│   └─ Document Tracker ↔ Synchronization Events
└─ Telemetry & Undo/Redo
    ├─ Event Bus ⇄ All Feature Modules
    └─ Diagnostics Logging + Performance Counters
```

- **Data flows**: runtime tree selection emits node IDs → AST lookup resolves XAML spans → AvaloniaEdit highlights and scrolls → inspector loads property context → user edits update AST and persist → Roslyn workspace receives reload notifications → updated tree refreshes bindings.
- **Boundary contracts**: AST services expose immutable snapshots to UI layers; mutation engine operates on cloned trees; navigation coordinator arbitrates between tree, editor, and inspector to prevent cyclic updates.
- **Threading**: parsing and Roslyn operations use background tasks; UI updates marshal back to dispatcher via diagnostics event bus.

### Task 3 Outcomes: Metrics and Risk Management

- **Success metrics**: ≤200 ms parse for 95th percentile XAML file; ≤100 ms navigation round-trip from tree node to highlighted source; ≤300 ms propagation of property edits to runtime and source; zero lost edits across undo/redo cycles; telemetry coverage for parse errors ≥95%.
- **Testing goals**: automated coverage for parsing edge cases (namespaces, resource dictionaries, control templates), navigation synchronization tests, mutation/undo cycles, Roslyn symbol resolution for partial classes, and integration tests for hot reload scenarios.
- **Risks**:
  - *Roslyn availability*: fallback stubs when workspace is missing; cache symbol data when running out-of-process.
  - *Parsing scalability*: implement file-sized tiered caching, throttle reparses, and fallback to partial parsing on large documents.
  - *Formatting drift*: include pretty-print diff checks and respect existing indentation/attributes ordering.
  - *Concurrency conflicts*: adopt optimistic concurrency with file change detection, surface merge prompts, and maintain history logs.
- **Mitigations**: staged rollout with feature flags, structured telemetry dashboards, recovery commands to reopen last successful snapshot, and documentation for manual conflict resolution paths.

## Milestone 2: XmlParser-Roslyn Adoption and XAML AST Pipeline

1. [x] Add the `GuiLabs.Language.Xml` NuGet package to the diagnostics tooling projects and resolve version compatibility with existing Avalonia dependencies.
2. [x] Implement a XAML parsing service that builds `XamlAst` models from project and loose XAML files via XmlParser-Roslyn, with caching keyed by file path and last write time.
3. [x] Map AST nodes to diagnostics metadata (e.g., named elements, bindings, styles, and resources) and expose a lookup API for diagnostics tree nodes.
4. [x] Establish incremental update hooks that re-parse on file changes, hot reload events, or editor buffer saves while preserving previously resolved node identities when possible.
5. [x] Centralize GuiLabs diagnostic extraction behind an internal mapper to prepare for future public APIs.

### Task 1 Outcomes: XmlParser-Roslyn Package Integration

- **Package selection**: `GuiLabs.Language.Xml` (XmlParser-Roslyn) NuGet (v1.2.15 as of current build) chosen for XAML AST generation; tracked for updates to v1.2.93+.
- **Project scope**: add package to diagnostics tooling core, experimental UI shell, and any shared infrastructure libraries; ensure Avalonia app runtime assemblies remain unaffected.
- **Compatibility**: validated against .NET 8 target framework, no dependency conflicts with Avalonia 11.x; monitor transitive `Microsoft.CodeAnalysis` versions to avoid Roslyn mismatch; aligned `System.Text.Json` to 6.0.11 to clear GHSA-8g4q-xg66-9fp4.
- **Build & distribution**: update lockfiles and CI configuration, include package source documentation, and align licensing files for redistribution compliance.

### Task 2 Outcomes: XAML Parsing Service Blueprint

- **Service contract**: `IXamlAstProvider` with async `GetDocumentAsync(string path)` returning cached `XamlAstDocument` snapshots plus `DocumentVersion` (timestamp/hash) and diagnostics.
- **Loading pipeline**: file content resolved via `IFileSystemAccessor` or Roslyn `TextDocument`; XmlParser-Roslyn `XmlParser` builds syntax tree → `XamlAstBuilder` transforms to domain model.
- **Caching strategy**: use `ConcurrentDictionary<string, CachedDocument>` keyed by normalized path; entries store file hash or last-write ticks; invalidation triggered by file watcher events, Roslyn workspace document change events, or explicit `Invalidate(path)` API, with telemetry-driven eviction heuristics queued to prevent unbounded growth in long-lived sessions.
- **Error handling**: capture parser exceptions and emit structured diagnostics (line/column, message) so diagnostics UI can present parse failures without crashing.
- **Threading & performance**: parsing runs on background thread pool with cancellation tokens; documents load asynchronously via shared `StreamReader` buffers (future `SourceText` chunking remains a stretch goal); telemetry wraps parse durations.
- **Extensibility hooks**: allow optional AST enrichers (e.g., binding analysis) to run post-parse; ensure pipeline is DI-friendly for testing with in-memory documents.

### Task 3 Outcomes: Diagnostics Metadata Mapping

- **Lookup API**: expose `IXamlAstIndex` with descriptor enumeration plus helpers such as `TryGetDescriptor(XamlAstNodeId id, out XamlAstNodeDescriptor descriptor)`, `FindByName(string name)`, and `FindByResourceKey(string key)` to correlate diagnostics nodes and AST elements.
- **Node identifiers**: derive stable IDs combining XAML element `x:Name`, runtime `IControl.Name`, resource keys, and hierarchical position; retain fallback hashed position for unnamed nodes.
- **Metadata projections**: convert AST nodes into `XamlDiagnosticsDescriptor` objects capturing element type, attributes, bindings, styles, and resources; attach source ranges for highlight overlays.
- **Tree linkage**: augment diagnostics tree view-models with `XamlDescriptorId`; maintain reverse index mapping spans to runtime nodes for reverse navigation.
- **Resource/style handling**: register specialized mappers for `<Style>`, `<DataTemplate>`, and `<ResourceDictionary>` sections to surface owner scope and implicit selectors.
- **Validation**: include normalization utilities to handle namespace alias resolution and templated parent references; log mismatches where runtime node has no XAML backing.

### Task 4 Outcomes: Incremental Update Hooks

- **Change detection**: subscribe to file-system watchers (workspace project files, loose XAML) and Roslyn `Workspace.WorkspaceChanged` events; hot-reload notifications remain on the roadmap while the dispatcher hook is finalized.
- **Update pipeline**: change notifications invalidate cached snapshots; the next consumer request triggers a fresh parse, preserving prior AST node IDs when possible without eager background work.
- **Debouncing**: coalesce rapid edits via dispatcher timer (e.g., 200 ms) to avoid thrashing; support manual `Refresh(path)` command for forced reload.
- **Notification flow**: emit `XamlDocumentChanged` events with new snapshot, invalidated nodes, and diagnostics; tree and inspector subscribe to refresh selection and metadata.
- **Error recovery**: maintain last-known-good snapshot; if parsing fails, surface errors but keep previous AST for navigation until fix committed.
- **Testing hooks**: expose virtual clock/delayed parse controls for integration tests to validate synchronization timing.

### Task 5 Outcomes: Diagnostics Mapping Helper

- **Reflection shim**: encapsulated GuiLabs `DiagnosticInfo` reflection in `XamlDiagnosticMapper` to translate spans, message text, and error identifiers to internal `XamlAstDiagnostic` records.
- **Extensibility**: mapper is localized, making it trivial to swap for official APIs once severity/message accessors are published.
- **Safety**: fallbacks ensure unknown diagnostics resolve to readable `ERR_None` with stringified payloads, avoiding hard failures in tooling UI.

## Milestone 3: Diagnostics Tree and AST Linking

1. [x] Extend logical/visual/object tree node view models to reference associated XAML AST nodes and carry source span information.
2. [x] Implement navigation commands that select and highlight the corresponding AST node when a diagnostics tree node is activated, including fallback messaging when no AST node exists.
3. [x] Provide reverse navigation from AST nodes back to diagnostics tree nodes, ensuring selections stay in sync across panes and that cyclic updates are debounced.

### Task 1 Outcomes: Tree ↔ AST Binding

- **Workspace plumbing**: `XamlAstWorkspace` now lives alongside dev tools view models, providing cached documents, indexes, and change events.
- **Selection payload**: `TreePageViewModel` exposes `SelectedNodeXaml` (with `XamlAstDocument` + descriptor) and refreshes data on tree selection, hot reload, and source edits.
- **Lookup strategy**: implemented heuristics (line span ranking, `x:Name`, type fallback) to match tree nodes with AST descriptors; AST metadata is ready for UI binding once the viewer integrates selection adorners.

### Task 2 Outcomes: Forward Navigation Commands

- **Viewer highlight**: tree selections now drive AvaloniaEdit highlighting inside `SourcePreviewWindow`, with multi-line spans derived from `XamlAstSelection` and fallbacks to `SourceInfo` metadata when descriptors are unavailable.
- **Document reuse**: source previews reuse cached `XamlAstDocument` content when available, avoiding redundant disk reads and keeping diagnostics in sync with the parsed AST.
- **Graceful degradation**: when no AST descriptor exists, the preview still opens, highlighting the original source line if present and surfacing the existing “unavailable” messaging.

### Task 3 Outcomes: Reverse Navigation

- **Descriptor registry**: tree nodes remember their assigned `XamlAstNodeDescriptor` IDs, allowing lookups from AST back into the virtualized tree while respecting scoped views.
- **Reveal in tree**: source preview adds a “Reveal in tree” command wired to `TreePageViewModel`, which expands ancestors, clears filters if needed, reselects the node, and brings it into view.
- **Debounce**: navigation requests post back to the UI thread and reuse the existing selection logic, preventing oscillation between tree and viewer selections.

## Milestone 4: AvaloniaEdit Source Viewer Integration

1. [x] Embed AvaloniaEdit within the diagnostics source viewer shell, aligning styling, input gestures, and theming with the existing tooling UI. _(Completed: editor host now exposes themed styles, button bindings, and keyboard gestures for navigation.)_
2. [x] Wire AvaloniaEdit to consume the XAML AST service for syntax coloring, folding, and structural navigation; include adorners for highlighting current selection spans. _(Completed: AST-backed folding builder plus selection overlays driven by `XamlAstSelection`.)_
3. [x] Implement go-to-definition commands that respond to diagnostics node activations, underline navigable elements, and support keyboard/mouse navigation patterns. _(Completed: ctrl-hover + click/F12 now open source directly from the preview, with visual affordances.)_
4. [x] Add synchronized scrolling and split-view options to compare live runtime values with underlying XAML definitions. _(Completed: dual-pane viewer with runtime snapshots, synchronized scrolling, orientation controls, and unit coverage.)_

### Task 1 Outcomes: Source Viewer Shell Integration

- **Editor host**: AvaloniaEdit is now the primary surface for the preview window with themed backgrounds, line numbering, and accent-aware selection/line highlighting.
- **Reusable control**: `SourcePreviewEditor` encapsulates the configured editor pipeline so other shells (e.g., go-to-definition panes) can plug in without duplicating wiring.
- **Commands & gestures**: Introduced `OpenSourceCommand`/`RevealInTreeCommand` with toolbar bindings and default shortcuts (`Enter`, `F12`, `Ctrl+Shift+R`, `Ctrl+O`), rounding out the navigation surface.
- **UX polish**: Updated window chrome with reveal telemetry, dynamic status strings, and consistent styling hooks for future palette refinements.
- **Next actions**: Promote the reusable editor host control, align clipboard operations with command infrastructure, and add navigation analytics to track go-to-source usage across shells.

### Task 2 Outcomes: Syntax + Structure Services

- **Folding pipeline**: Added `XamlAstFoldingBuilder` to project structural fold sections straight from parsed `XmlDocumentSyntax`, and wired it through a managed `FoldingManager` in the preview.
- **Selection adorners**: Layered AST-driven segment and line renderers plus a border overlay to emphasize the current descriptor span while maintaining theme awareness.
- **Validation**: New unit tests cover folding generation alongside the existing AST selection regression to safeguard the visual pipeline.
- **Next actions**: Feed node metadata into future adorners (e.g., style/resource badges) and surface folding diagnostics through the tree-to-source navigation workflow.

### Task 3 Outcomes: Go-to-Definition Commands

- **Navigation gestures**: ctrl-hover changes the editor cursor, underlines AST selections, and allows ctrl+click or F12 to open the underlying XAML using the shared navigator.
- **Command wiring**: the reusable `SourcePreviewEditor` dispatches through `OpenSourceCommand`, ensuring diagnostics panes and future go-to-definition surfaces share consistent behavior.
- **Visual hints**: selection adorners switch to underline mode when navigation is available, giving users clear affordances for keyboard and mouse activation.
- **Inline adoption**: the property inspector now embeds `SourcePreviewEditor` for applied value frames, giving bindings/resources the same inline navigation affordances as tree selections.
- **Extensibility**: `SourcePreviewEditor` now surfaces `InspectNavigation`, `NavigationRequested`, and `NavigationStateChanged` hooks so tooling features can add custom targets (bindings/resources) without forked editors.

### Task 4 Outcomes: Synchronized Comparison Views

- **Viewer layouts**: split-view toggle now instantiates paired `SourcePreviewEditor` hosts with persisted orientation, ratio, and visibility baked into the view model for session-to-session continuity.
- **Scroll linking**: shared `SourcePreviewScrollCoordinator` watches both editors, suppresses feedback loops, and reapplies scroll state when toggling panes or orientation changes.
- **Value bridging**: runtime pane renders live inspector frames as a formatted snapshot so property changes surface alongside the persisted XAML snippet.
- **Interaction affordances**: keyboard (`Alt+Shift+Up`) and toolbar controls flip orientations, and split, ratio, and pane visibility respond to persisted preferences for rapid comparisons.
- **Reliability**: added unit coverage for split-state persistence and manual snippet handling to guard against regressions; manual UX validation still recommended for accessibility/telemetry follow-up.
- **Dependencies and risks**: telemetry hooks for scroll latency remain in backlog; runtime snapshot formatting may require future theming to match main editor adorners.

## Milestone 5: Property Editor to XAML Update Pipeline

1. [x] Finalize the change-serialization contract that maps property inspector edits (setters, bindings, style tweaks) to concrete XAML AST modifications. _(Documented in `docs/property-editor-change-serialization.md`, schema version 1.0.0.)_
2. [x] Implement AST mutation utilities that update attributes, element content, or resource definitions while preserving formatting and respecting XmlParser-Roslyn constraints.
3. [x] Integrate file persistence using Roslyn workspace or direct file services, with validation, conflict detection, and undo/redo hooks tied to the diagnostics property editor.
4. [x] Emit notifications to refresh diagnostics trees, AvaloniaEdit buffers, and runtime bindings after successful updates, and surface errors with actionable remediation guidance.

### Task 3 Outcomes: File Persistence Integration

- **Workspace-aware persistence**: `XamlMutationDispatcher` now applies mutations through an optional Roslyn `Workspace`, falling back to direct file IO when the XAML asset is not tracked and keeping disk content aligned for subsequent AST loads.
- **Conflict resilience**: Guard hashes and optimistic concurrency checks surface version mismatches, while the property inspector’s undo/redo commands stay aligned via the shared `XamlMutationJournal`.
- **Diagnostics refresh**: `MainViewModel` listens for workspace document events to invalidate cached AST snapshots, keeping tree nodes and source previews current after external edits.
- **Validation**: Added headless coverage for workspace-backed persistence and serialised the test suite to keep dispatcher-bound flows deterministic.

### Task 4 Outcomes: Mutation Notifications

- **UI refresh**: `MainViewModel` now fans out mutation results to all tree panes so inspector panes, pinned properties, and applied frames immediately reflect new XAML state without manual refreshes.
- **Source previews**: `SourcePreviewViewModel` instances register with the diagnostics shell, reloading AvaloniaEdit buffers after successful saves and surfacing guard failures in the preview panel.
- **Runtime frames**: Property inspector frames rebuild their value diagnostics on success, keeping inline previews and frame metadata synchronized with updated bindings.
- **Guided errors**: Guard and persistence failures bubble actionable guidance (“refresh the inspector”, “check diagnostics output”) to both the inspector status banner and any open source previews.

## Milestone 6: Roslyn C# AST Integration for Linked Source

1. [ ] Evaluate current Roslyn workspace availability in diagnostics to ensure access to project compilation, semantic models, and document tracking.
2. [ ] Build cross-reference services that associate XAML elements with code-behind partial classes, named fields, event handlers, and data context types using Roslyn symbol analysis.
3. [ ] Expose navigation commands that jump between XAML AST nodes and related C# declarations, including support for generated partials and MVVM patterns.
4. [ ] Synchronize property updates that originate in C# (e.g., generated bindings) by surfacing read-only or bi-directional editing rules in the property inspector.

## Milestone 7: Quality Gates, Tooling UX, and Documentation

1. [ ] Create automated coverage (unit and integration tests) for AST parsing, node linking, navigation commands, and XAML mutation flows; include regression cases for known edge XAML constructs.
2. [ ] Profile performance for large XAML trees and solution-wide parsing, add telemetry around parsing duration and synchronization latency, and tune caching/invalidation heuristics.
3. [ ] Conduct UX validation sessions to confirm navigation clarity, highlighting behavior, and error messaging; iterate on accessibility considerations (keyboard navigation, screen readers).
4. [ ] Update diagnostics documentation with setup instructions, feature walkthroughs, troubleshooting guidance, and extension points for custom AST annotations.
