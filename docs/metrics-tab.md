# Metrics Tab Plan

## Goals
- Surface Avalonia performance metrics and activities directly in DevTools to aid runtime diagnostics.
- Visualize histogram-based timings (composition, layout, input) with live updates and historical snapshots.
- Track observable up/down counters (UI event handlers, visuals, dispatcher timers) to highlight resource churn.
- Display diagnostic activities (styles, layout, hit-testing) with filtering and duration statistics.
- Provide an extensible pipeline that can grow with future metrics without restructuring the UI.

## Instrumentation Overview
- Avalonia publishes metrics via `System.Diagnostics.Metrics` under the default `Meter` namespace (`Avalonia.*`).
- Histograms: `avalonia.comp.render.time`, `avalonia.comp.update.time`, `avalonia.ui.measure.time`, `avalonia.ui.arrange.time`, `avalonia.ui.render.time`, `avalonia.ui.input.time`.
- Observable gauges/up-down counters: `avalonia.ui.event.handler.count`, `avalonia.ui.visual.count`, `avalonia.ui.dispatcher.timer.count`.
- Activities exposed through `System.Diagnostics.ActivitySource`: `Avalonia.AttachingStyle`, `Avalonia.FindingResource`, `Avalonia.EvaluatingStyle`, `Avalonia.MeasuringLayoutable`, `Avalonia.ArrangingLayoutable`, `Avalonia.PerformingHitTest`, `Avalonia.RaisingRoutedEvent`.

## Data Collection Strategy
- Introduce a `Diagnostics/Metrics` namespace with:
  - `MetricsListenerService` wrapping a `MeterListener` and `ActivityListener`.
  - Aggregation records (e.g., `HistogramStats`, `ObservableGaugeSnapshot`, `ActivitySample`).
- Configure the `MeterListener` to listen to meters with name prefix `Avalonia`.
  - For histograms, accumulate rolling windows (e.g., last 120 samples) with min/max/avg/p95 computations.
  - For observable instruments, store the latest sample plus a short history for sparkline rendering.
- Hook an `ActivityListener` to `ActivitySource` names matching `Avalonia.*`.
  - Capture start/end timestamps, duration, tags, and parent linkage.
  - Maintain bounded queues (ring buffers) per activity type to constrain memory usage.
- Ensure collection occurs on the UI thread via `Dispatcher.UIThread.Post` or `SynchronizationContext` to avoid cross-thread updates into view models.

## View Models
- Add `MetricsPageViewModel : ViewModelBase` exposing:
  - `ObservableCollection<HistogramMetricViewModel>` for histogram metrics.
  - `ObservableCollection<GaugeMetricViewModel>` for counters.
  - `ActivityTimelineViewModel` encapsulating recent activities with filtering support.
  - Commands for clearing history, pausing capture, and exporting snapshots, each wired to `MetricsListenerService` state.
  - Raise `SnapshotRequested` events that wrap `MetricsSnapshotService` output so views can opt into clipboard serialization.
- Use `ReactiveCommand`/`ReactiveObject` patterns consistent with existing view models (`TreePageViewModel`).
- Provide dependency injection via `MainViewModel` constructor: instantiate `MetricsListenerService`, subscribe to data feeds, and dispose them when DevTools closes.
- Implement throttled update batches (e.g., coalesce within 100ms) to reduce UI churn.

## UI/UX Design
- Create `MetricsPageView.axaml` featuring:
  - A `TabControl` or `Expander` layout with three panels: "Timing Histograms", "Resource Counts", "Activity Timeline".
  - For histograms: list each metric with current stats, sparkline chart (use `Path` or simple bar segments), and toggle for logarithmic scaling.
  - For counters: show current value, delta, min/max in session, and optional threshold coloring (warning when exceeding user-defined value).
  - Activity timeline: virtualized list showing activity name, duration, associated control/type, start time; allow filtering by text and minimum duration.
  - Toolbar actions (pause, resume, clear, export JSON/CSV).
- Adopt consistent styling with the rest of DevTools (brush resources, typography).
- Provide tooltip help describing each metric and how to interpret it.

## DevTools Integration
- Extend `DevToolsViewKind` enum with `Metrics` and update `DevToolsOptions.LaunchView` docs/comments.
- Update `MainViewModel` to create `_metrics` view model and include it in `SelectedTab` switch.
- Insert a new `TabStripItem` labeled "Metrics" in `MainView.xaml`; adjust tab indices accordingly.
- Ensure context menu and keyboard shortcuts remain unaffected by the additional tab.
- Wire the new page into `MainWindow` selection hand-off; when switching to metrics tab, start listener if not already running.

## Persistence & Export
- Implement `MetricsSnapshotService` to serialize current aggregations to JSON for bug reports.
- Store session statistics only in-memory; avoid disk persistence by default to limit privacy concerns.
- Optionally allow CSV export of histogram stats for offline analysis.

## Testing & Validation
- Add unit tests to verify listener registration, histogram aggregation logic, and bounded history behavior. Include gauge history deltas or tab hotkey flows in follow-up coverage if shortcuts evolve.
- Mock `Meter` and `ActivitySource` emissions in tests to simulate rapid updates and ensure UI throttling works.
- Create an integration test to confirm `DevToolsOptions.LaunchView = Metrics` selects the new tab and starts listeners.
- Manually validate with a sample Avalonia app using `DiagnosticsToolsSample` by generating activity and verifying live updates.

## Documentation & Follow-Up
- Link this plan from `README.md` or a docs index.
- After implementation, include usage notes and screenshots describing the metrics tab capabilities.
- Monitor upstream Avalonia changes to keep metric names aligned with future revisions.

## Implementation Checklist
1. [x] Implement `MetricsListenerService` with meter and activity listeners plus aggregation models.
2. [x] Build view models (`MetricsPageViewModel`, metric item VMs, activity timeline) and wire throttled updates.
3. [x] Create `MetricsPageView.axaml` UI with histogram, counter, and activity panels aligned to DevTools styling.
4. [x] Extend `DevToolsViewKind`, `DevToolsOptions`, and `MainViewModel/MainView` to register the Metrics tab.
5. [x] Add pause/clear/export command handling and optional snapshot serialization support.
6. [x] Write unit tests covering listener registration, aggregation logic, and tab activation scenarios.
7. [ ] Validate manually with `DiagnosticsToolsSample` to confirm live metric updates and UI responsiveness.
8. [ ] Update repository documentation and screenshots to showcase the Metrics tab once implemented.
