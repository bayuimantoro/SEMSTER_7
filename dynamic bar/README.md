# IslandWin — Dynamic Island for Windows 11

A floating, always-on-top pill UI inspired by the iPhone Dynamic Island, built with C# / WPF on .NET 6 (upgrade to .NET 8 by changing TFM when SDK is available).

## Implementation Status

### ✅ Stage 1: Minimal Working Pill (Current)
- Floating, borderless, transparent, always-on-top pill
- Centered horizontally near the top of primary display
- DPI-aware positioning across screen resolutions
- Smooth expand/collapse animations (cubic easing)
- Compact mode with clock and status indicator
- Expanded mode with placeholder media layout
- No focus-stealing (WS_EX_NOACTIVATE)
- System tray icon with Show/Hide and Exit
- Does not appear in Alt+Tab (WS_EX_TOOLWINDOW)

### 🔲 Stage 2: Media Session Integration
- Windows media session detection (SystemMediaTransportControls)
- Spotify desktop support
- Microsoft Edge / YouTube support
- Play/pause, next/previous track controls
- Album art / thumbnail display

### 🔲 Stage 3: System Monitoring
- CPU, RAM, Battery, GPU usage

### 🔲 Stage 4: Lyrics Panel
- LRC file loading
- Synchronized lyrics display
- Provider interface

### 🔲 Stage 5: Settings & Polish
- Settings window
- Startup with Windows option
- Publish script

## Requirements
- Windows 11 x64
- .NET 8.0 SDK

## Build & Run
```powershell
cd "dynamic bar"
dotnet restore
dotnet run
```

## Known Limitations (Stage 1)
- Media controls are non-functional placeholders
- No actual media session integration yet
- No settings window yet
- Uses system icon for tray (no custom icon)
