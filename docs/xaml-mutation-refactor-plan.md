# XAML Mutation Refactor – Detailed Plan

## Goals
- Guarantee property edits and node deletions always target the correct XAML element.
- Remove fragile span/line heuristics and rely on authoritative AST information.
- Keep the existing diagnostics tooling responsive and compatible with current UI flows.

## Constraints & Assumptions
- Existing `XamlAstWorkspace` and `XamlAstIndex` provide read-only snapshots; mutating the underlying AST requires new APIs or a separate mutation layer.
- We must continue to support undo/redo via `XamlMutationDispatcher` and the mutation journal.
- Refactor should be incremental to avoid breaking current usage.

## Workstream Overview

1. **Introduce a Writable AST Layer**
   - Define mutation-friendly wrapper nodes (`MutableXamlElement`, `MutableXamlAttribute`, etc.).
   - Provide conversion from `XamlAstNodeDescriptor` → mutable node and back.
   - Preserve trivia (whitespace, formatting) so serialization can round-trip.

2. **Extend Workspace API**
   - Add `GetMutableDocumentAsync(path)` returning a mutable tree plus version metadata.
   - Provide `CommitMutableDocument(path, mutableDocument)` that:
     - Serializes back to XAML text.
     - Updates workspace caches and raises `DocumentChanged`.

3. **Update Mutation Dispatcher**
   - Replace text-edit generation in `XamlMutationEditBuilder` with AST mutations:
     - Locate target node via descriptor ID → mutable node map.
     - Apply property/attribute/element changes directly on the mutable tree.
   - On commit:
     - Serialize mutated tree.
     - Persist file and register entry in mutation journal using before/after text.

4. **Descriptor Mapping Improvements**
   - Maintain a stable descriptor-to-node map inside mutable documents for fast lookup.
   - On each mutation, update the map instead of recomputing descriptors from spans.
   - Expose APIs for diagnostics UI to retrieve descriptors by runtime node ID.

5. **Selection & Synchronization**
   - Adjust `TreePageViewModel` to pull descriptors from the new map rather than heuristics.
   - When workspace invalidates documents, rebuild the map once and broadcast updates to the tree.

6. **Undo/Redo Integration**
   - Store serialized snapshots (pre/post) in the journal as today.
   - When undoing/redoing, load mutable tree from snapshot, commit via workspace API to keep caches consistent.

7. **Incremental Rollout**
   - Phase 1: Implement mutable layer and make dispatcher opt-in (behind flag) for property changes.
   - Phase 2: Migrate node deletion, insert, rename operations.
   - Phase 3: Remove legacy text-edit path once parity is proven.

8. **Tooling & Tests**
   - Add unit tests for AST mutations covering attributes, nested elements, namespace handling.
   - Provide integration tests that run property change + undo/redo on sample XAML and assert serialized output.
   - Benchmark serialization to ensure acceptable latency for large documents.

9. **Documentation & Migration**
   - Document new workspace APIs for future extensions.
  - Provide guidance for contributors on writing AST mutations instead of text edits.

## Risks & Mitigations
- **Formatting drift**: Preserve trivia and spacing when mutating; include tests that assert output matches input formatting.
- **Performance regressions**: Cache mutable documents and reuse where possible; profile on large trees.
- **Incomplete feature parity**: Keep old path behind fallback switch until new pipeline covers all mutation types.

## Milestones
1. Mutable AST representation & serialization prototype.
2. Mutation dispatcher switched to AST for property edits.
3. Undo/redo compatibility verified.
4. All mutation types (add/remove/reorder) migrated.
5. Legacy text-edit code removed; documentation updated.

