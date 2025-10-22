# XAML Mutation – Future Work Plan

## Editing Pipeline Enhancements
1. [ ] Restore mutation preview support by diffing pre/post `MutableXamlDocument`, wiring the highlights back into `MutationPreviewWindow` (`src/DiagnosticsTools.PropertyEditing/XamlMutationDispatcher.cs`).
2. [ ] Benchmark serialization and commit latency for large XAML files, capturing baselines before further pipeline work (`docs/xaml-mutation-refactor-plan.md:60`).
3. [ ] Add a default `IMutationTelemetrySink` implementation to record guard failures, apply duration, and serializer timings in shipped builds (`src/DiagnosticsTools.PropertyEditing/MutationTelemetry.cs`).
4. [ ] Extend `ChangeOperationTypes` and `MutableXamlMutationApplier` to cover setters, triggers, and structural operations such as wrap/unwrap and duplicate (`src/DiagnosticsTools.PropertyEditing/PropertyInspectorChangeEmitter.cs`).
5. [ ] Enhance `ChangeEnvelope` handling so multi-selection edits can emit shared operations with companion descriptor IDs (`src/DiagnosticsTools.PropertyEditing/ChangeEnvelope.cs`).
6. [ ] Support cross-document mutations (e.g., resource extraction) by allowing `XamlMutationDispatcher` to commit coordinated updates to multiple paths per dispatch (`src/DiagnosticsTools.PropertyEditing/XamlMutationDispatcher.cs`).
7. [ ] Publish contributor documentation describing the mutable workspace, guard expectations, and test guidance for new mutation operations (`docs/xaml-mutation-refactor-plan.md:63`).

## Visual Editing Experience
1. [x] Implement a selection overlay service that renders runtime adorners from `SelectionCoordinator` updates in the host window (`src/DiagnosticsTools/Diagnostics/ViewModels/TreePageViewModel.cs`).
2. [x] Route pointer gesture updates through `RuntimeMutationCoordinator` so live moves/resizes stay undoable alongside XAML commits (`src/DiagnosticsTools.Runtime/RuntimeMutationCoordinator.cs`).
3. [x] Provide layout-specific handles (Canvas, Grid, Dock) translating drag gestures into guarded attribute mutations before commit (`src/DiagnosticsTools.PropertyEditing/MutableXamlMutationApplier.cs`).
4. [ ] Add snap lines and alignment guides by sampling sibling geometry from the diagnostics tree (`src/DiagnosticsTools/Diagnostics/ViewModels/TreePageViewModel.cs`).
5. [x] Surface a design-surface history pane backed by `XamlMutationJournal` snapshots for rollbacks during visual edit sessions (`src/DiagnosticsTools.PropertyEditing/XamlMutationJournal.cs`).
