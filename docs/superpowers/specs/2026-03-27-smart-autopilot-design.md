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

- `POST /session` creates a bare session (with `title: "MeetNow Autopilot"`) → stores session ID
- All cycles use `POST /session/{id}/message` with body:
  ```json
  {
    "parts": [{ "type": "text", "text": "<context + prompt>" }],
    "system": "<system prompt from autopilot-system.md>",
    "model": "copilot/claude-sonnet-4-6"
  }
  ```
- The `system` prompt is sent with every message (not stored at session level)
- Uses the synchronous `POST /session/{id}/message` endpoint (blocks until LLM finishes, typically 10-30s) — not `prompt_async`
- Session is persistent across cycles — the LLM remembers previous decisions
- Session resets when: autopilot manually toggled off then on, or when date changes (checked at each cycle start)
- If session errors (404 or error response), create a new one automatically
- If OpenCode restarts (detected via health check failure + recovery), stored session ID is invalidated and a new session is created on next cycle

### Cycle Concurrency

Only one cycle executes at a time. A `SemaphoreSlim(1, 1)` in `TriggerCycle()` ensures mutual exclusion:
- If a cycle is running when a new trigger fires, the trigger is coalesced (a flag is set to re-trigger after the current cycle completes)
- This prevents concurrent `POST /session/{id}/message` calls to the same session
- The chat panel's "Send" button also acquires the semaphore — user messages are queued behind any running cycle

### Error Handling

- **Auth failure / model not found:** Log the LLM error response, skip the cycle, retry next trigger. Do not crash.
- **Rate limiting:** If OpenCode returns a rate-limit error, back off the cycle timer to 10 minutes for the next 3 cycles.
- **Context window growth:** After 50 cycles in one session, reset the session (create a new one). The LLM will lose history but the system prompt provides all behavioral rules. User instructions carry over (they're stored in `AutopilotAgent`, not the session).

## System Prompt

Stored in `%LOCALAPPDATA%\MeetNow\autopilot-system.md` (editable by user). MeetNow reads this file at each cycle and passes it via the `system` field. A default is created on first run if the file doesn't exist:

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

## Efficiency
- You have memory of all previous cycles in this session. Do not re-fetch data that has not changed.
- Keep each cycle to 3-5 tool calls maximum.
- If nothing has changed since last cycle, just say "No action needed."

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

Switch to active mode. Sets a flag in `AutopilotAgent` allowing the LLM to execute action tools. Also shows the red border overlay via `AutopilotOverlay.Enable()`. Does NOT automatically change Teams status — the LLM should call `set_availability` separately if needed.

**Parameters:** None
**Returns:** `{ "success": true, "mode": "active" }`
**Execution:** `AutopilotAgent.SetMode(active)` + `AutopilotOverlay.Enable()` (via UI dispatcher). The `Enable()` call is modified to no longer set Teams status (that's the LLM's decision).

### `disable_autopilot`

Switch to passive mode. Hides border overlay. Does NOT change Teams status or clear the operation queue — the LLM should manage those explicitly if needed.

**Parameters:** None
**Returns:** `{ "success": true, "mode": "passive" }`
**Execution:** `AutopilotAgent.SetMode(passive)` + `AutopilotOverlay.Disable()` (via UI dispatcher). The `Disable()` call is modified to no longer set Teams status or clear the queue.

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

- Keeps its UI role: red border, "Autopilot OFF" button
- Removes all message tracking logic (`TrackUrgentMessage`, `_urgentMessageCounts`, `_pendingReplies`, `ScheduleAutoReplyAsync`) — this is now the LLM's job
- Removes auto-off timer (`StartAutoOffTimer`) — end-of-day logic is now the LLM's job via the system prompt
- `Enable()` / `Disable()` become UI-only: show/hide border overlay. No longer set Teams status or clear the operation queue — those are now the LLM's responsibilities via `set_availability` and explicit queue management
- `AutopilotAgent` calls these methods; the LLM calls them via MCP tools

### MainWindow.xaml.cs

- Add "Autopilot Chat" menu item to system tray context menu
- Remove the hardcoded `if (AutopilotOverlay.IsActive)` block in `OnTeamsMessageDetected` (lines 323-348) — this duplicates what the LLM now handles. Keep only `MessageHistory.Add(message)` followed by `AutopilotAgent.TriggerCycle()`
- Remove `ForwardUrgentIfEnabled` method (forwarding logic moves to LLM)

### McpServer.cs

- Add `enable_autopilot`, `disable_autopilot`, `send_instruction` to `DispatchTool` and `GetToolDefinitions`
- Update `ToolGetStatus` to include `autopilotMode` field ("active"/"passive") from `AutopilotAgent` instead of `pendingAutoReplies` (which is removed)

### QueueOverlayWindow.cs

- Remove references to `AutopilotOverlay.GetPendingAutoReplies()` (method no longer exists)

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
| `%LOCALAPPDATA%\MeetNow\autopilot-system.md` | New (created at runtime) — system prompt for the LLM |
| `MeetNow/McpServer.cs` | Add 3 MCP tools, update `ToolGetStatus` |
| `MeetNow/AutopilotOverlay.cs` | Remove message tracking, auto-off timer, status side effects — keep UI only |
| `MeetNow/MeetNowSettings.cs` | Add autopilot settings; obsolete: `SimulateTypingInAutopilot`, `AutoReplyHiInAutopilot`, `ForwardUrgentInAutopilot`, `AutoReplyDelayMinutes`, `AutoReplyMessageThreshold`, `AutopilotOffTime` (keep but unused) |
| `MeetNow/MainWindow.xaml.cs` | Remove hardcoded autopilot block in `OnTeamsMessageDetected`, add chat menu item, wire message trigger |
| `MeetNow/QueueOverlayWindow.cs` | Remove `GetPendingAutoReplies()` references |
| `MeetNow/App.xaml.cs` | Start/stop AutopilotAgent |

## OpenCode Configuration

OpenCode must be configured to know about the MeetNow MCP server. The stdio proxy is used (since SSE has compatibility issues with some clients):

**`%USERPROFILE%\.config\opencode\opencode.json`** must include:
```json
{
  "mcp": {
    "meetnow": {
      "type": "local",
      "command": ["dotnet", "run", "--project", "C:\\Users\\Boris.Kudriashov\\Source\\repos\\MeetNow\\MeetNow.McpProxy"]
    }
  }
}
```

This is a prerequisite — without it, the LLM will have no MCP tools available. `AutopilotAgent` should verify tools are available on the first cycle (check `tools/list` response) and log a warning if MeetNow tools are missing.

## Constraints

- No new NuGet packages — use `HttpClient` for OpenCode serve API calls
- OpenCode serve is localhost-only, no auth needed (same machine)
- If OpenCode is unavailable, autopilot degrades gracefully (logs warning, no actions)
- All actions still go through `TeamsOperationQueue` for sequential execution
- System prompt is a user-editable file at `%LOCALAPPDATA%\MeetNow\autopilot-system.md`, not hardcoded
