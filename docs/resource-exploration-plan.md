# Resource, Template, and Style Diagnostics Plan

## 1. Milestone: Baseline Research and UX Definition

### 1.1 Task: Survey Avalonia Diagnostics Capabilities
Document current resource/style inspection features in `extern/Avalonia/src/Avalonia.Diagnostics`, focusing on APIs that expose `ResourceDictionary`, `IDataTemplate`, and `IStyle` metadata.

### 1.2 Task: Benchmark External Tooling
Compile comparative notes from WPF Live Visual Tree, WinUI XAML diagnostics, and other mature tooling to identify high-value inspection patterns for resources, templates, and styles.

### 1.3 Task: Draft UX Artifacts
Produce wireframes or annotated mock-ups for tree integration and dedicated tab views, highlighting navigation, selection states, and detail panes.

## 2. Milestone: Data Acquisition Layer

### 2.1 Task: Define Data Contracts
Specify view-model DTOs for resources, data templates, and styles (keys, target types, scopes, origins, and mutability flags).

### 2.2 Task: Implement Resource Harvesters
Create services that walk logical/visual ancestry to collect local/global dictionaries, merged dictionaries, theme dictionaries, and implicit styles.

### 2.3 Task: Add Template and Style Resolvers
Hook into Avalonia diagnostics hooks to enumerate `IDataTemplate` and `IStyle` instances, resolving associated controls and source descriptors when available.

### 2.4 Task: Cache and Refresh Strategy
Design caching with invalidation triggers (hot reload, theme change, resource updates) to keep diagnostics responsive without stale data.

## 3. Milestone: Integrated Tree Presentation

### 3.1 Task: Extend Existing Tree Nodes
Introduce optional child groups under combined/logical/visual nodes to surface scoped resources, data templates, and styles.

### 3.2 Task: Visual Design Alignment
Create icons, colors, and typography variants aligned with Fluent Compact theme to distinguish between resources, templates, and styles in-tree.

### 3.3 Task: Interaction Enhancements
Implement context actions (copy key, navigate to owner control, open source) and hover tooltips with summarized metadata.

## 4. Milestone: Dedicated Resource-Oriented Tabs

### 4.1 Task: Build Resource Tab View Models
Provide virtualized trees filtered by resource scope (application, control, theme), with search and grouping controls.

### 4.2 Task: Build Data Template Tab View Models
List templates with usage counts, applied controls, and triggers; enable navigation back to originating controls.

### 4.3 Task: Build Styles Tab View Models
Show active/inactive styles, selectors, setters, and conflicts; support toggling visual emphasis of applied styles.

### 4.4 Task: Detail Pane Implementation
Create shared detail pane component presenting setters, bindings, triggers, and source file hints for the selected item.

## 5. Milestone: Commanding and Navigation

### 5.1 Task: Cross-View Navigation Hooks
Enable jumping between resource tab entries and corresponding nodes in combined/logical/visual trees as well as focus in the running app.

### 5.2 Task: Clipboard and Export Commands
Add copy-as-selector/key operations and optional JSON/XAML export of resource hierarchies for debugging.

### 5.3 Task: Diagnostics Overlay Integration
Leverage existing highlight adorners to visually outline controls or regions impacted by the selected template/style.

## 6. Milestone: Performance, Testing, and Documentation

### 6.1 Task: Performance Validation
Profile resource aggregation on large applications, adding telemetry/logging to flag expensive operations and caching misses.

### 6.2 Task: Automated Test Coverage
Create headless tests verifying resource enumeration, scope filtering, selection persistence, and command behaviors.

### 6.3 Task: Documentation and Adoption Plan
Update DevTools documentation with usage guides, troubleshooting, and comparison to legacy tooling; outline preview rollout and feedback channels.
