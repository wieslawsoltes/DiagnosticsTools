# XAML Editor Evolution Plan

## Resolved Findings (Historical)
> All items below were uncovered during the initial stabilization pass and are retained here for archival context.

1. Cached mutation origins were never cleared; reverting after external edits could emit attribute removals (`PropertyInspectorChangeEmitter.GetOrCreateMutationOrigin`).
2. Revert detection relied on `Equals`, so bindings/markup extensions with new instances failed to recognize roundtrips (`PropertyInspectorChangeEmitter.ShouldUnsetAttribute`).
3. Original attribute text was captured but not reused, causing formatting churn when values were restored (`PropertyMutationOrigin.AttributeValue`).
4. The dispatcher rebuilt the full AST index on every mutation, hurting responsiveness (`XamlMutationDispatcher.DispatchAsync`).
5. Attribute insertion did not honor indentation/ordering, leading to inconsistent formatting (`XamlMutationEditBuilder.BuildAttributeInsertionText`).

## Milestone 1 – Stabilize Mutation Infrastructure
1. [x] Keep `_mutationOrigins` baselines aligned by invalidating on `XamlAstWorkspace.DocumentChanged` and refreshing snapshots after each successful mutation.  
2. [x] Update mutation origins with the new runtime value after successful dispatches to align revert detection.  
3. [x] Introduce structural comparison helpers for bindings, markup extensions, and resource references to detect true reverts.  
4. [x] Reuse captured attribute text when restoring initial values to preserve author formatting.  
5. [x] Cache `IXamlAstIndex` snapshots keyed by document version to avoid reparsing for successive edits.

_Next steps_: tighten thread-safety around `_mutationOrigins` and `_pendingMutationInvalidations` to guard FileSystemWatcher callbacks (dispatcher marshaling + regression coverage).

## Milestone 2 – Broaden Change Operation Support
1. [x] Add rename element, reorder/move node, namespace, and content-text operations with safe edit builder support.  
2. [x] Implement attached-property mutation support across parent/child scopes with guard validation.  
3. [x] Enable batched envelopes with parent/child guard checks so multi-step edits apply atomically.

## Milestone 3 – Enhance AST & Document Services
1. [x] Extend `IXamlAstProvider` with node-level diff/change events to minimize rebuild work.  
2. [x] Maintain cross-indexes for resources, name scopes, bindings, and styles to empower dependency-aware tooling.  
3. [x] Stream parser/semantic diagnostics through the workspace to warn or block conflicting edits.

## Milestone 4 – Runtime/Edit Synchronization
1. [x] Refresh inspector view models’ `PreviousValue` after each mutation and sync undo/redo state with dispatcher journals.  
2. [x] Detect external file edits, invalidate caches, and surface conflict resolution options.  
3. [x] Track mutation provenance (inspector, file watcher, hot reload) so undo stacks and telemetry remain coherent.

## Milestone 5 – Editor Experience Improvements
1. [x] Provide higher-level editing commands (toggle, slider, color picker, binding editor) that emit structured mutation envelopes.  
2. [x] Keep the preview/diff UI disabled by default and surface it only when a guard flags a potential issue, still falling back to raw text editing if the mutation cannot be applied.  
3. [x] Add rich editing features: multi-selection edits, subtree copy/paste, template editing with live preview, namespace auto-imports.

## Milestone 6 – Validation & Tooling
1. [x] Expand regression tests for revert handling (bindings/resources), whitespace preservation, multi-operation envelopes, and undo/redo after reloads.  
2. [x] Instrument mutation timings, guard failures, and AST reload costs to guide optimization.  
3. [x] Capture anonymized mutation telemetry to understand usage and prioritize future tooling work.
