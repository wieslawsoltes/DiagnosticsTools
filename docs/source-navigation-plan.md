# Source Navigation Plan

## Objectives
- Surface XAML/C# source file, line, and column information for the objects represented in visual/resource trees.
- Provide context menu commands to jump from DiagnosticsTools to source definitions (local files or SourceLink remotes).
- Surface quick-glance metadata (tooltip overlays) and a richer preview dialog for source snippets.
- Leverage Avalonia/CLR debug symbols so the feature works without extra markup in app code.

## Technical Notes from Avalonia Repo Review
- `Avalonia.Base/Diagnostics/ValueStoreDiagnostic` and related `IValueFrameDiagnostic` expose `Source` objects for applied styles/themes; these objects need to be mapped back to compiled XAML definitions.
- `Avalonia.Markup.Xaml.XamlIl.Runtime.XamlIlRuntimeHelpers` emits `#line hidden` pragmas and sequence points to route debugger navigation into XAML, implying `*.pdb` files contain per-element offsets referencing `.axaml` documents.
- `Avalonia.Build.Tasks/XamlCompilerTaskExecutor` copies debug documents into generated IL ensuring sequence points reference original XAML URIs; this is where we will resolve `Document` paths.
- Build outputs (e.g., `obj/Debug/net8.0/<App>.pdb`) ship `*.sourcelink.json` manifests mapping local documents to repository URLs; the navigation service should respect these for cloned vs. remote lookups.
- `TreePageViewModel`, `CombinedTreeNode`, and `ControlDetailsViewModel` create the objects that will host new commands and tooltips. `VirtualizedTreeView.ContextFlyout` in `TreePageView.xaml` is where UI affordances will be added.

## Implementation Tasks
1. [x] **Audit debug metadata availability**: Confirmed Portable PDB sequence points enumerate `.axaml` and code-behind documents for DiagnosticsToolsSample and theme assemblies; documented runtime frame gaps.
2. [x] **Design source metadata model & service**: Implemented `SourceInfo`/`SourceOrigin`, `ISourceInfoService`, and `ISourceNavigator`, and wired them through DevTools options.
3. [x] **Implement PDB + SourceLink readers**: Delivered `PortablePdbResolver` with SourceLink parsing, embedded-PDB support, and per-assembly caching/fallback strategies.
4. [x] **Integrate resolver with Diagnostics view models**: Tree/combined nodes, details panes, and value frames now resolve `SourceInfo` asynchronously and expose navigation state/commands.
5. [x] **Augment UI interactions**: Context flyouts, badges, and the source preview dialog are live with snippet rendering plus copy/open actions wired through tree context menus and control detail headers.
6. [x] **Navigation execution layer**: Added `DefaultSourceNavigator` and DevTools option plumbing for host overrides.
7. [ ] **Testing & resilience**: Initial unit coverage added for view-model enablement plus new preview-command tests; still need resolver-focused cases, SourceLink failure handling, and command binding integration checks.
8. [ ] **Documentation & samples**: Docs/screenshots and README guidance still pending (next up: SourceLink setup notes + preview dialog walkthrough).

## Task 1 Findings (updated)
- `DiagnosticsToolsSample.pdb` enumerates both `.axaml` and code-behind documents; sequence points reference real `.axaml` lines (confirmed via `tmp/PdbInspector`).
- `DiagnosticsToolsSample.sourcelink.json` maps workspace roots (DiagnosticsTools repo + Avalonia + Dock externals) to GitHub raw URLs, providing remote fallback for navigation.
- Runtime probe (`DIAGNOSTICS_PROBE=1 dotnet run samples/DiagnosticsToolsSample`) surfaces `ValueStoreDiagnostic` frames for `Window`, `Button`, etc.; `Frame.Source` resolves to `ControlTheme[Type]` or `Style[selector]`, but no document/line metadata is exposed on the object itself.
- `ValueStoreDiagnostic.AppliedFrames` is marked `[PrivateApi]` in Avalonia 11.x; compile-time access fails so reflection is required—service API must tolerate shape changes across Avalonia releases.
- Template frames reference the owning type (`DiagnosticsToolsSample.MainWindow`) rather than the backing `.axaml` URI; resolving to actual documents will rely on PDB sequence points for the generated class.
- Dynamic resource setters surface as `DynamicResourceExtension` instances without URI data; resolver needs to chase resource keys back through merged dictionaries (likely via the resource scope stack captured in parent providers).
- Outstanding gap: previewing remote theme content still depends on SourceLink availability; Fluent `ControlTheme` instances are currently mapped via GitHub raw URLs when symbol data is missing.
- `PortablePdbResolver` uses `System.Reflection.Metadata` to map CLR members back to `.axaml` documents (preferring them over `.cs`) and honors SourceLink entries when local files are missing.
- SourceLink JSON parsing normalizes wildcard patterns and returns remote URIs; caching/invalidation work remains future work.

## Task 2: `ISourceInfoService` Implementation Notes
- **SourceInfo Contract**: `SourceInfo` stores local path, remote URI, span, and origin; `DisplayPath` prefers local files while falling back to remote URLs.
- **Service Surface**: Current interface supports `AvaloniaObject`, `MemberInfo`, and value-frame diagnostics. Property-level resolution is deferred until needed.
- **Resolver Pipeline**:
	- `PortablePdbResolver` locates sequence points, handles embedded PDBs, and rewrites documents through SourceLink maps.
	- Fluent control themes fall back to deterministic GitHub raw URLs when symbol data is absent.
	- Value frame resolution attempts the frame source, owning control, then declaring type.
- **Caching/Ownership**: Resolver instances are cached per assembly path and disposed with the service to avoid file locks.
- **Threading**: Asynchronous lookups marshal updates onto the UI thread before mutating observable state in view models.
- **Customization Hooks**: `DevToolsOptions` exposes setters for replacing the service or navigator, enabling host applications to inject custom navigation flows.
