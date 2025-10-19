# Source Preview Code Review

## Scope
Focused on the source preview pipeline: `TreePageViewModel`, `SourcePreviewViewModel`, `SourcePreviewEditor`, and related helpers. Primary target areas were XAML editor selection handling and synchronisation with the diagnostics trees.

## Findings
1. **High – AST selection breaks for remote-only sources**  
   - `BuildXamlSelectionAsync` exits early when `SourceInfo.LocalPath` is missing (`src/DiagnosticsTools/Diagnostics/ViewModels/TreePageViewModel.cs:1804`). Many descriptors resolved through `SourceInfoService` (e.g. Fluent control themes) surface only a `RemoteUri`, so `SelectedNodeXaml` stays `null`. The preview window then falls back to coarse line highlights and the editor never knows which AST node to map, so tree ↔ editor sync collapses for those controls.  
   - **Fix ideas:** allow the workspace to accept remote URIs (download-once cache or feed `XamlSourceResolver` output into the workspace); alternatively, fall back to the `IXamlDocumentLocator` pipeline already used by `SourceInfoService` so nodes with remote sources still yield an AST snapshot. Record the descriptor once resolved so subsequent requests reuse it.

2. **High – Preview → tree reveal skips nodes without local files**  
   - `FindNodeBySelectionAsync` explicitly discards nodes whose `SourceInfo.LocalPath` is empty before it even considers line heuristics (`src/DiagnosticsTools/Diagnostics/ViewModels/TreePageViewModel.cs:1722-1734`). That excludes the very same remote-only cases described above, so Ctrl+Click (and caret moves) inside the editor never reach the diagnostics tree.  
   - **Fix ideas:** compare against `SourceInfo.RemoteUri` when `LocalPath` is absent, and treat document paths that already look like remote URIs as valid matches. As a fallback, reuse `_nodesByXamlId` by registering the descriptor coming from the preview selection when a match is found so future navigation avoids the slow path.

3. **Medium – Scoped tree prevents preview-driven selection**  
   - The search helper only traverses the currently scoped `Nodes` array (`src/DiagnosticsTools/Diagnostics/ViewModels/TreePageViewModel.cs:1762-1780`). When the diagnostics tree is scoped to a subtree, any preview selection outside that scope returns `null`, so the UI never exits scope even though `ApplySelectionFromPreview` is prepared to call `ShowFullTree()`.  
   - **Fix ideas:** switch enumeration to `_rootNodes` (or conditionally expand to `_rootNodes` when scoped) so targets can still be located, then keep the existing scope reset logic. Cache scope afterwards to avoid unnecessary re-filtering.

4. **Medium – Preview navigation is unnecessarily expensive**  
   - Every miss in `_nodesByXamlId` walks the whole tree and calls `EnsureSourceInfoAsync` (`src/DiagnosticsTools/Diagnostics/ViewModels/TreePageViewModel.cs:1711-1756`). Each call posts back to the UI thread to update `TreeNode.SourceInfo`, so large trees incur noticeable pauses when the editor caret changes quickly.  
   - **Fix ideas:** build per-document indices (path → sorted list of nodes/line ranges) once when the tree loads or when source info arrives, so navigation becomes a dictionary lookup. At minimum, batch the `EnsureSourceInfoAsync` work or reuse the already-computed info instead of re-posting for every node during a single search.

## Recommended Fix Plan
1. Extend the AST lookup to handle remote sources: teach `BuildXamlSelectionAsync` (and the workspace) to hydrate documents via `RemoteUri`, caching snapshots locally.  
2. Adjust `FindNodeBySelectionAsync` to accept remote URIs, and register descriptors obtained from preview selections back into `_nodesByXamlId` to short-circuit further lookups.  
3. Update node enumeration to fall back to `_rootNodes` when scoped, so preview-driven navigation can escape scopes automatically.  
4. Replace the per-search `EnsureSourceInfoAsync` sweep with a cached per-document index; invalidate that cache when the workspace publishes `NodesChanged` or when a mutation occurs.  
5. Add integration tests that cover (a) remote theme selection, (b) preview → tree sync after scoping, and (c) regression for repeated caret moves to confirm the new indexing stays responsive.

## Risk & Mitigation Notes
- Remote document support will introduce IO; cache aggressively and surface failures in the preview UI to keep the tool responsive.  
- Reworking the search index touches selection-critical code—wrap it with unit tests plus a headless integration test that simulates navigation loops to prevent regressions.
