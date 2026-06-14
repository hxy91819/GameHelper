# feat/in-house-tray Branch Pause Note

## Status
**Paused, not merged into main.**

## Exploratory Implementation

This branch attempted to add system tray icon support for GameHelper.ConsoleHost:

- TrayIconService - Windows Forms NotifyIcon-based tray service
- ConsoleWindowHelper - P/Invoke wrappers for hiding/showing console window
- HideSmokeTest / TrayIconSmokeTest - automated smoke tests
- --tray launch argument via ArgumentParser
- Program.cs startup flow for tray mode

## Why Paused

### Core Problem: Console app to system tray is architecturally unsound

GameHelper.ConsoleHost is a Console Application (OutputType = Exe). Its runtime model is inherently tied to a visible console window:

1. **Process type constraint**: Console processes get a console host (conhost.exe) on launch. Hiding the window via P/Invoke does not remove this host; the process cannot structurally become a windowless tray app.

2. **Output redirection loss**: Once the console window is hidden, all Console.WriteLine / stdout / stderr output loses its visible target. This makes logs and exceptions invisible to users.

3. **Message loop conflict**: Tray icons need an STA thread with Application.Run() message loop (this branch attempted a dedicated thread). But Console app main threads typically have no message pump, and IHost.RunAsync() lifecycle management conflicts with WinForms.

4. **Fundamentally different launch models**:
   - Tray app should be WinExe (no console window)
   - ConsoleHost is Exe (has console window)
   - Supporting both in one program requires complex dual-mode startup (e.g., stub process + console detach), far beyond scope.

### Refactoring Cost

To make ConsoleHost truly tray-capable:
- Change project OutputType to WinExe, or create a new WinUI/WinExe shell project
- Rebuild logging to output to file or UI panel in WinExe mode
- Rewrite startup flow to separate Console vs Tray lifecycles
- Coordinate IHostedService (Worker, IPC Server) with WinForms message loop

These changes touch the **core architecture**, not a standalone feature module.

## Conclusion & Next Steps

The patchwork tray approach on ConsoleHost architecture leads to unacceptable complexity, hidden windows that are not truly hidden, lost output, and messy lifecycle management.

Recommended approaches if tray support is needed later:
- **Option A**: Create a new GameHelper.TrayHost or GameHelper.WinUI project running as WinExe, referencing core logic as services.
- **Option B**: Keep ConsoleHost unchanged; use third-party tools like Traymond to minimize the existing console window to tray.

This branch is retained as a technical exploration record.

