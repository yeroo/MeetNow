# Smart Autopilot Agent — Design Spec

## Overview

Replace MeetNow's hardcoded autopilot rules with an LLM-powered agent loop using OpenCode's serve API and GitHub Copilot models. The LLM observes Teams messages and calendar, decides when to go active/passive, and takes conservative actions (simulate typing, brief holding replies, status changes).

This is **Subsystem 1** of a larger vision that will later include remote commands (Subsystem 2), knowledge base accumulation (Subsystem 3), and daily secretary briefings (Subsystem 4).

## Architecture

### Core Component: AutopilotAgent.cs

A static class that manages:
- **OpenCode serve process lifecycle** — checks if `opencode serve` is already running on the configured port, launches it if not, monitors health, restarts on crash
- **Persistent OpenCode session** — creates one session on first cycle, reuses it across all cycles so the LLM accumulates context. Session resets when autopilot is manually toggled off then on.
- **Cycle triggers** — event-driven (new message → immediate, debounced 30s) + background timer (every 5 minutes for calendar transitions)
- **Context injection** — each cycle prepends current time, day, mode, last actions, and any user instructions before sending to OpenCode

### Modes

- **Passive** (default on startup): LLM observes and logs what it *would* do, but takes no actions. Builds context.
- **Active**: LLM can execute actions — simulate typing, send messages, change availability, etc.
- The LLM itself decides when to transition between modes based on calendar, time, personal schedule, and user instructions.
- Manual toggle always available via existing UI button or MCP tool.

### Cycle Flow

```
Trigger (message event / timer / manual)
    ↓
AutopilotAgent.TriggerCycle()
    ↓
Build context message:
    Time: 14:32 Thursday 2026-03-27
    Mode: active
    Last actions: simulated typing to Agarwal (2 min ago)
    User instruction: "Taking wife to doctor, back ~3pm"

    Check messages and calendar. Decide what to do.
    ↓
POST /session/{sessionId}/message  →  OpenCode serve API
    ↓
LLM calls MCP tools (get_messages, get_meetings, etc.)
LLM decides and calls action tools (simulate_typing, send_message, etc.)
    ↓
Response logged to AutopilotChatWindow
```

### OpenCode Serve Management

- On app startup, `AutopilotAgent.Start()` is called
- Checks `http://localhost:{OpenCodePort}/global/health`
- If not responding, spawns `opencode serve --port {port}` as a background process
- Health checks every 60 seconds; restart if unresponsive
- On app exit, does NOT kill the serve process (it may be shared with other uses)
- If launch fails (opencode not installed), logs warning and disables autopilot agent gracefully

### Session Management

- `POST /session` with system prompt on first cycle → stores session ID
- All subsequent cycles use `POST /session/{id}/message`
- Session is persistent across cycles — the LLM remembers previous decisions
- Session resets when: autopilot manually toggled off then on, or daily at midnight
- If session errors (deleted, expired), create a new one automatically

## System Prompt

Stored in `.opencode/commands/autopilot-system.md` (editable by user):

```markdown
You are Boris's Teams autopilot. You run continuously and manage his presence.

## Tools available
You can call MCP tools: get_messages, get_meetings, get_contacts,
get_favorites, get_contact_priorities, get_status, set_availability,
send_message, simulate_typing, enable_autopilot, disable_autopilot,
set_contact_priority

## Modes
- Passive: observe, log what you would do, but don't act
- Active: you may act (simulate typing, reply, change status)
- You decide when to switch modes based on context
- Call enable_autopilot / disable_autopilot to switch

## Rules
- ALWAYS reply in English
- Be conservative: simulate typing first, wait a few minutes, only then consider replying
- Only send brief holding messages ("In a meeting, will follow up shortly")
- Never share technical details or commit to deadlines on Boris's behalf
- Never send substantive replies — only acknowledgments and time estimates
- Favorite and Urgent contacts get faster responses
- Normal contacts: typing indicator only unless they message 3+ times
- Low priority contacts: no action

## Schedule
- After 6pm (7pm on Fridays): set Away and disable autopilot
  - UNLESS an accepted meeting is still running — wait until it ends
- Boris walks his dog ~10-11am — consider going active during this time
- Respect Boris's manual instructions above all rules

## Response format
After each cycle, briefly report what you did and why (1-2 sentences).
```

## Chat Panel (AutopilotChatWindow)

A WPF window accessible from the system tray context menu:

- **Input area** (bottom): Text box + Send button. User types instructions like "Taking wife to doctor, back ~3pm" or "Ignore messages from John today"
- **Log area** (top, scrollable): Shows timestamped entries of:
  - User instructions (marked as "You:")
  - LLM decisions and actions (marked as "Autopilot:")
  - Mode transitions ("Autopilot went active — meeting detected")
- Instructions are stored in a `List<string>` and included in every cycle's context message until cleared
- A "Clear instructions" button resets standing instructions

The chat input sends the message directly to the OpenCode session via `POST /session/{id}/message`, so the LLM sees it immediately and can act on it.

## New MCP Tools

### `enable_autopilot`

Switch to active mode. Shows red border overlay, allows the LLM to execute actions.

**Parameters:** None
**Returns:** `{ "success": true, "mode": "active" }`
**Execution:** `AutopilotOverlay.Enable()` (via UI dispatcher)

### `disable_autopilot`

Switch to passive mode. Hides border overlay, LLM can only observe.

**Parameters:** None
**Returns:** `{ "success": true, "mode": "passive" }`
**Execution:** `AutopilotOverlay.Disable()` (via UI dispatcher)

### `send_instruction`

Inject an instruction into the autopilot session. Used by remote clients or other tools.

**Parameters:**
- `message` (string, required) — instruction text

**Returns:** `{ "success": true }`
**Execution:** Adds to instruction list, triggers an immediate cycle

## Settings Additions

```csharp
// Smart Autopilot
public string AutopilotModel { get; set; } = "copilot/claude-sonnet-4-6";
public int OpenCodePort { get; set; } = 4096;
public int AutopilotCycleIntervalSeconds { get; set; } = 300;
public int AutopilotMessageDebounceSeconds { get; set; } = 30;
```

## Changes to Existing Code

### AutopilotOverlay.cs

- Keeps its UI role: red border, "Autopilot OFF" button, auto-off timer
- Removes all message tracking logic (`TrackUrgentMessage`, `_urgentMessageCounts`, `_pendingReplies`, `ScheduleAutoReplyAsync`) — this is now the LLM's job
- `Enable()` / `Disable()` still toggle the overlay UI and set Teams status
- `AutopilotAgent` calls these methods; the LLM calls them via MCP tools

### MainWindow.xaml.cs

- Add "Autopilot Chat" menu item to system tray context menu
- Wire `MessageHistory.Add()` to also call `AutopilotAgent.TriggerCycle()` (debounced)

### McpServer.cs

- Add `enable_autopilot`, `disable_autopilot`, `send_instruction` to `DispatchTool` and `GetToolDefinitions`

### App.xaml.cs

- Call `AutopilotAgent.Start()` on startup
- Call `AutopilotAgent.Stop()` on exit

## File Changes Summary

| File | Change |
|------|--------|
| `MeetNow/AutopilotAgent.cs` | New — OpenCode serve management, session lifecycle, cycle triggers |
| `MeetNow/AutopilotChatWindow.xaml` | New — chat panel UI (input + log) |
| `MeetNow/AutopilotChatWindow.xaml.cs` | New — code-behind for chat window |
| `.opencode/commands/autopilot-system.md` | New — system prompt for the LLM |
| `MeetNow/McpServer.cs` | Add 3 MCP tools |
| `MeetNow/AutopilotOverlay.cs` | Remove hardcoded message tracking logic, keep UI |
| `MeetNow/MeetNowSettings.cs` | Add autopilot settings |
| `MeetNow/MainWindow.xaml.cs` | Add chat menu item, wire message trigger |
| `MeetNow/App.xaml.cs` | Start/stop AutopilotAgent |

## Constraints

- No new NuGet packages — use `HttpClient` for OpenCode serve API calls
- OpenCode serve is localhost-only, no auth needed (same machine)
- If OpenCode is unavailable, autopilot degrades gracefully (logs warning, no actions)
- All actions still go through `TeamsOperationQueue` for sequential execution
- System prompt is a user-editable file, not hardcoded
