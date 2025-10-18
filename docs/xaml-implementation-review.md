# XAML Tooling Implementation Review

## Summary
- Core AST services, mutation dispatch, and editor integrations generally align with the documented plans. The caching layers, undo journal, and navigation affordances are well structured and accompanied by unit coverage.
- Two high-severity issues surfaced during the review: thread safety around inspector mutation baselines, and file persistence forcing UTF-8 without preserving the original encoding. Both can result in user-visible failures (race crashes / corrupted files) and should be addressed before widening usage.
- Several minor quality gaps remain (e.g., cache eviction heuristics now tracked in the plan); no blocking defects beyond the items called out below.

## Findings

1. [x] **High – Property inspector mutation baselines are not thread-safe**  
   - **Resolution**: `PropertyInspectorChangeEmitter` now synchronizes `_mutationOrigins` and `_pendingMutationInvalidations` via dedicated locks and helper methods when tracking pending invalidations, extending suppression windows, or refreshing baselines (`src/DiagnosticsTools/Diagnostics/PropertyEditing/PropertyInspectorChangeEmitter.cs:41-44`, `117-148`, `245-288`, `639-678`, `862-877`, `1066-1074`). Workspace-driven invalidations share the same guards, eliminating cross-thread dictionary mutations raised by `XmlParserXamlAstProvider` (`src/DiagnosticsTools/Diagnostics/Xaml/XmlParserXamlAstProvider.cs:200-299`).  
   - **Next steps**: Add regression coverage that simulates simultaneous watcher + inspector updates to guard against future regressions.

2. [ ] **High – File persistence drops the original encoding**  
   - **What & where**: `WriteDocumentAsync` always recreates the file using `new UTF8Encoding(false)` (`src/DiagnosticsTools/Diagnostics/PropertyEditing/XamlMutationDispatcher.cs:435-449`).  
   - **Why it matters**: Projects that rely on BOMs or non-UTF8 encodings will silently lose them after an inspector edit, causing diffs, build pipeline regressions, or outright parse failures. The plan calls for “preserving formatting fidelity,” which includes encoding.  
   - **Recommendation**: Capture the source encoding when the document is loaded (e.g., expose it on `XamlAstDocument`) and re-use it on write. If the encoding cannot be determined, fall back to UTF-8 but surface a warning/telemetry. Extend tests to cover BOM and non-UTF8 scenarios.

3. [ ] **Medium – Mutation completion telemetry still accessed off-thread**  
   - **What & where**: Even after addressing item 1, ensure the `_pendingMutationInvalidations` guard remains accurate—the current “extend suppression window” write happens on the watcher thread (`PropertyInspectorChangeEmitter.cs:229-233`).  
   - **Why it matters**: Without marshaling to the dispatcher, the suppression window can be extended while an inspector mutation is in-flight, leaving the UI with stale baselines that never refresh.  
   - **Recommendation**: Coalesce workspace notifications onto the UI thread before adjusting suppression windows, or at minimum take and release a lock shared with the mutation path to avoid stale writes.

## Observations / Follow-ups
- Cache eviction heuristics for `XmlParserXamlAstProvider` are now documented as future work (`docs/xaml-ast-plan.md:71`); re-evaluate once telemetry is available.
- Hot-reload integration is explicitly deferred (`docs/xaml-ast-plan.md:87`). Keep the plan updated as the dispatcher hook lands.
