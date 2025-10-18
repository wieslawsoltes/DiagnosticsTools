# DiagnosticsTools Core Refinement Plan

## Objectives
- Analyse the remaining `src/DiagnosticsTools` project and identify infrastructure that can be extracted into reusable libraries, reducing coupling with the DevTools UI.
- Preserve current functionality: the DevTools window, property inspector, visual tree, hotkeys, runtime mutation pipeline, and screenshot support must behave exactly as they do today.
- Establish a staged refactoring roadmap that enables incremental extraction, documentation, and packaging without breaking existing consumers.

## Current State Overview

- **UI Shell** (`Views`, `ViewModels`, `Controls`, `Xaml`): Implements the DevTools window, tabs, previews, and interactions. Tight coupling to Avalonia UI objects and should remain in the application project.
- **Property Editing** (`Diagnostics/PropertyEditing`): Contains mutation dispatch, journaling, guard utilities, and instrumentation. Depends on the XAML AST workspace and mutation instrumentation abstractions.
- **Runtime & Hotkeys** (`Runtime`, `HotKeyConfiguration`, `Behaviors`): Runtime mutation coordinator, hot key binding helpers, and behaviours applied to tool windows. Mostly logic with minimal UI surface.
- **Common Utilities** (`Converters`, `VisualExtensions`, `VisualTreeDebug`, `Screenshots`, `KeyGestureExtensions`, `ViewLocator`, `TypeExtensions`): Reusable helpers not strictly tied to the DevTools UI but currently internal to the project.
- **Source Navigation & XAML AST**: Already extracted to `DiagnosticsTools.SourceNavigation` and `DiagnosticsTools.XamlAst`.

## Candidate Libraries & Responsibilities

1. **DiagnosticsTools.PropertyEditing**
   - `PropertyInspectorChangeEmitter`, `XamlMutationDispatcher`, `XamlMutationEditBuilder`, `XamlMutationJournal`, `MutationTelemetry`, guard utilities.
   - Public surface aimed at mutation orchestration and mutation event notifications.
   - Depends on `DiagnosticsTools.XamlAst` for indexing and uses optional telemetry hooks.

2. **DiagnosticsTools.Runtime**
   - `RuntimeMutationCoordinator`, runtime attach/detach helpers, DevTools host convenience APIs.
   - Could expose factory methods for wiring DevTools into an app without referencing the UI assembly.

3. **DiagnosticsTools.Input**
   - Hot key configuration, behaviours, and key gesture helpers (`HotKeyConfiguration`, `Behaviors`, `KeyGestureExtensions`).
   - Optional; may merge with Runtime if scope stays small.

4. **DiagnosticsTools.Screenshots**
   - Screenshot abstractions (`IScreenshotHandler`, `Screenshots` folder). Potentially reusable for other tooling scenarios.

5. **DiagnosticsTools.Core**
   - Shared utility layer (converters, visual extensions, visual tree debug helpers). Serves as a foundational package for non-UI logic.

## Dependency Inventory (Checklist Item 1)

### Property Editing (`Diagnostics/PropertyEditing`)
- **Key Types:** `PropertyInspectorChangeEmitter`, `XamlMutationDispatcher`, `XamlMutationEditBuilder`, `XamlMutationJournal`, `XamlMutationDispatcher`, `MutationTelemetry`, `XamlGuardUtilities`, `XamlTextEdit`.
- **Internal Dependencies:** 
  - `Avalonia.Diagnostics.Xaml` (`XamlAstWorkspace`, descriptors) for document/index access.
  - `MutationTelemetry` infrastructure for optional mutation observers.
  - Dispatcher interactions rely on `Avalonia.Threading.Dispatcher.UIThread`.
- **External Dependencies:** `System.Text.Json`, `Microsoft.Language.Xml`, `Avalonia` (data binding, markup extensions).
- **Consumers:** `MainViewModel`, `TreePageViewModel`, `ControlDetailsViewModel`, xUnit tests (`PropertyInspectorChangeEmitterTests`, `XamlMutationDispatcherTests`).
- **Extraction Considerations:** 
  - Provide interfaces for mutation dispatching and mutation completion events (currently internal).
  - Replace direct `Dispatcher.UIThread` usage with delegate injection or an abstraction to avoid UI dependencies.
  - Ensure telemetry sinks remain optional attachments rather than hard dependencies.

### Runtime Mutation (`Diagnostics/Runtime/RuntimeMutationCoordinator`)
- **Key Types:** `RuntimeMutationCoordinator` + nested mutations.
- **Dependencies:** 
  - Uses `TreeNode` abstractions from `Diagnostics/ViewModels` (logical/visual tree).
  - Directly touches `Avalonia.Controls` and `Avalonia.VisualTree`.
  - Requests UI-thread access via `Dispatcher.UIThread`.
- **Consumers:** `MainViewModel`, tree view models for undo/redo.
- **Extraction Considerations:** 
  - Need to move `TreeNode` (or introduce an interface `IMutableTreeNode`) into a sharable assembly before extraction.
  - Replace hard dependency on view-models with a service interface that the DevTools UI implements.

### Hot Key & Behaviors (`HotKeyConfiguration`, `Diagnostics/Behaviors`, `KeyGestureExtensions`)
- **Dependencies:** `Avalonia.Input`, `Avalonia.Interactivity`, `Avalonia.Controls`.
- **Consumers:** `MainViewModel`, `HotKeyPageViewModel`, view XAML behaviours.
- **Extraction Considerations:** Standalone library possible; minimal cross-module coupling. Evaluate whether behaviours rely on internal view models.

### Screenshots (`Diagnostics/Screenshots`, `IScreenshotHandler`)
- **Key Types:** `IScreenshotHandler`, `BaseRenderToStreamHandler`, `FilePickerHandler`.
- **Dependencies:** `Avalonia.Controls`, `Control.RenderTo`.
- **Consumers:** `MainViewModel` (screenshot commands), `ScreenshotsPage`.
- **Extraction Considerations:** Self-contained; easy to publish as a separate package once command plumbing is decoupled.

### Shared Utilities (`Converters`, `VisualExtensions`, `VisualTreeDebug`, `TypeExtensions`, `ViewLocator`, etc.)
- **Dependencies:** Core Avalonia types (`IControl`, `VisualTree`, `DataTemplates`).
- **Consumers:** Multiple view models and views; broadly reusable.
- **Extraction Considerations:** Move into `DiagnosticsTools.Core` once their consumers are updated to reference the new package. Ensure no references to DevTools-specific view models remain.

### Remaining UI-Specific Modules (`ViewModels`, `Views`, `Controls`, `DevTools.*`)
- **Purpose:** Compose packages into the DevTools UI; should stay in the application project.
- **Dependencies:** All extracted packages + Avalonia UI components.
- **Action:** Maintain as orchestrating layer; introduce adapters where decoupling is required (e.g., new runtime interfaces).

### Cross-Cutting Interfaces/Adapters Required
- `IPropertyMutationService`, `IMutationInstrumentation`, `IXamlAstWorkspaceFactory` to avoid new packages referencing the DevTools UI.
- `IRuntimeMutationCoordinator`/`IMutableTreeNode` to decouple runtime undo/redo logic from view models.
- `IScreenshotService` to expose screenshot operations without referencing DevTools commands directly.

This dependency map satisfies checklist item 1 and informs the next steps for contract design and extraction sequencing.

## Proposed Public Contracts (Checklist Item 2)

### Property Editing Package
- **Interfaces**
  - `IPropertyMutationService` – central entry point for apply/preview operations. Methods: `ValueTask<MutationResult> ApplyAsync(PropertyMutationRequest request, CancellationToken)`, `ValueTask<MutationPreview> PreviewAsync(PropertyMutationRequest request, CancellationToken)`.
  - `IMutationInstrumentation` – abstraction for telemetry/logging hooks (`RecordMutation`, `RecordAstReload`, `RecordAstIndexBuild`).
  - `IPropertyMutationOriginStore` – optional persistence for mutation origins and suppression windows.
  - `IXamlMutationWorkspaceFactory` – factory to create/configure XAML workspaces used during mutations.
- **Data Contracts**
  - `PropertyMutationRequest` (document path, targets, gesture, command metadata, value payload).
  - `MutationResult` (status, message, affected paths, failures).
  - `MutationPreview` (diff entries, validation messages).
- **Adapters Required**
  - Wrap existing `XamlMutationDispatcher`/`PropertyInspectorChangeEmitter` into `IPropertyMutationService`.
  - Inject Dispatcher access via delegates rather than hard-coded `Dispatcher.UIThread`.

### Runtime Package
- **Interfaces**
  - `IRuntimeMutationCoordinator` – `RegisterPropertyChange`, `TryApplyRemoval`, `UndoAsync`, `RedoAsync`, `Clear`.
  - `IMutableTreeNode` – abstraction describing tree nodes (visual reference, index, parent).
  - `IRuntimeMutationHost` – optional callbacks around mutation lifecycle (before/after apply).
- **Data Contracts**
  - `ElementRemovalContext` (node identifier, index, stored item metadata).
- **Adapters Required**
  - Tree view models implement `IMutableTreeNode` and hand data to coordinator.
  - Expose undo/redo commands through interface rather than direct view-model coupling.

### Input/HotKey Package
- **Interfaces**
  - `IHotKeyRegistry` – register/unregister gestures, expose current configuration.
  - `IHotKeyHandler` – callback invoked when a registered gesture executes.
- **Data Contracts**
  - `HotKeyDescriptor` (gesture, scope, description).
  - Reuse `HotKeyConfiguration` as the mutable configuration object.

### Screenshots Package
- **Interfaces**
  - `IScreenshotService` – `Task CaptureAsync(ScreenshotContext context, CancellationToken)`.
  - `IScreenshotStorageProvider` – resolves output streams/paths.
  - `IScreenshotRenderer` – abstraction over rendering a control to a stream/bitmap.
- **Data Contracts**
  - `ScreenshotContext` (target control, format, metadata).

### Core Utilities Package
- **Interfaces / Types**
  - Primarily static helper classes (`VisualTreeUtilities`, `TypeUtilities`, converters) with no DevTools dependencies.
  - Optional `IVisualTreeInspector` interface if advanced inspection is needed by host apps.

### Bridging Strategy
- DevTools application will provide concrete implementations/adapters for these interfaces while the new packages remain UI-agnostic.
- Instrumentation is entirely optional: host applications can provide no-op implementations of `IMutationInstrumentation`.
- Tests will target the new contracts directly, enabling package-level validation without spinning up the full DevTools UI.

## Proposed Refactoring Steps

1. **Inventory & Dependency Mapping**
   - Build a dependency graph for `PropertyEditing`, `Runtime`, `Screenshots`, and common utilities.
   - Identify cross-references to UI components to determine required adapter layers.

2. **Define Public API Contracts**
   - Draft interfaces for mutation orchestration (`IPropertyMutationService`), runtime activation (`IDevToolsRuntimeCoordinator`), and screenshot capture (`IScreenshotService`).
   - Annotate internal-only code in the DevTools UI that will become consumers of the new packages.

3. **Create New Class Library Projects**
   - Scaffold SDK-style projects under `src/PropertyEditing`, `src/Runtime`, `src/Screenshots`, etc., mirroring the pattern established by `SourceNavigation`/`XamlAst`.
   - Establish shared Directory.Build props to align TFMs (`netstandard2.0`, `net6.0`, `net8.0`) and packaging metadata.

4. **Incremental Code Migration**
   - Move property editing infrastructure first, introducing adapters in the DevTools UI to preserve behaviour.
   - Relocate runtime coordinator and hotkey utilities.
   - Move screenshot helpers.
   - Migrate shared utilities last once upstream dependencies are resolved.

5. **Update DevTools Application**
   - Replace internal references with package/project references.
   - Ensure DI/initialisation pipeline uses the new abstractions (e.g., `PropertyInspectorChangeEmitter` from the property editing package).

6. **Testing & Validation**
   - Extend unit tests for each new package (mutation pipeline, runtime coordinator, screenshot provider).
   - Run targeted DevTools integration tests and perform manual QA to confirm observed behaviour is unchanged.
   - Dedicated test projects exist for `DiagnosticsTools.PropertyEditing`, `DiagnosticsTools.Runtime`, `DiagnosticsTools.Input`, `DiagnosticsTools.Screenshots`, and `DiagnosticsTools.Core` to exercise the public contracts added during extraction.
   - Provide headless harnesses/mocks (e.g., storage providers, timers, dispatcher adapters) so screenshot and hotkey tests can execute without UI dependencies.

7. **Documentation & Packaging**
   - Update `README` sections for each package detailing install instructions, sample usage, and API surfaces.
   - Refresh the roadmap and plan once extraction is complete, capturing any follow-up tasks (e.g., cross-package instrumentation guidance).
   - Per-package upgrade notes now live alongside each library (`src/*/README.md`) describing how consumers transition from the monolithic `DiagnosticsTools` assembly.
   - Add quick-start snippets demonstrating wiring for property editing, runtime undo/redo, hotkeys, and screenshot services in the host application's documentation.

## Checklist Roadmap

1. [x] Produce detailed dependency map and list of public APIs for migration targets.
2. [x] Define interfaces/contracts for mutation, runtime, screenshot, and utility packages.
3. [x] Scaffold new class library projects with packaging metadata.
4. [x] Extract property editing infrastructure and adapt DevTools UI.
5. [x] Extract runtime + hotkey helpers and adapt DevTools UI.
6. [x] Extract screenshot utilities.
7. [x] Migrate shared converters/extensions to a core utilities package.
8. [x] Update DevTools project references and clean up obsolete code paths.
9. [x] Expand unit/integration tests for each new package.
10. [ ] Document package usage and update public readme/docs.
11. [ ] Perform full regression QA and adjust roadmap for remaining work.
