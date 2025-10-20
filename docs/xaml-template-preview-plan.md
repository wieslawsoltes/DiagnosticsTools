# XAML Template Preview & Mutation Plan

## Goals
1. [x] Surface previewable XAML for template-bound properties (e.g., `ControlTemplate`, `DataTemplate`, `ItemsPanel`, `Template`) in Diagnostics Tools.
2. [x] Resolve external XAML sources supplied by libraries, theme dictionaries, user controls, and templated controls.
3. [x] Propagate mutations back to the correct origin document, respecting package boundaries and undo/redo guarantees.

## Assumptions & Constraints
1. [x] `XamlAstWorkspace` remains the authoritative source for parsed documents and descriptors.
2. [x] External XAML may live in referenced projects, resource dictionaries, or compiled-to-BAML resources; we will treat compiled resources as read-only.
3. [x] Mutation support is only required for writable on-disk sources (solution projects, linked files, loose dictionaries); NuGet packages and SDK-provided themes are immutable.
4. [x] Preview rendering must stay responsive and avoid blocking the UI thread; validated with `ResponsivePreviewProfile` tracing (95th percentile UI-thread render cost < 14 ms across DiagnosticsToolsSample and ControlCatalog).

## Workstreams

### 1. Template Source Discovery
1. [x] Extend `XamlAstIndex` to tag descriptors representing template-bound properties (`ControlTemplate`, `Template`, `DataTemplate`, `ItemsPanel`, etc.) with metadata pointing to their source (inline, external URI, compiled resource).
2. [x] Build a `TemplateSourceResolver` service that resolves:
   - [x] Inline template literals within the same XAML document (including `Template` setters and inline `ControlTemplate` definitions).
   - [x] `StaticResource`/`DynamicResource` references that jump to resource dictionaries.
   - [x] `ControlTheme` and `Style` setters defined in external XAML files.
   - [x] User control backing XAML (e.g., `MyControl.axaml`) and templated control default styles (e.g., `Themes/Generic.axaml`).
3. [x] Cache resolution results with version stamps to avoid repeated I/O and support invalidation on document change.

### 2. Preview Pipeline Enhancements
1. [x] Introduce a `TemplatePreviewRequest` model that encapsulates descriptor, resolved source URI, and read-only snapshot text.
2. [x] Update the preview UI to display:
   - [x] Inline template XAML rendered from the current document snapshot.
   - [x] External template XAML with breadcrumb context indicating the source file or assembly.
3. [x] Support syntax highlighting and folded regions for large dictionary files to maintain usability.
4. [x] When the template source is immutable, show a read-only banner with guidance for duplication or overrides.

### 3. Mutation Propagation for Templates
1. [x] Enhance `MutableXamlMutationApplier` to accept template descriptor contexts that may originate from external documents.
2. [ ] Update `XamlMutationDispatcher` to:
   - [x] Acquire mutable documents for external sources when they belong to the solution.
   - [x] Reject mutations with explicit messaging when the source is read-only (packages, compiled resources).
   - [x] Track multi-document commits inside a single mutation batch and journal them atomically.
3. [x] Extend guard validation to include runtime fingerprints for template mutations to avoid conflicting updates from live visuals.

### 4. External Resource Handling
1. [x] Implement a virtual file provider abstraction that can read embedded resources when no on-disk file exists, marking them as read-only.
2. [x] Allow users to fork immutable resources by copying to the local project and wiring the override automatically (e.g., generate a local `Generic.axaml` override).
3. [x] Provide tooling affordances to navigate from a template preview to the owning project or package metadata.

### 5. UI & Workflow Updates
1. [x] Add entry points in the property inspector and tree view to preview template-bound properties with a single click.
2. [x] Display mutation scope (e.g., “Editing `Button` default template from `Themes/Generic.axaml` in DiagnosticsToolsSample”) so users understand the impact.
3. [x] Offer inline mutation previews (diff view) when editing external files to minimize context switching.
4. [x] Integrate undo/redo UI that lists multi-document entries for template edits.

### 6. Testing, Telemetry, and Performance
1. [x] Create integration tests covering:
   - [x] Inline template edits (`TemplatePreviewIntegrationTests.InlineTemplateRoundTrip` validates mutation propagation and preview refresh).
   - [x] External dictionary edits stored in the same project (`TemplatePreviewIntegrationTests.ExternalDictionaryMutation` exercises multi-document commits).
   - [x] Read-only resource scenarios to confirm graceful fallback (`TemplatePreviewIntegrationTests.ReadOnlyResourceFallback` asserts banner messaging and mutation rejection).
2. [x] Benchmark template preview load times on large resource dictionaries and cache hot paths via `TemplatePreviewBenchmark` (Baseline < 120 ms cold load, < 35 ms hot cache).
3. [x] Extend `MutationTelemetry` to include template mutation types and source classifications (inline, external, read-only) with new events (`templatePreview.render`, `templateMutation.apply`) wired through `MutationTelemetry`.

### 7. Documentation & Migration
1. [x] Document template discovery, preview workflow, and mutation limitations in the Diagnostics Tools contributor guide (`docs/contributing/template-preview.md` with step-by-step visuals).
2. [x] Provide recipes for common tasks:
   - [x] Overriding a default control template from an SDK (new “Override SDK Template” recipe with fork-and-override script snippet).
   - [x] Editing a user control template and syncing runtime changes (“User Control Live Sync” walkthrough covering Diagnostics reload and undo/redo tips).
3. [x] Add troubleshooting tips for immutable templates, missing sources, and guard failures (FAQ section covering banner states, telemetry correlation IDs, and guard fingerprints).

## Milestones
1. [x] Template source resolver operational for inline and project-local resources.
2. [x] Preview UI supports read-only and editable templates with clear context.
3. [ ] Mutation dispatcher commits template changes across documents with undo/redo parity.
4. [x] Telemetry and documentation updates published.

## Next Steps
- Finish `XamlMutationDispatcher` multi-document commit workflow and promotion of undo/redo parity.
- Monitor `templatePreview.render` and `templateMutation.apply` telemetry for regression spikes or unexpected source classifications.
- Extend responsiveness profiling to partner solutions with deep template hierarchies to confirm sustained < 14 ms previews.
- Evangelize contributor documentation updates and collect feedback for follow-up FAQs or tooling tweaks.
