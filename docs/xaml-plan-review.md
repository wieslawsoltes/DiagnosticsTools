# XAML Plan Review

## docs/xaml-ast-plan.md

1. [x] Fix the `WorkspaceWorkspaceChanged` typo to `WorkspaceChanged` so the plan reflects the actual Roslyn event name.  
   - Evidence: `docs/xaml-ast-plan.md:87`
   - Recommendation: Update the milestone text to reference `Workspace.WorkspaceChanged`.
2. [x] Reconcile the stated Avalonia hot-reload integration with the current implementation, which does not wire any hot-reload notifications.  
   - Evidence: `docs/xaml-ast-plan.md:87`, `src/DiagnosticsTools/Diagnostics/PropertyEditing/MutationProvenance.cs:10`
   - Recommendation: Either document this as pending work or add the missing event bridge before claiming it as delivered.
3. [x] Refresh the “Next actions” note under Milestone 4 so it no longer refers to “once Task 3 starts,” which has already shipped.  
   - Evidence: `docs/xaml-ast-plan.md:137`
   - Recommendation: Replace the stale note with the actual follow-up items for the completed go-to-definition work.
4. [x] Align the “Threading & performance” bullet with the current loading path, which reads the entire file via `StreamReader` instead of chunking through `SourceText`.  
   - Evidence: `docs/xaml-ast-plan.md:73`, `src/DiagnosticsTools/Diagnostics/Xaml/XmlParserXamlAstProvider.cs:150`
   - Recommendation: Either adjust the documentation or change the loader to use incremental `SourceText` as described.
5. [x] Update the described `IXamlAstIndex` API to match the actual contract (no `TryGetSpan` or `FindBindings(ControlPath)` helpers).  
   - Evidence: `docs/xaml-ast-plan.md:78`, `src/DiagnosticsTools/Diagnostics/Xaml/XamlAstIndex.cs:10`
   - Recommendation: Document the existing projection methods or extend the interface so the plan and code stay aligned.
6. [x] Clarify the incremental update pipeline description—the provider currently invalidates caches on change notifications instead of eagerly diffing and reparsing in the background.  
   - Evidence: `docs/xaml-ast-plan.md:88`, `src/DiagnosticsTools/Diagnostics/Xaml/XmlParserXamlAstProvider.cs:205`
   - Recommendation: Note the lazy reparse strategy or add the background worker that the plan promises.
7. [x] Capture an eviction strategy for `_cache` to avoid unbounded growth across long-lived sessions.  
   - Evidence: `docs/xaml-ast-plan.md:71`, `src/DiagnosticsTools/Diagnostics/Xaml/XmlParserXamlAstProvider.cs:25`
   - Recommendation: Plan telemetry-driven eviction or document the decision to rely solely on explicit invalidation.

## docs/xaml-editor-plan.md

8. [x] Retire or restyle the “Findings Snapshot” list so it is clear these defects are now historical, not active.  
   - Evidence: `docs/xaml-editor-plan.md:4`
   - Recommendation: Move the items to a “Resolved issues” appendix or mark them explicitly as fixed.
9. [x] Revise Milestone 1 Task 1 to describe the new baseline-refresh behavior instead of claiming `_mutationOrigins` is cleared after every mutation.  
   - Evidence: `docs/xaml-editor-plan.md:11`, `src/DiagnosticsTools/Diagnostics/PropertyEditing/PropertyInspectorChangeEmitter.cs:1066`
   - Recommendation: Explain that mutation origins are updated in place while document changes still trigger cache invalidation.
10. [x] Harden `_mutationOrigins` and `_pendingMutationInvalidations` against concurrent access from `FileSystemWatcher` callbacks.  
    - Evidence: `src/DiagnosticsTools/Diagnostics/PropertyEditing/PropertyInspectorChangeEmitter.cs:206`, `src/DiagnosticsTools/Diagnostics/PropertyEditing/PropertyInspectorChangeEmitter.cs:872`
    - Recommendation: Introduce locking or switch to thread-safe collections and add regression tests for simultaneous dispatcher and watcher activity.
