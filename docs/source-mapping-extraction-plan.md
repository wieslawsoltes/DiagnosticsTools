# Diagnostics Tools Source Mapping Extraction Plan

## Objectives
- Split the current Avalonia Diagnostics Tools implementation into two reusable class libraries: one for PDB-based source discovery and another for XAML AST indexing.
- Preserve all existing behaviours (local file lookup, SourceLink fallback, Avalonia resource probing, XAML logical tree mapping, diagnostics, and file watching).
- Minimise churn to downstream consumers by keeping existing interfaces (`ISourceInfoService`, `ISourceNavigator`, `XamlAstWorkspace`, etc.) available through thin compatibility layers.
- Enable independent testing, packaging, and reuse of the new libraries in other diagnostics tooling scenarios.

## Current State Overview

### PDB & Source Mapping Pipeline
- `src/DiagnosticsTools/DiagnosticsTools/Diagnostics/SourceNavigation/SourceInfoService.cs` coordinates source lookup for CLR members and Avalonia objects. It owns:
  - A cache of `PortablePdbResolver` instances keyed by assembly path.
  - XAML document acquisition (`BuildXamlDocumentAsync`) and logical-tree-to-XAML-node mapping via private `XamlDocument`/`XamlNode` classes.
  - Resource metadata deserialisation (`ResourceXamlInfo`) and remote SourceLink fetch logic.
- `src/DiagnosticsTools/DiagnosticsTools/Diagnostics/SourceNavigation/PortablePdbResolver.cs` wraps `System.Reflection.Metadata` to read sequence points, SourceLink blobs, embedded/loose PDBs, and returns `SourceInfo`.
- `src/DiagnosticsTools/DiagnosticsTools/Diagnostics/SourceNavigation/SourceLinkMap.cs` parses SourceLink JSON and resolves document URIs.
- `src/DiagnosticsTools/DiagnosticsTools/Diagnostics/SourceNavigation/SourceInfo.cs` models source metadata and origin tracking.
- Consumers obtain an `ISourceInfoService` from `MainWindow.xaml.cs` and propagate it through view models (e.g., `Diagnostics/ViewModels/MainViewModel.cs`, `TreePageViewModel.cs`, `ValueFrameViewModel.cs`) to drive source previews.
- Unit coverage sits in `tests/DiagnosticsTools.Tests/PortablePdbResolverTests.cs`.

### XAML AST Infrastructure
- `src/DiagnosticsTools/DiagnosticsTools/Diagnostics/Xaml` contains the XML-backed AST stack:
  - `XamlAstWorkspace` caches parsed documents and exposes events for mutations.
  - `XmlParserXamlAstProvider` handles on-disk file watching, encoding detection, SHA-256 versioning, incremental caching, and uses `Microsoft.Language.Xml` to parse documents and collect diagnostics via `XamlDiagnosticMapper`.
  - `XamlAstIndex`, `XamlAstNodeDiffer`, `XamlAstDocument`, and related descriptor types drive lookups for bindings, styles, and named elements.
- Property editing components (`Diagnostics/PropertyEditing/*`) depend heavily on the AST (e.g., `PropertyInspectorChangeEmitter`, `XamlMutationDispatcher`, `XamlMutationJournal`).
- Instrumentation hooks (`MutationInstrumentation`) currently reside in `Diagnostics/PropertyEditing` but are invoked from both `XamlAstWorkspace` and `XmlParserXamlAstProvider`.

### Shared Touchpoints
- `SourceInfoService` bridges the two concerns by loading XAML documents (from local files, AVR resources, or SourceLink endpoints) and mapping logical tree paths to XML nodes.
- The diagnostics UI relies on both services: `SourcePreviewViewModel` expects `SourceInfoService` for source navigation and `XamlAstWorkspace` for preview rendering and mutation tracking.
- Remote XAML fetches reuse `HttpClient` from `SourceInfoService`; no equivalent exists in the XAML workspace.

## Target Architecture

### Library A — `DiagnosticsTools.SourceNavigation`
**Responsibilities**
- Resolve CLR members/types to `SourceInfo` using PDBs (portable, embedded, CodeView), SourceLink metadata, and assembly resource fallbacks.
- Provide extensibility points for retrieving XAML documents (local, embedded, remote) and mapping logical nodes to XML elements.
- Surface a reusable `ISourceInfoResolver` (replacement for `ISourceInfoService`) that can be hosted in different environments.

**Public Surface (initial proposal)**
- `SourceInfo`, `SourceOrigin`.
- `ISourceInfoResolver` with async APIs matching `ISourceInfoService`.
- `PortablePdbResolver` (public) and `IPdbResolverFactory` abstraction to allow alternative caching strategies.
- `SourceLinkMap` and lightweight helpers for SourceLink-aware path resolution.
- Optional `IXamlDocumentLocator` interface returning `(XDocument, SourceOrigin)` metadata given a `SourceDescriptor` (assembly + class name + fallback `SourceInfo`).

**Internal Organisation**
- Move PDB handling (`PortablePdbResolver`, `SourceLinkMap`) unchanged into the new project.
- Extract XAML-specific helpers from `SourceInfoService` into dedicated collaborators:
  - `ResourceMetadataProvider` to read `!AvaloniaResourceXamlInfo` data contracts.
  - `XamlLogicalPathMapper` to translate logical tree paths (list of child indices) into XML nodes, implemented using `IXamlDocumentLocator`.
- Keep caching (concurrent dictionaries) but allow the hosting app to decide lifetime via injected factories (e.g., `ISourceResolverCache`).

**Dependencies**
- `System.Reflection.Metadata`, `System.Reflection.PortableExecutable`, `System.Xml.Linq`, Avalonia abstractions limited to `AvaloniaObject`, `ILogical`, and `IAssetLoader` (aim to wrap them in interfaces to reduce coupling).

### Library B — `DiagnosticsTools.XamlAst`
**Responsibilities**
- Provide the XML-based AST, indexing, diagnostics, and incremental change tracking for XAML files.
- Surface a file-system backed implementation (`XmlParserXamlAstProvider`) and allow alternative providers (e.g., language server).
- Offer instrumentation extension points instead of hard dependencies on `MutationInstrumentation`.

**Public Surface (initial proposal)**
- `XamlAstWorkspace`, `XamlAstDocument`, `XamlDocumentVersion`.
- `IXamlAstProvider`, `IXamlAstIndex`, `XamlAstNodeDescriptor`, and related descriptor/diagnostic types.
- `IXamlAstInstrumentation` (new) to record timing/metrics, defaulting to a no-op implementation.

**Internal Organisation**
- Retain parsing and diffing logic but relocate file watching, caching, and SHA-256 checksum routines into internal helpers.
- Isolate encoding detection and XML parsing utilities for reuse.
- Provide adapters in the DiagnosticsTools app to forward instrumentation events to `MutationInstrumentation`.

**Dependencies**
- `Microsoft.Language.Xml`, `System.IO.Abstractions` (optional for testing), `Avalonia.Utilities` only where unavoidable (e.g., `StopwatchHelper` — consider replacing with BCL).

### Integration Layer (DiagnosticsTools App)
- Keep a thin wrapper that adapts the new libraries to the existing UI contracts:
  - Implement legacy `ISourceInfoService` by delegating to the new `ISourceInfoResolver`.
  - Configure `XamlAstWorkspace` with the app-specific instrumentation adapter and expose it via view models.
- Provide default implementations of `IXamlDocumentLocator` that leverage `XamlAstWorkspace` for document retrieval and indexing.
- Maintain existing `DefaultSourceNavigator` (could also move to the source navigation library if no Avalonia dependencies remain).

## Extraction Steps

1. [x] **Preparation**
   - [x] Create solution folders `src/SourceNavigation` and `src/XamlAst` with new SDK-style class library projects targeting the same TFMs as `DiagnosticsTools`.
   - [x] Update `DiagnosticsTools.sln` to include the new projects and reference shared `Directory.Build.props`.
   - [x] Decide on namespace casing (`DiagnosticsTools.SourceNavigation`, `DiagnosticsTools.XamlAst`) and confirm package IDs (open question).

2. [x] **Refine Current Contracts**
   - [x] Introduce `ISourceInfoResolver` (app project) mirroring `ISourceInfoService` but omitting Avalonia-specific overloads; add adapters so existing callers compile.
   - [x] Define `IXamlDocumentLocator` and `ILogicalTreePathBuilder` interfaces to separate logical tree traversal from document lookup.
   - [x] Draft `IXamlAstInstrumentation` with methods used today (`RecordAstReload`, `RecordAstIndexBuild`) and provide a diagnostics app implementation that forwards to `MutationInstrumentation`.

3. [ ] **Extract Source Navigation Library**
   - [x] Move `SourceInfo`, `PortablePdbResolver`, `SourceLinkMap` into the new project; adjust namespaces and access modifiers to make them public/internal as required.
   - [x] Split `SourceInfoService` into:
     - [x] `SourceInfoResolver` (public) that handles resolver caching and calls into abstractions.
     - [x] `AssemblyDocumentLocator` implementing `IXamlDocumentLocator`, encapsulating current resource lookup + SourceLink HTTP fallback.
     - [x] `LogicalTreePathHelper` that encapsulates `TryBuildLogicalPath`, exposing methods that accept Avalonia-agnostic interfaces (inject adapters where Avalonia types are required).
   - [x] Inject dependencies (e.g., `IXamlDocumentLocator`, `IPdbResolverFactory`) via constructor; provide default implementations for existing behaviour.
   - [x] Update tests: move `PortablePdbResolverTests` into a new test project targeting the library, add coverage for resolver caching and SourceLink fallbacks.

4. [ ] **Extract XAML AST Library**
   - [x] Relocate the entire `Diagnostics/Xaml` folder to the new project, adjusting namespaces.
   - [x] Replace direct calls to `MutationInstrumentation` with the injected `IXamlAstInstrumentation`.
   - [x] Audit usages of `Avalonia.Utilities.StopwatchHelper` and either re-export minimal helpers or reimplement with `Stopwatch`.
   - [x] Ensure the provider no longer touches app-level singletons (e.g., remove implicit dependencies on `AvaloniaLocator`; the workspace should accept services via constructor).
   - [x] Add focused tests:
     - [x] File watching cache invalidation.
     - [x] Diagnostics mapping.
     - [x] Node diff correctness for simple change scenarios.

5. [ ] **Wire Libraries Back Into DiagnosticsTools**
   - [x] Reference the two new projects from `DiagnosticsTools.csproj`.
   - [x] Implement `ISourceInfoService` as an adapter that composes:
     - [x] `SourceInfoResolver` from library A.
     - [x] `XamlAstWorkspace`-backed implementation of `IXamlDocumentLocator`.
   - [x] Provide an instrumentation adapter that implements `IXamlAstInstrumentation` and delegates to `MutationInstrumentation` (placed in `Diagnostics/PropertyEditing`).
   - [x] Update view models and views to consume the adapters; ensure DI factories (`MainWindow.xaml.cs:369`) create the new resolver instead of the old `SourceInfoService`.
   - [x] Remove obsolete internal classes (`SourceInfoService.XamlDocument`, etc.) once adapters are wired.

6. [ ] **Testing & Validation**
   - [x] Run existing unit/integration tests and create new ones for adapter layers (e.g., verifying logical path resolution still matches XAML nodes for sample controls in `samples`).
   - [ ] Perform manual QA: verify source navigation for XAML-backed controls, code-behind methods, and SourceLink-only scenarios.
   - [ ] Stress test file watching by editing `.axaml` files while diagnostics tools are running.

7. [ ] **Documentation & Packaging**
   - [ ] Update `README.md` and `docs/source-navigation-plan.md` to reference the new libraries and onboarding steps.
   - [ ] Add XML documentation summaries in public APIs before publishing.
   - [ ] Decide on NuGet packaging strategy (single package vs. two) and update build scripts if necessary.

## Risks & Mitigations
- **Avalonia type coupling:** `SourceInfoService` currently relies on concrete `AvaloniaObject`/`ILogical`. Introduce interfaces or adapter helpers to avoid leaking Avalonia internals outside the app. Provide default adapters in the DiagnosticsTools app.
- **File watcher reliability:** Moving `XmlParserXamlAstProvider` could expose platform-specific watcher bugs; add integration tests using `FileSystemWatcher` and consider abstractions for unit tests.
- **Remote SourceLink latency:** Centralising SourceLink HTTP fetch logic inside the library may require cancellation/timeout controls; expose `HttpMessageHandler` injection for testability.
- **Binary compatibility:** Keep legacy namespaces or provide type-forwarders if external consumers already reference `Avalonia.Diagnostics.*` types.
- **Instrumentation loss:** Without careful adapter wiring, metrics might stop emitting; ensure `IXamlAstInstrumentation` defaults to no-op but DiagnosticsTools provides real implementation.

## Open Questions / Follow-Ups
- Confirm desired target frameworks for the new projects (match app or extend to netstandard2.0 for broader reuse?).
- Decide whether `DefaultSourceNavigator` should move to the source navigation library or remain in the host app.
- Determine if `ResourceXamlInfo` serialization should remain internal (requires Avalonia build tooling) or become an explicit contract.
- Evaluate need for additional public APIs (e.g., exposing logical path computation independently of Avalonia objects).
- Plan for future SourceLink caching (disk-based) to reduce repeated HTTP downloads.
