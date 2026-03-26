# WebView DOM-based Message Detection Design

## Problem

LevelDB polling only detects messages after the conversation is synced to local storage (requires opening the chat in desktop Teams). External messages and messages in unopened chats are missed until the user manually views them.

## Solution

Add a WebView-based message detector that polls the Teams web chat list sidebar DOM every 30 seconds. Supplements existing LevelDB and notification monitors — does not replace them. Deduplication prevents duplicate alerts across all three sources.

## Architecture

### New Component: WebViewMessageDetector

- Static class in `MeetNow.Tasks`
- Attaches to the existing MessageMonitor WebViewInstance (already on `teams.microsoft.com`)
- DispatcherTimer fires every 30 seconds
- Reads the chat list sidebar DOM for unread messages
- Fires `Action<TeamsMessage> NewMessageDetected` event — same signature as existing monitors

### DOM Polling Strategy

Every 30 seconds, execute JS on the MessageMonitor WebView to:

1. Query chat list items from the sidebar (`[role="listitem"]` or equivalent)
2. For each item, extract:
   - Sender name (from display text / aria-label)
   - Last message preview text
   - Unread indicator (bold text, badge count, or unread dot)
   - Timestamp if available
3. Only process items that have unread indicators
4. Return JSON array of unread messages

### TeamsMessage Mapping

```
Id = "wv_" + hash(sender + content)
Sender = extracted sender name
Content = preview text from chat list
Timestamp = DateTime.Now (or parsed if available)
ThreadType = "chat" (or "channel"/"meeting" if detectable)
IsMention = content contains @username or has mention indicator
```

### Deduplication

- Maintains own `HashSet<string>` of processed message IDs
- Cross-source dedup: same sender + same content within 60 seconds = skip
- Reset at 5,000 entries to prevent memory growth

### Integration

- `MainWindow.StartTeamsMonitor()` subscribes to `WebViewMessageDetector.NewMessageDetected += OnTeamsMessageDetected`
- WebViewMessageDetector starts after MessageMonitor WebView navigates to Teams and loads
- Fires into the same urgency classification + notification pipeline as LevelDB and toast monitors

### What Changes

| File | Change |
|------|--------|
| `Tasks/WebViewMessageDetector.cs` | New — DOM polling + message parsing |
| `MainWindow.xaml.cs` | Wire up `WebViewMessageDetector.NewMessageDetected` event |

### What Stays Unchanged

- `TeamsMessageMonitor.cs` — LevelDB polling continues (10s interval)
- `NotificationListenerMonitor.cs` — toast/title detection continues
- `Models/TeamsMessage.cs` — no model changes
- Urgency classification pipeline — unchanged
- `MessageMonitorTask.cs` — WS hooks continue (for traffic logging)

### Safety

- No API calls — pure DOM reads via `EvaluateJsAsync`
- Read-only — does not click or modify anything in the chat list
- 30s polling interval — minimal CPU impact
- UI thread dispatch required for WebView2 access
