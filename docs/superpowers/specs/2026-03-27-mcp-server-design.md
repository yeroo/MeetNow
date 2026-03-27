# MCP Server Design Spec

## Overview

Embed an MCP (Model Context Protocol) server inside the running MeetNow WPF process, exposing Teams automation capabilities to external LLM clients (Claude Code, OpenCode). The server uses SSE transport over a local HTTP endpoint.

## Architecture

### Transport & Hosting

- **Transport:** SSE (Server-Sent Events) over HTTP
- **Endpoint:** `http://localhost:{McpPort}/sse` (default port `27182`)
- **Hosting:** Embedded in the MeetNow process — no separate binary, no IPC
- **Protocol:** JSON-RPC 2.0 over SSE (MCP standard)
- **Lifecycle:** Starts at app startup in `App.xaml.cs`, stops on app shutdown

### Why Embedded SSE

- Most target APIs are static classes in-process — no serialization or IPC overhead
- `MeetingDataAggregator` is an instance class but stateless — safe to create a new instance for MCP queries
- Single-instance mutex prevents launching a second MeetNow process (rules out stdio transport)
- SSE is supported by both Claude Code and OpenCode
- Multiple LLM clients can connect simultaneously

### MCP Protocol Handshake

The server must implement the MCP protocol handshake:
- `initialize` request — respond with server capabilities (tools list)
- `initialized` notification — acknowledge client readiness
- `tools/list` request — return all available tools with JSON Schema parameters

### Threading Model

- `HttpListener` runs on a dedicated background thread
- Read-only tools query static APIs directly (most are thread-safe: `ConcurrentDictionary`, stateless classifiers, cached data)
- **Exception:** `ContactPriorityProvider` uses a plain `Dictionary` — must be made thread-safe (switch to `ConcurrentDictionary` or add locking) since `set_contact_priority` bypasses the queue
- Action tools enqueue via `TeamsOperationQueue.Enqueue()` (already thread-safe)
- `MeetingDataAggregator` is stateless — create a new instance per query, reading `OutlookSource` from `MeetNowSettings.Instance`
- `MeetNowSettings` is read-mostly singleton — safe for background reads

### Error Handling

- Tool errors return MCP-standard JSON-RPC error objects with descriptive messages
- Invalid parameters (bad enum values, query too short) return error before execution
- `HttpListener` startup failure (port conflict) logs a warning and retries on port+1, writing actual port to `%LOCALAPPDATA%\MeetNow\mcp_port`
- Loopback-only traffic — no firewall rules needed on Windows

### Shutdown

`McpServer.Stop()` is called from `App.OnExit`:
- Cancels the `CancellationToken` to stop the listener loop
- Closes all active SSE connections
- `MessageHistory` requires no explicit cleanup (GC handles the `ConcurrentQueue`)

## Tools

### Read-Only Tools

#### `get_messages`

Query recent Teams messages from in-memory history.

**Parameters:**
- `sender` (string, optional) — filter by sender name (partial match)
- `urgency` (string, optional) — filter by urgency: "Urgent", "Normal", "Low"
- `minutes` (int, optional, default 60) — how far back to look

**Returns:** Array of objects:
```json
{
  "sender": "John Doe",
  "content": "Can you review this PR?",
  "timestamp": "2026-03-27T14:30:00Z",
  "threadType": "chat",
  "isMention": false,
  "urgency": "Normal",
  "urgencyReason": "keyword 'review', DM (score: 2.0)"
}
```

**Source:** `MessageHistory` ring buffer (subscribes to `TeamsMessageMonitor.NewMessageDetected`)

#### `get_meetings`

Get calendar meetings for a given date.

**Parameters:**
- `date` (string, optional, default today) — date in YYYY-MM-DD format

**Returns:** Array of objects:
```json
{
  "subject": "Sprint Planning",
  "start": "2026-03-27T09:00:00",
  "end": "2026-03-27T10:00:00",
  "organizer": "Jane Smith",
  "location": "Room 42",
  "teamsUrl": "https://teams.microsoft.com/l/meetup-join/...",
  "responseStatus": "Accepted",
  "isRequired": true,
  "recurrent": true,
  "requiredAttendees": ["Alice", "Bob"],
  "optionalAttendees": ["Charlie"]
}
```

**Source:** `new MeetingDataAggregator().GetMeetings(date, MeetNowSettings.Instance.OutlookSource)` — the `outlookSource` parameter is read from settings internally, not exposed to the MCP caller

**Note:** `ResponseStatus` enum values (`olResponseAccepted`, etc.) are mapped to friendly strings: None, Organized, Tentative, Accepted, Declined, NotResponded

#### `get_contacts`

Search or list enriched contacts.

**Parameters:**
- `query` (string, optional) — search by name or email (min 3 chars)
- `pinned_only` (bool, optional, default false) — only return pinned contacts

**Returns:** Array of objects:
```json
{
  "teamsUserId": "8:orgid:abc-123",
  "displayName": "John Doe",
  "email": "john.doe@shell.com",
  "jobTitle": "Senior Engineer",
  "department": "IT",
  "phone": "+1234567890",
  "isPinned": true,
  "lastSeen": "2026-03-27T14:00:00Z"
}
```

**Source:** `ContactDatabase.GetByName()`, `GetPinned()`, or `GetAll()`

#### `get_favorites`

List favorite contacts from Teams.

**Parameters:** None

**Returns:** Array of favorite contact display names.

**Source:** `FavoriteContactsProvider.GetFavoriteContactNames()`

#### `get_contact_priorities`

List per-contact priority overrides.

**Parameters:**
- `priority` (string, optional) — filter by priority level: "Urgent", "Normal", "Low", "Default"

**Returns:** Array of objects:
```json
{
  "sender": "John Doe",
  "priority": "Urgent"
}
```

**Source:** `ContactPriorityProvider` — when no filter, iterates all priority levels via `GetContactsByPriority()` for each enum value; alternatively, add a new `GetAllOverrides()` method returning `IReadOnlyDictionary<string, ContactPriority>`

#### `get_status`

Get current MeetNow operational status.

**Parameters:** None

**Returns:**
```json
{
  "autopilotActive": true,
  "pendingAutoReplies": { "John Doe": "2026-03-27T15:00:00" },
  "queueCurrent": "Set Teams Busy",
  "queueCurrentStep": "Typing /busy",
  "queuePending": ["Auto-reply Hi to Jane"],
  "isExecuting": true
}
```

**Source:** `AutopilotOverlay`, `TeamsOperationQueue`

### Action Tools

All action tools route through `TeamsOperationQueue` for sequential execution. They return immediately with a queued confirmation.

#### `set_availability`

Set Teams presence status.

**Parameters:**
- `status` (string, required) — one of: "Available", "Busy", "Away", "DoNotDisturb", "BeRightBack"

**Returns:**
```json
{ "queued": true, "description": "Set Teams Busy" }
```

**Execution:** `TeamsOperationQueue.Enqueue()` → `TeamsStatusManager.SetStatusAsync()`

#### `send_message`

Send a Teams message to a contact.

**Parameters:**
- `recipient` (string, required) — contact name or email
- `message` (string, required) — message text

**Returns:**
```json
{ "queued": true, "description": "Send 'Hi' to John Doe" }
```

**Execution:** `TeamsOperationQueue.Enqueue()` → `TeamsStatusManager.SendMessageAsync()`

#### `simulate_typing`

Show "is typing..." indicator to a contact without sending a message.

**Parameters:**
- `recipient` (string, required) — contact name or email

**Returns:**
```json
{ "queued": true, "description": "Simulate typing to John Doe" }
```
Or if cooldown active:
```json
{ "skipped": true, "reason": "Cooldown active, last simulation 2 minutes ago" }
```

**Execution:** `TeamsOperationQueue.TryClaimSimulateTyping()` check, then `TeamsOperationQueue.Enqueue()` → `TeamsStatusManager.SimulateTypingAsync()`

#### `set_contact_priority`

Set or remove a priority override for a contact.

**Parameters:**
- `sender` (string, required) — contact name
- `priority` (string, required) — one of: "Urgent", "Normal", "Low", "Default"

**Returns:**
```json
{ "success": true }
```

**Execution:** Direct call to `ContactPriorityProvider.SetPriority()` (no queue needed — instant operation)

## New Components

### MessageHistory.cs

In-memory ring buffer that stores recent messages for MCP queries.

- Static class with `Add(TeamsMessage)` method — called from `MainWindow.OnTeamsMessageDetected` *after* urgency classification and priority overrides have been applied
- This is important: `MainWindow.OnTeamsMessageDetected` is the unified handler that receives messages from all three sources (TeamsMessageMonitor, NotificationListenerMonitor, WebViewMessageDetector) and runs both `LocalUrgencyClassifier.Classify()` and `ContactPriorityProvider` overrides. `MessageHistory` must consume already-classified messages to avoid duplicating this logic and missing priority overrides.
- Stores last 500 messages in a `ConcurrentQueue<TeamsMessage>`
- Provides `GetRecent(int minutes, string? sender, MessageUrgency? urgency)` query method
- No persistence — live view of recent activity only

### McpServer.cs

Single class handling the full MCP SSE server:

- `HttpListener` on `http://localhost:{port}/`
- SSE endpoint at `/sse` — sends events to connected clients
- POST endpoint at `/messages` — receives JSON-RPC tool calls
- Tool dispatch via switch on tool name → calls to existing static APIs
- JSON serialization with `System.Text.Json`
- Graceful shutdown via `CancellationToken`

### Settings Addition

Add to `MeetNowSettings.cs`:
```csharp
public int McpPort { get; set; } = 27182;
```

### Startup & Shutdown

In `App.xaml.cs` `OnStartup`, after existing initialization:
```csharp
McpServer.Start();
```

In `MainWindow.OnTeamsMessageDetected`, after urgency classification:
```csharp
MessageHistory.Add(message);
```

In `App.OnExit` or shutdown path:
```csharp
McpServer.Stop();
```

## Design Principles

### Extensibility Rule

When adding new features to MeetNow that expose data or actions, also add corresponding MCP tools in `McpServer.cs`. This rule will be added to `CLAUDE.md`.

### Constraints

- No external NuGet dependencies for MCP — use raw `HttpListener` + `System.Text.Json`
- No authentication on the local endpoint (localhost only)
- Action tools always go through `TeamsOperationQueue` to prevent conflicts with autopilot
- Read-only tools query existing thread-safe APIs directly
- `ContactPriorityProvider` must be made thread-safe before MCP integration (switch internal `Dictionary` to `ConcurrentDictionary`)

## File Changes

| File | Change |
|------|--------|
| `MeetNow/McpServer.cs` | New — SSE HTTP listener, JSON-RPC dispatch, tool handlers |
| `MeetNow/MessageHistory.cs` | New — ring buffer for recent messages |
| `MeetNow/MeetNowSettings.cs` | Add `McpPort` property |
| `MeetNow/ContactPriorityProvider.cs` | Replace `Dictionary` with `ConcurrentDictionary` for thread safety |
| `MeetNow/MainWindow.xaml.cs` | Add `MessageHistory.Add(message)` call in `OnTeamsMessageDetected` |
| `MeetNow/App.xaml.cs` | Start `McpServer` at startup, stop on exit |
| `CLAUDE.md` | Add MCP exposure rule for new features |
