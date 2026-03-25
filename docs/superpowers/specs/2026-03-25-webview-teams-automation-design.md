# WebView-based Teams Automation Design

## Problem

TeamsStatusManager uses Win32 P/Invoke to automate the real Teams desktop app — `SetForegroundWindow`, `SendKeys`, mouse clicks at calculated pixel coordinates. This steals focus, is fragile to window layout changes, requires Teams desktop to be running, and is visible to the user.

## Solution

Replace Win32 automation with DOM automation on a dedicated offscreen WebViewInstance ("TeamsAutomation") that navigates to `teams.microsoft.com`. All interactions happen via JavaScript — no focus stealing, no pixel math, no Win32 dependencies.

## Architecture

### New WebViewInstance: TeamsAutomation

- Third persistent instance in WebViewManager (alongside MessageMonitor and CalendarMonitor)
- Navigates to `teams.microsoft.com` on startup
- Shares cookies/auth via the common `WebView2Profile` user data folder
- Offscreen (-10000,-10000) — completely invisible

### Shortcut Discovery

Teams web uses different keyboard shortcuts than the desktop app (e.g. `Ctrl+Alt+E` instead of `Ctrl+E` for search). Before executing any automation, run a one-time discovery after Teams web loads:

1. Scan the left rail buttons — read `aria-label`, `accesskey`, and tooltip text containing keyboard shortcuts (e.g. "Chat Ctrl+Alt+3")
2. Find the search box — read placeholder/tooltip for actual shortcut
3. Store discovered shortcuts in a dictionary: `{"Search": "Ctrl+Alt+E", "Chat": "Ctrl+Alt+3", ...}`
4. Use discovered shortcuts in all automation instead of hardcoded values
5. Re-discover after page reload (shortcuts may change with Teams updates)

### Operations

**1. Set Status**

- Use discovered search shortcut to focus the search box via JS keyboard event dispatch
- Type slash command (`/available`, `/busy`, `/away`, `/dnd`, `/brb`) into the search input via DOM manipulation
- Press Enter via keyboard event
- Wait for confirmation, then clear search

**2. Simulate Typing**

- Navigate the TeamsAutomation instance to `https://teams.microsoft.com/l/chat/0/0?users=8:orgid:{GUID}` (direct chat URL using Teams user ID from ContactDatabase)
- Wait for compose box to appear in DOM
- Type "Hi" via JS input events on the compose element
- Hold for configured duration (`MeetNowSettings.SimulateTypingDurationSeconds`)
- Clear with select-all + delete (never send)
- Navigate back to Teams home or stay on chat

**3. Send Message**

- Same as simulate typing: navigate to direct chat URL
- Type message via JS input events
- Press Enter via keyboard event dispatch (sends the message)

### What Changes

| File | Change |
|------|--------|
| `TeamsStatusManager.cs` | Gut Win32 P/Invoke code, replace with WebViewManager JS calls |
| `WebViewManager.cs` | Add `_teamsAutomation` persistent instance, `StartTeamsAutomationAsync()` |
| Win32 P/Invoke | Remove `FindWindow`, `SetForegroundWindow`, `SendKeys`, `mouse_event`, `SetCursorPos`, thread attachment |

### What Stays the Same

| File | Reason |
|------|--------|
| `TeamsOperationQueue.cs` | Queue mechanism unchanged — just the operation implementations swap |
| `AutopilotOverlay.cs` | Enqueues operations the same way |
| `MainWindow.xaml.cs` | Triggers operations the same way |
| `QueueOverlayWindow.cs` | Displays progress the same way |
| `MeetNowSettings.cs` | Same settings (typing duration, cooldown, etc.) |

### Safety Rules

- **No speculative API calls** — all interactions are DOM automation (clicking, typing in UI elements)
- **No fetch() with tokens** — same constraint as all other WebView tasks
- **Auth redirect detection** — if Teams web redirects to login, back off and log warning
- **Operation timeout** — 30s max per operation (same as current queue timeout pattern)

### Key Benefits

- No focus stealing — completely invisible/offscreen
- No Win32 dependencies — pure JS
- No coordinate calculations — DOM selectors instead of pixel math
- Works even if Teams desktop isn't running
- Resilient to Teams UI updates via shortcut discovery

### File Structure

```
MeetNow/
  WebViewManager.cs          (modify — add TeamsAutomation instance)
  TeamsStatusManager.cs      (rewrite — DOM automation instead of Win32)
  Tasks/
    TeamsShortcutDiscovery.cs (new — discover keyboard shortcuts from DOM)
```
