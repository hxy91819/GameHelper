# Active Branch Status

## Current WIP Branch: codex/fix-monitor-control-service-state

### Purpose
Fixes MonitorControlService state management issues and ETW ProcessMonitor stop-event path resolution bug.

### Committed (3 commits)

| Commit | Description |
|--------|-------------|
| 1aed39c | fix(core): make MonitorControlService.Start/Stop exception-safe |
| 4fa2fb6 | fix(infra): cache process path in EtwProcessMonitor to fix stop-event matching |
| f1a6c60 | fix(winui): sync ShellViewModel.IsMonitorRunning to actual service state |

### Uncommitted WIP

- GameHelper.Tests/EtwSessionCleanupFixture.cs - new: ETW session cleanup test fixture
- GameHelper.Tests/EtwProcessMonitorTests.cs - new test cases
- GameHelper.Tests/EndToEndProcessMonitoringTests.cs - adjustments
- GameHelper.Tests/ProcessMonitorIntegrationTests.cs - adjustments

Status: in progress, not yet committed.


---

## Retained Branch: feat/in-house-tray

### Status
Paused, not merged.

### Why Paused
Console app to system tray is technically infeasible:
- Console process binds to conhost.exe; cannot become a windowless tray app
- Hiding the window makes stdout/stderr output invisible
- WinForms message loop conflicts with IHost.RunAsync() lifecycle
- Requires converting to WinExe shell project; touches core architecture

Detailed analysis: see docs/todo/tray-pause.md on the branch.

### Exploratory Implementation
- TrayIconService (NotifyIcon)
- ConsoleWindowHelper (P/Invoke window control)
- SmokeTest automation
- --tray launch parameter

---

## Remote Branches Awaiting Cleanup (already merged into main)

Safe to delete:

- origin/bugfix/name-match
- origin/codex-6ed2gm
- origin/codex/add-configuration-for-auto-select-monitoring
- origin/codex/add-configuration-for-auto-select-monitoring-l593pv
- origin/codex/add-interactive-command-line-features-r4qzdr
- origin/codex/add-numbering-for-quick-menu-selection
- origin/codex/build-and-publish-v0.0.1-exe-to-github-releases
- origin/codex/change-exit-command-for-monitoring
- origin/codex/enable-instant-input-for-numbers
- origin/codex/feat-cli-speed-control
- origin/codex/implement-hdr-support-detection-feature
- origin/codex/initialize-agents.md-for-project
- origin/codex/outline-codebase-for-newcomers
- origin/codex/remove-confirmation-for-starting-monitoring
- origin/codex/update-showstatistics-for-user-prompt
- origin/feat/add-ui
- origin/fix/config-add-validation

