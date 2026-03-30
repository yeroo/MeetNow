# Recorder Tray UI Design

**Date:** 2026-03-30
**Status:** Approved

## Overview

Convert MeetNow.Recorder from a console app to a WPF tray application with a system tray icon, recording status indicators, and a full status dashboard with audio playback and transcript sync.

## Architecture

Convert `MeetNow.Recorder` project in-place. Recording logic stays in `MeetNow.Recording.Core`, transcription stays in the Python transcriber process.

**Project changes to MeetNow.Recorder.csproj:**
- OutputType: `Exe` → `WinExe`
- Add `Hardcodet.NotifyIcon.Wpf` dependency
- Add WPF SDK (`UseWPF = true`)

**New files:**
- `App.xaml` / `App.xaml.cs` — WPF app lifecycle, single-instance mutex
- `TrayIcon.xaml` — tray icon with context menu
- `StatusWindow.xaml` / `StatusWindow.xaml.cs` — master/detail dashboard
- `RecorderViewModel.cs` — bridges RecorderService state → UI via INotifyPropertyChanged
- Icons: `icon-idle.ico`, `icon-recording.ico` (with red dot overlay badge)

**RecorderService changes:**
- Replace `UpdateConsoleTitle()` with events/observable properties
- Add `AudioLevelChanged(float loopbackRms, float micRms)` event for live meters
- Expose `RecorderState` as observable property
- Service stays headless — all UI in the WPF layer

## Tray Icon Behavior

**Icon states:**
- **Idle** — normal MeetNow icon, tooltip: "MeetNow Recorder — Idle"
- **Recording** — same icon with small red dot overlay badge, tooltip: "MeetNow Recorder — Recording (chunk 5, 00:12:34)"
- **Transcribing** — same icon with small orange dot overlay, tooltip: "MeetNow Recorder — Transcribing 3/8 chunks"

**Context menu (right-click):**
- Start Recording / Stop Recording (toggles based on state)
- Open Status Window
- Separator
- Exit

**Double-click:** Opens/activates the Status Window.

**Startup behavior:**
- App starts minimized to tray (no window shown)
- Recording starts automatically on launch
- Single-instance enforced via named Mutex

## Status Window — Master/Detail Layout

### Left Pane (Session List)

Sessions listed newest-first, each row shows:
- Status icon: red dot (recording), orange dot (transcribing), green checkmark (done), red X (failed)
- Date/time: `Mar 30, 09:16`
- Duration: `00:45:23`
- Transcription progress for active transcription: `3/8 chunks`

Disk usage shown at bottom of the list pane.

### Right Pane (Detail)

Content changes based on selected session's state:

**Active recording session:**
- Header: recording indicator, elapsed time, chunk count
- Audio level meters: two horizontal bars (Loopback / Mic), updated ~10 times/sec
- Live transcript area: chunks shown as placeholders when recorded, text fills in as transcriber completes each chunk. Auto-scrolls to bottom.

**Completed session:**
- Header: session date, duration, chunk count, speaker count
- Audio player bar: play/pause, seek slider, time position, channel toggle (Loopback / Mic / Both)
- Transcript view: scrollable text with timestamps, speaker labels (Person 1, Person 2, Me) color-coded. Click any segment to seek audio to that timestamp.
- Action buttons: Open Transcript, Open Folder, Retry Transcription, Delete Session

**Transcribing session:**
- Same as completed but audio player disabled
- Transcript shows completed chunks with placeholders for pending ones
- Progress bar showing X/Y chunks done

**Failed session:**
- Shows error message
- Retry Transcription button prominent

## Audio Player & Transcript Sync

**Audio playback (NAudio):**
- Channel toggle: three buttons — Loopback / Mic / Both
  - Loopback: plays `chunk_*_loopback.wav` files in sequence
  - Mic: plays `chunk_*_mic.wav` files in sequence
  - Both: mixes both channels into stereo (loopback left, mic right)
- Standard transport: Play/Pause, seek slider, current time / total time
- Chunks played seamlessly in sequence using timeline from chunk metadata

**Transcript → audio sync:**
- Each transcript segment has `start`/`end` timestamps from Whisper
- Click a segment → audio seeks to that segment's start time
- During playback, currently-playing segment is highlighted
- Auto-scroll follows the highlighted segment

**Audio → transcript sync:**
- Position timer (100ms interval) finds current segment by timestamp
- Highlights matching segment and scrolls into view
- Seek slider drag also updates highlighted segment

## Data Flow & State Updates

**RecorderService → UI:**
- RecorderService runs on background thread, fires events:
  - `StateChanged(RecorderState)` — Idle/Recording/Draining
  - `AudioLevelChanged(float loopbackRms, float micRms)` — from audio frames
  - `ChunkFlushed(int chunkIndex, string sessionId)` — new chunk written
  - `SessionStarted(string sessionId)` / `SessionCompleted(string sessionId)`
- RecorderViewModel subscribes, marshals to UI thread via `Dispatcher.Invoke`

**Session/transcript discovery:**
- ViewModel scans `%LOCALAPPDATA%\MeetNow\Recordings\` on startup
- Each session directory → SessionViewModel with data from `session.json`
- `FileSystemWatcher` on recordings directory detects:
  - New sessions (new subdirectory)
  - Transcript completion (new `.json` in `transcripts/`)
  - Session status changes (`session.json` updates)
- Chunk metadata files are source of truth for transcription progress

**Transcriber process:**
- Still launched as separate Python process by `TranscriberProcessManager`
- No changes to transcriber protocol
- UI discovers results via FileSystemWatcher, not via the transcriber process

## Error Handling & Edge Cases

**Audio device changes:**
- Existing `DeviceMonitor.OnDefaultDeviceChanged` handling stays
- UI shows brief notification: "Audio device changed — capture reinitialized"
- Audio meters reset and resume

**Transcriber failures:**
- Individual chunk failures shown as red placeholder: "Chunk 3 — transcription failed: [error]"
- Retry Transcription resets failed chunks to `pending_transcription`, relaunches transcriber
- If transcriber process crashes, status window shows "Transcriber stopped" with Restart button

**Disk space:**
- Existing periodic check (10 min) stays
- Low disk: yellow warning bar at top of status window
- Critical disk: stops recording, red bar

**Window lifecycle:**
- Close button hides to tray (doesn't exit)
- Exit only via tray menu → "Exit" (confirmation if recording active)
- Window position/size remembered between opens
