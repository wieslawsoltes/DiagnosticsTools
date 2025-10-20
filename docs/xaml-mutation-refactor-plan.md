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

1. [x] **Introduce a Writable AST Layer**
   - [x] Define mutation-friendly wrapper nodes (`MutableXamlElement`, `MutableXamlAttribute`, etc.).
   - [x] Provide conversion from `XamlAstNodeDescriptor` → mutable node and back.
   - [x] Preserve trivia (whitespace, formatting) so serialization can round-trip.

2. [x] **Extend Workspace API**
   - [x] Add `GetMutableDocumentAsync(path)` returning a mutable tree plus version metadata.
   - [x] Provide `CommitMutableDocument(path, mutableDocument)` that:
     - [x] Serializes back to XAML text.
     - [x] Updates workspace caches and raises `DocumentChanged`.

3. [x] **Update Mutation Dispatcher**
   - [x] Replace text-edit generation in `XamlMutationEditBuilder` with AST mutations:
     - [x] Locate target node via descriptor ID → mutable node map.
     - [x] Apply property edits, including attribute add/remove, directly on the mutable tree.
     - [x] Support element removal on the mutable tree with descriptor map rebuilds.
     - [x] Handle element insertion/replacement (upsert) on the mutable tree.
   - [x] On commit:
     - [x] Serialize mutated tree.
     - [x] Persist file and register entry in mutation journal using before/after text.
   - [x] Default `UseMutablePipeline` to the AST path for dispatcher operations (UI toggle still pending).
   - [x] Retire legacy text-edit mutation logic once AST pipeline reaches parity.

4. [x] **Descriptor Mapping Improvements**
   - [x] Maintain a stable descriptor-to-node map inside mutable documents for fast lookup.
   - [x] On each mutation, update the map instead of recomputing descriptors from spans.
   - [x] Expose APIs for diagnostics UI to retrieve descriptors by runtime node ID.

5. [x] **Selection & Synchronization**
   - [x] Adjust `TreePageViewModel` to pull descriptors from the new map rather than heuristics.
   - [x] When workspace invalidates documents, rebuild the map once and broadcast updates to the tree.

6. [x] **Undo/Redo Integration**
   - [x] Store serialized snapshots (pre/post) in the journal as today.
   - [x] When undoing/redoing, load mutable tree from snapshot, commit via workspace API to keep caches consistent.
   - [x] Port undo/redo execution paths to operate on mutable documents without falling back to text edits.

7. [x] **Incremental Rollout**
   - [x] Phase 1: Implement mutable layer and make dispatcher opt-in (behind flag) for property changes.
   - [x] Phase 2: Migrate node deletion, insert, rename operations.
   - [x] Phase 3: Remove legacy text-edit path once parity is proven.

8. [ ] **Tooling & Tests**
   - [x] Add unit tests for AST mutations covering attributes, nested elements, namespace handling.
   - [x] Provide integration tests that run property change + undo/redo on sample XAML and assert serialized output.
   - [ ] Benchmark serialization to ensure acceptable latency for large documents.
   - [x] Add regression tests that exercise the mutable serializer and dispatcher end-to-end.

9. [ ] **Documentation & Migration**
   - [ ] Document new workspace APIs for future extensions.
   - [ ] Provide guidance for contributors on writing AST mutations instead of text edits.

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
