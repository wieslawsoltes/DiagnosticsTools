# Property Editor Change Serialization Contract

## Purpose

Finalize the serialization contract that turns property inspector gestures into deterministic XAML AST mutations that can be persisted back to source documents without losing formatting or runtime fidelity.

## Scope

- **In scope**: local value edits, binding authoring, resource reassignment, style setter creation/update/removal, attached property updates, property element insertion, converter/editor output, and metadata updates that originate in the diagnostics property inspector.
- **Out of scope**: code-behind edits, runtime-only preview changes, and cross-document refactorings (handled by specialized workflows).

## Guiding Principles

- Preserve the original document structure, whitespace, comments, and attribute ordering where possible.
- Keep each inspector gesture mapped to a single `ChangeBatch` so undo/redo and telemetry reflect user intent.
- Provide enough guard information to detect divergences between the inspected runtime view and the current XAML document.
- Make payloads self-describing and versioned so that contract consumers can evolve independently.

## Core Concepts

| Concept | Description |
| --- | --- |
| `ChangeEnvelope` | Transport wrapper that carries document metadata, originating inspector gesture, and one `ChangeBatch`. |
| `ChangeBatch` | Ordered list of `ChangeOperation`s emitted for a single gesture (e.g., slider drag, binding conversion). May include inverse operations for undo. |
| `ChangeOperation` | Atomic AST mutation request such as setting an attribute, inserting a node, or renaming a resource. |
| `ChangeContext` | Element- and inspector-centric metadata (runtime element ID, property path, frame, XAML descriptor IDs). |
| `ChangeTarget` | Normalized identifier for the AST node to mutate (descriptor ID + human-readable path + optional selector metadata). |
| `ValueSource` | Origin of the property value being edited: `LocalValue`, `Template`, `StyleSetter`, `Theme`, `Inherited`. Drives guard expectations. |
| `Guard` | Optimistic concurrency information (document version, span hash, runtime fingerprint). |

## Inspector Event Sources and AST Mapping

The tables below map each inspector gesture to the concrete `ChangeOperation` sequence emitted in the `ChangeBatch`. Unless noted, operations are single-step.

### Local Value and Property Element Edits

| Gesture | Value source | AST target | Change operations (ordered) | Notes |
| --- | --- | --- | --- | --- |
| `SetLocalValue(property, literal)` | `LocalValue` | Attribute on owning element | `SetAttribute` (`valueKind=Literal`) | Value normalized via type converters provided by the property metadata. |
| `SetLocalValue(property, markupExtensionString)` | `LocalValue` | Attribute | `SetAttribute` (`valueKind=MarkupExtension`) | Inspector provides canonical string or structured binding data. |
| `SetLocalValue(property, bindingModel)` | `LocalValue` | Attribute | `SetAttribute` (`valueKind=Binding`, `binding` payload) | Binding model serialized deterministically; see binding schema below. |
| `SetLocalValue(property, resourceReference)` | `LocalValue` | Attribute | `SetAttribute` (`valueKind=Resource`, `resourceKind=Static|Dynamic|Theme`) | Ensures resource namespace prefixes preserved. |
| `SetLocalValue(property, complexContent)` | `LocalValue` | Property element | `UpsertElement` with serialized fragment | Fragment respects indentation policy and namespace declarations. |
| `SetAttachedProperty` | `LocalValue` | Attribute on owning element | `SetAttribute` with `{namespace}` metadata | `ChangeTarget` includes attached property owner type. |
| `ClearLocalValue(property)` | `LocalValue` | Attribute or property element | `RemoveNode` | Removes attribute or property element depending on current representation. |
| `ResetToTemplateValue(property)` | `Template` | Attribute/property element | `RemoveNode` (local value) → optional `SetAttribute` to template-specific binding | Template binding emitted only when inspector explicitly overrides template defaults. |
| `UpdateContentTextBlock` (inline text) | `LocalValue` | Text node child | `SetAttribute` or `UpsertElement` depending on representation | If element uses text-only content, convert to `SetAttribute` with `valueKind=Literal`. |

### Binding Editor Gestures

| Gesture | Value source | AST target | Change operations | Notes |
| --- | --- | --- | --- | --- |
| `EditBinding(bindingModel)` | `LocalValue` or `StyleSetter` | Attribute | `SetAttribute` with updated binding payload | Binding payload includes canonical string plus structured fields for mode, path, converter, etc. |
| `ConvertBindingToLocalValue(literal)` | `LocalValue` | Attribute | `SetAttribute` (`valueKind=Literal`) | Inspector includes `previousValueKind` in operation metadata for undo. |
| `ConvertLocalValueToBinding(bindingModel)` | `LocalValue` | Attribute | `SetAttribute` (`valueKind=Binding`) | Removes inline literals; guard ensures original literal still present. |
| `ToggleBindingTwoWay` | `LocalValue` | Attribute | `SetAttribute` (`valueKind=Binding`, `binding.mode=TwoWay`) | Treated as an edit to the existing binding payload. |
| `ClearBinding` | `LocalValue` | Attribute | `RemoveNode` OR `SetAttribute` with `valueKind=Unset` | When the property has a default value, inspector uses `RemoveNode`; otherwise emits explicit literal via follow-up `SetAttribute`. |

### Resource and Style Tweaks

| Gesture | Value source | AST target | Change operations (ordered) | Notes |
| --- | --- | --- | --- | --- |
| `AssignStyle(key)` | `Style` | Attribute on owning element | `SetAttribute` (`name="Style"`, `valueKind=Resource`) | Uses `StaticResource` or `DynamicResource` depending on inspector selection. |
| `ClearStyle()` | `Style` | Attribute | `RemoveNode` targeting `Style` attribute | Guard ensures style assignment has not been externally edited. |
| `AddSetter(property, literal)` | `StyleSetter` | `<Setter>` element under `Style.Setters` | `UpsertElement` (new `<Setter Property="" Value=""/>`) | Batch may include `UpsertElement` to create `<Style.Setters>` when missing. |
| `AddSetter(property, bindingModel)` | `StyleSetter` | `<Setter>` child | `UpsertElement` with nested `<Setter.Value>` fragment | Inspector emits multi-operation batch: create setter → `UpsertElement` for value element. |
| `UpdateSetterValue` | `StyleSetter` | Existing `<Setter>` | `SetAttribute` on `Value` attribute or `UpsertElement` for `<Setter.Value>` | `ChangeTarget` points to setter descriptor ID. |
| `RemoveSetter(property)` | `StyleSetter` | `<Setter>` element | `RemoveNode` | Cascade removal of empty `<Style.Setters>` handled via optional trailing `RemoveNode`. |
| `ReorderSetter(from,to)` | `StyleSetter` | Sibling `<Setter>` nodes | `ReorderNode` | Maintains guard on parent setter collection fingerprint. |
| `ReassignResource(key)` | `ResourceDictionary` | Resource element | `SetAttribute` or `UpsertElement` depending on structure | When key changes, combine with `RenameResource`. |
| `RenameResource(oldKey,newKey)` | `ResourceDictionary` | Resource element | `RenameResource` (`traceImpact=true/false`) | Optionally followed by `SetAttribute` updates to references when supported. |
| `UpdateResourceValue(fragment)` | `ResourceDictionary` | Resource element | `UpsertElement` or `SetAttribute` | Prefers structural update to preserve indentation and inner elements. |
| `LinkToThemeResource(key)` | `Theme` | Attribute | `SetAttribute` with `valueKind=Resource`, `resourceKind=Theme` | `ChangeContext.frame="Theme"`. |

### Batch Composition Rules

- A `ChangeBatch` must remain ordered; consumers apply operations sequentially.
- Guard checks run before the batch is applied; failure of any guard aborts the entire batch.
- Composite gestures (e.g., add setter, initialize value element) emit multiple operations within one batch.
- Inspector emits `ChangeBatch.mergeBehavior` hint (`Replace`, `Append`, `MergeWithExistingBatch`) so mutation engine can coalesce continuous slider drags.

## Change Envelope Schema

Every `ChangeBatch` is wrapped in a `ChangeEnvelope`. The JSON payload uses camelCase and deterministic key ordering.

```jsonc
{
  "schemaVersion": "1.0.0",
  "batchId": "guid",
  "initiatedAt": "2024-03-09T12:34:56.123Z",
  "source": {
    "inspector": "PropertyEditor",
    "gesture": "SetLocalValue",
    "uiSessionId": "runtime-session-guid"
  },
  "document": {
    "path": "/Views/MainWindow.axaml",
    "encoding": "utf-8",
    "version": "sha256:9cfa…",
    "mode": "Writable"
  },
  "context": {
    "elementId": "runtime://visual-tree/123",
    "astNodeId": "xaml://doc/113",
    "property": "Button.Content",
    "frame": "LocalValue",
    "valueSource": "LocalValue"
  },
  "guards": {
    "documentVersion": "sha256:9cfa…",
    "runtimeFingerprint": "rtfp:ab12…"
  },
  "changes": [
    {
      "id": "op-1",
      "type": "SetAttribute",
      "target": {
        "descriptorId": "xaml://doc/113/attr:Content",
        "path": "Button[0].@Content",
        "nodeType": "Attribute"
      },
      "payload": {
        "name": "Content",
        "namespace": "",
        "valueKind": "Literal",
        "newValue": "Apply",
        "binding": null
      },
      "guard": {
        "spanHash": "h64:ad73…"
      }
    }
  ]
}
```

### Header Fields

- `schemaVersion`: Semantic version of this contract. Increment the minor version for additive changes.
- `batchId`: Globally unique identifier for de-duplication and telemetry correlation.
- `initiatedAt`: UTC timestamp captured at gesture completion.
- `source`: Identifies UI surface (`PropertyEditor`, `QuickAction`, etc.), specific gesture, and optional session correlation.
- `document`: File path (normalized), encoding, writable mode, and current version fingerprint.
- `context`: Runtime element identifiers, property path, inspector frame, and `valueSource`.
- `guards`: Optional set of optimistic concurrency tokens. Empty object means "no guard".

### Common Operation Payload Fields

- `id`: Stable within batch for referencing dependent operations.
- `target`: Uniform descriptor for the AST node. `descriptorId` is the canonical AST ID, `path` is a human-readable breadcrumb, `nodeType` is `Attribute`, `Element`, `Text`, or `Resource`.
- `payload`: Operation-specific data (see below).
- `guard`: Optional span-level guard complementing batch-level guards.
- `mergeBehavior`: Optional hint for coalescing repeated operations (`Replace`, `Append`, `DiscardIfExists`). Defaults to `Replace`.

### Operation Payloads

- **SetAttribute**
  - Required: `name`, `namespace`, `valueKind`, `newValue`.
  - Optional: `binding` (structured binding object), `resource` (structured resource reference), `previousValueKind`.
  - `valueKind` enumerations: `Literal`, `Binding`, `MarkupExtension`, `Resource`, `ThemeResource`, `TemplateBinding`, `Unset`.
  - When `binding` is present, include `{ path, mode, updateSourceTrigger, converter, converterParameter, stringFormat, targetType }`.
- **UpsertElement**
  - Required: `serialized`, `insertionIndex`.
  - Optional: `createContainer` (e.g., create `<Style.Setters>`), `indentationPolicy`, `surroundingWhitespace`.
- **RemoveNode**
  - Required: `descriptorId`.
  - Optional: `cascade` (`DeleteEmptyParent`, `PreserveWhitespace`).
- **ReorderNode**
  - Required: `descriptorIds` (ordered array), `newIndex`.
  - Optional: `companionDescriptorIds` for multi-node moves.
- **RenameResource**
  - Required: `oldKey`, `newKey`.
  - Optional: `cascadeTargets` (list of descriptor IDs to update), `requiresConfirmation`.

## Guarding and Conflict Detection

- Guard evaluation order: document version → span hash → runtime fingerprint.
- Span hashes cover the exact text span identified by `target.descriptorId`.
- For batches that create new nodes, include parent span hash in the guard to detect concurrent insertions.
- When a guard fails, the mutation engine must stop applying further operations and return a `GuardFailure` diagnostic with the first failing guard, the expected fingerprint, and the current fingerprint.

## Application Semantics

1. Inspector gesture completes; UI collects runtime context and current AST descriptor IDs.
2. Inspector composes `ChangeBatch`, populating payloads based on the mapping tables above.
3. `ChangeEnvelope` serialized to JSON and sent to mutation engine.
4. Mutation engine validates schema version, guards, and document writability.
5. Each `ChangeOperation` executed in order against a cloned AST, producing a patch set.
6. Patch applied to the live document buffer; formatting preserved according to indentation policy.
7. Engine emits success or failure diagnostics, updates undo stack, and triggers downstream refresh notifications.

## Error Handling

- **GuardFailure**: No mutations applied; inspector should prompt the user to refresh or open a merge dialog. Include conflicting spans in the diagnostic payload.
- **MutationValidationError**: Parser rejects the resulting fragment. Report offending operation ID and provide diff preview.
- **SerializationError**: Occurs before dispatch; log and block gesture until serialization bug is fixed.
- **UnsupportedGesture**: Inspector attempted to emit an unrecognized `gesture`; surface actionable telemetry and suppress the mutation.

## Undo/Redo Strategy

- Each `ChangeBatch` carries optional `inverseChanges`. When omitted, the mutation engine computes inverses before committing.
- Undo stack groups by `batchId`. Redo reapplies the same serialized batch to guarantee deterministic results.
- Batches tagged with `mergeBehavior=Append` (e.g., slider drags) coalesce consecutively to avoid flooding the undo stack.

## Telemetry Expectations

- Emit telemetry event per `batchId` with dimensions: `gesture`, `valueKind`, `frame`, `changeTypes` (set of operation types), `guardOutcome`, `durationMs`, `documentSize`.
- Record failure diagnostics with `operationId`, `failureType`, and `guardType` to monitor reliability.
- Sample large literal payloads (>256 chars) before logging to avoid PII leakage.

## Worked Examples

### Example 1: Toggle Boolean Local Value

```jsonc
{
  "schemaVersion": "1.0.0",
  "batchId": "cb51b9cc-1c5e-4c36-9c1e-2392144959a1",
  "source": { "inspector": "PropertyEditor", "gesture": "ToggleCheckBox" },
  "document": { "path": "/Views/Dialog.axaml", "version": "sha256:217c…" },
  "context": { "elementId": "runtime://visual-tree/42", "property": "CheckBox.IsChecked", "frame": "LocalValue", "valueSource": "LocalValue" },
  "changes": [
    {
      "id": "op-1",
      "type": "SetAttribute",
      "target": { "descriptorId": "xaml://doc/200/attr:IsChecked", "path": "CheckBox[0].@IsChecked", "nodeType": "Attribute" },
      "payload": { "name": "IsChecked", "namespace": "clr-namespace:Avalonia.Controls", "valueKind": "Literal", "newValue": "True" },
      "guard": { "spanHash": "h64:9aa1…" }
    }
  ]
}
```

### Example 2: Add Setter with Binding to Style

```jsonc
{
  "schemaVersion": "1.0.0",
  "batchId": "a0d9b82d-fc3b-4e08-82cf-6e9be5a02c4d",
  "source": { "inspector": "PropertyEditor", "gesture": "AddSetter" },
  "document": { "path": "/Themes/Controls.axaml", "version": "sha256:31bc…" },
  "context": { "elementId": "runtime://visual-tree/999", "property": "Setter.Value", "frame": "StyleSetter", "valueSource": "StyleSetter" },
  "changes": [
    {
      "id": "op-1",
      "type": "UpsertElement",
      "target": { "descriptorId": "xaml://doc/70/elt:Style.Setters", "path": "Style[0].Setters", "nodeType": "Element" },
      "payload": {
        "serialized": "<Setter Property=\"Foreground\">\n  <Setter.Value>\n    <Binding Path=\"Theme.Foreground\" Mode=\"OneWay\" />\n  </Setter.Value>\n</Setter>",
        "insertionIndex": 3,
        "indentationPolicy": { "indentSize": 2, "useTabs": false },
        "createContainer": false
      },
      "guard": { "spanHash": "h64:51ee…" }
    }
  ]
}
```

## Implementation Checklist

- [ ] Property inspector emits `ChangeEnvelope` objects using the schema defined above.
- [ ] Mutation engine validates schema version and guard semantics.
- [ ] AST mutation utilities cover each operation type with unit tests for formatting preservation.
- [ ] Undo/redo stack consumes `ChangeBatch` identifiers to maintain gesture granularity.
- [ ] Telemetry and diagnostics structured according to the telemetry expectations section.
- [ ] Documentation consumers notified of schema changes through semantic version increments.
