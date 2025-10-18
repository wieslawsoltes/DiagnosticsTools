# Diagnostics Status Panel Overview

## Architecture Highlights

- **External-first diagnostics** – DevTools now delegates heavy diagnostics and metrics rendering to the out-of-process monitor/viewer, keeping the host application responsive.
- **Configuration surface** – `DiagnosticsStatusViewModel` exposes ports, host, and channel toggles for the publisher, plus auto-launch preferences for the external tools, and the DevTools default tab now opens to this panel so out-of-process diagnostics are ready immediately.
- **Publisher lifecycle** – `MainViewModel` brokers `DiagnosticsPublisherOptions`, restarts the publisher when settings change, and gates auto-launch behaviour via the new toggles.
- **Status UX** – `DiagnosticsStatusView.axaml` surfaces connection controls, start/stop/restart buttons, quick launch shortcuts, and real-time status messaging inside the former metrics tab.
- **Compatibility** – Settings flow is plumbed through `DevToolsOptions.Diagnostics`, enabling host applications to preconfigure diagnostics without touching defaults.

## Key Components

| Area | Description |
| --- | --- |
| Configuration model | `DiagnosticsPublisherOptions` (host, ports, channel toggles) + `DevToolsOptions.Diagnostics` surface |
| DevTools UI | `DiagnosticsStatusViewModel`, `DiagnosticsStatusView.axaml`, launcher commands, auto-launch toggles |
| Publisher orchestration | `MainViewModel` start/stop/restart helpers, `AutoLaunchMonitor/Viewer`, startup telemetry guards |
| External tooling | `ExternalDiagnosticsLauncher` honours force launches, leverage viewer/monitor executables |

## Follow-up Considerations

- Surface publisher health counters (ingress rate, envelope drops) alongside the status message.
- Persist diagnostics preferences between sessions so host apps can remember custom port layouts.
- Provide quick links to open the external tools in “listen only” mode without starting the publisher.
- Extend the panel with a history list of recent connections once envelope versioning evolves.

## Implementation Checklist

1. [x] Move the DevTools metrics tab to the new diagnostics status panel + commands.
2. [x] Thread diagnostics settings through `DevToolsOptions` and `MainViewModel`.
3. [x] Gate `DiagnosticsPublisher` start/stop via the new settings and expose restart logic.
4. [x] Add UI affordances for manual external launches and publisher status feedback.
5. [x] Update automated coverage to validate tab wiring and option propagation.
