# WebView DOM Message Detection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Detect incoming Teams messages by polling the chat list sidebar DOM in the MessageMonitor WebView every 30 seconds, supplementing existing LevelDB and notification monitors.

**Architecture:** New `WebViewMessageDetector` attaches to the existing MessageMonitor WebViewInstance, polls chat list DOM for unread items, parses sender/content/timestamp, fires `NewMessageDetected` event into the same pipeline as existing monitors. Defensive approach: retry DOM queries, verify results, handle stale DOM gracefully.

**Tech Stack:** Microsoft.Web.WebView2, WPF .NET 8, JavaScript DOM queries

**Spec:** `docs/superpowers/specs/2026-03-26-webview-message-detection-design.md`

**CRITICAL: Defensive DOM Rules (apply to ALL JS in this plan):**
- Wrap DOM queries in retry logic — elements may not exist on first try
- Check return values — null/unexpected = log and skip, never crash
- UI thread dispatch for all WebView2 calls (`Dispatcher.InvokeAsync`)
- Log what was found for debugging when selectors don't match

---

### Task 1: Create WebViewMessageDetector

**Files:**
- Create: `MeetNow/Tasks/WebViewMessageDetector.cs`

Static class that polls the Teams web chat list DOM for unread messages.

- [ ] **Step 1: Create WebViewMessageDetector.cs**

```csharp
using MeetNow.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace MeetNow.Tasks
{
    public static class WebViewMessageDetector
    {
        private static WebViewInstance? _instance;
        private static DispatcherTimer? _pollTimer;
        private static readonly HashSet<string> _processedIds = new();
        private static bool _polling;

        public static event Action<TeamsMessage>? NewMessageDetected;

        public static void StartListening(WebViewInstance instance)
        {
            _instance = instance;

            // Poll every 30 seconds
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _pollTimer.Tick += async (s, e) => await PollChatListAsync();
            _pollTimer.Start();

            Log.Information("WebViewMessageDetector: started polling on [{Name}]", instance.Name);
        }

        public static void StopListening()
        {
            _pollTimer?.Stop();
            _pollTimer = null;
            _instance = null;
            Log.Information("WebViewMessageDetector: stopped");
        }

        private static async Task PollChatListAsync()
        {
            if (_instance == null || _polling) return;
            _polling = true;

            try
            {
                // Read unread chat items from the sidebar DOM
                var resultJson = await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                    () => _instance.EvaluateJsAsync(POLL_CHAT_LIST_JS)).Task.Unwrap();

                if (resultJson == null)
                {
                    Log.Debug("WebViewMessageDetector: poll returned null (page may be loading)");
                    return;
                }

                using var doc = JsonDocument.Parse(resultJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var err))
                {
                    Log.Debug("WebViewMessageDetector: poll error: {Error}", err.GetString());
                    return;
                }

                if (!root.TryGetProperty("unread", out var unreadArr))
                    return;

                var count = 0;
                foreach (var item in unreadArr.EnumerateArray())
                {
                    var sender = item.TryGetProperty("sender", out var s) ? s.GetString() : null;
                    var content = item.TryGetProperty("content", out var c) ? c.GetString() : null;
                    var threadType = item.TryGetProperty("threadType", out var t) ? t.GetString() : "chat";

                    if (string.IsNullOrWhiteSpace(sender) || string.IsNullOrWhiteSpace(content))
                        continue;

                    // Deduplication: sender + content hash
                    var msgId = $"wv_{sender}_{content.GetHashCode():X8}";
                    if (!_processedIds.Add(msgId))
                        continue;

                    count++;
                    var message = new TeamsMessage
                    {
                        Id = msgId,
                        Sender = sender,
                        Content = content,
                        Timestamp = DateTime.Now,
                        ThreadType = threadType,
                        IsMention = item.TryGetProperty("isMention", out var m) && m.GetBoolean()
                    };

                    Log.Information("WebViewMessageDetector: new message from {Sender}: {Content}",
                        sender, content.Length > 80 ? content[..80] + "..." : content);

                    NewMessageDetected?.Invoke(message);
                }

                if (count > 0)
                    Log.Information("WebViewMessageDetector: detected {Count} new message(s)", count);

                // Prevent memory growth
                if (_processedIds.Count > 5000)
                    _processedIds.Clear();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "WebViewMessageDetector: poll failed");
            }
            finally
            {
                _polling = false;
            }
        }

        private const string POLL_CHAT_LIST_JS = @"
(function() {
    try {
        var unread = [];

        // Strategy 1: Find chat list items with unread indicators
        // Teams v2 uses role='listitem' inside the chat list
        var chatItems = document.querySelectorAll('[role=""listitem""]');

        for (var i = 0; i < chatItems.length && unread.length < 20; i++) {
            var item = chatItems[i];
            var ariaLabel = item.getAttribute('aria-label') || '';
            var text = (item.textContent || '').trim();

            // Detect unread: bold text, unread badge, or aria-label containing 'unread'
            var hasUnread = false;

            // Check for unread badge (number or dot)
            var badge = item.querySelector('[class*=""badge""], [class*=""Badge""], [class*=""unread""], [class*=""Unread""]');
            if (badge) hasUnread = true;

            // Check aria-label for unread indicator
            if (ariaLabel.toLowerCase().indexOf('unread') >= 0) hasUnread = true;

            // Check for bold/emphasized sender name (Teams bolds unread chats)
            var boldEl = item.querySelector('b, strong, [class*=""bold""], [class*=""Bold""]');
            if (boldEl && boldEl.textContent.length > 1) hasUnread = true;

            if (!hasUnread) continue;

            // Extract sender — typically first line or aria-label first part
            var sender = '';
            var content = '';
            var threadType = 'chat';

            // Try aria-label parsing: 'SenderName, preview text, time'
            if (ariaLabel.length > 0) {
                var parts = ariaLabel.split(',');
                if (parts.length >= 2) {
                    sender = parts[0].trim();
                    // Content is typically the second-to-last part (before time)
                    content = parts.slice(1, -1).join(',').trim();
                }
            }

            // Fallback: parse from child text elements
            if (!sender) {
                var nameEl = item.querySelector('[class*=""name"" i], [class*=""title"" i], [class*=""sender"" i]');
                if (nameEl) sender = nameEl.textContent.trim();
            }
            if (!content) {
                var previewEl = item.querySelector('[class*=""preview"" i], [class*=""message"" i], [class*=""lastMessage"" i]');
                if (previewEl) content = previewEl.textContent.trim();
            }

            // Last resort: first and second lines of text
            if (!sender || !content) {
                var lines = text.split('\n').filter(function(l) { return l.trim().length > 0; });
                if (lines.length >= 2) {
                    if (!sender) sender = lines[0].trim();
                    if (!content) content = lines[1].trim();
                }
            }

            // Detect mentions
            var isMention = ariaLabel.toLowerCase().indexOf('mention') >= 0
                         || text.indexOf('@') >= 0;

            // Detect channel/meeting vs chat
            if (ariaLabel.toLowerCase().indexOf('channel') >= 0) threadType = 'channel';
            if (ariaLabel.toLowerCase().indexOf('meeting') >= 0) threadType = 'meeting';

            if (sender && content) {
                unread.push({
                    sender: sender.substring(0, 100),
                    content: content.substring(0, 500),
                    threadType: threadType,
                    isMention: isMention,
                    ariaLabel: ariaLabel.substring(0, 200)
                });
            }
        }

        return JSON.stringify({
            total: chatItems.length,
            unread: unread
        });
    } catch(e) {
        return JSON.stringify({ error: e.message });
    }
})();";
    }
}
```

- [ ] **Step 2: Build to verify**

```bash
cd MeetNow && dotnet build --nologo -v q 2>&1 | grep "error CS"
```

- [ ] **Step 3: Commit**

```bash
git add MeetNow/Tasks/WebViewMessageDetector.cs
git commit -m "feat: add WebViewMessageDetector with DOM-based chat list polling"
```

---

### Task 2: Wire Up WebViewMessageDetector in MainWindow

**Files:**
- Modify: `MeetNow/MainWindow.xaml.cs`

Subscribe to the new detector's event and start it after MessageMonitor loads.

- [ ] **Step 1: Start WebViewMessageDetector after MessageMonitor loads**

In `InitializeWebViewManager()`, after the MessageMonitorTask hook, add:

```csharp
// Start WebView message detection on the MessageMonitor instance
Tasks.WebViewMessageDetector.StartListening(persistent);
```

This goes inside the `NavigationCompleted` handler, after `MessageMonitorTask.StartAsync(persistent)`.

- [ ] **Step 2: Subscribe to NewMessageDetected event**

In `StartTeamsMonitor()`, after the existing monitor subscriptions, add:

```csharp
Tasks.WebViewMessageDetector.NewMessageDetected += OnTeamsMessageDetected;
```

- [ ] **Step 3: Stop in OnClosed**

In `OnClosed()`, before `WebViewManager.Instance.Shutdown()`, add:

```csharp
Tasks.WebViewMessageDetector.StopListening();
```

- [ ] **Step 4: Build to verify**

```bash
cd MeetNow && dotnet build --nologo -v q 2>&1 | grep "error CS"
```

- [ ] **Step 5: Commit**

```bash
git add MeetNow/MainWindow.xaml.cs
git commit -m "feat: wire WebViewMessageDetector into MainWindow message pipeline"
```

---

### Task 3: Manual Validation

**Files:** None (testing only)

- [ ] **Step 1: Start the app and wait 30s**

Check log for "WebViewMessageDetector: started polling on [MessageMonitor]"

- [ ] **Step 2: Send a test message from external account**

Send a message from epam Teams to Shell Teams. Wait 30 seconds.

Check log for:
- "WebViewMessageDetector: new message from Boris Kudriashov: ..."
- The message should appear in the Messages window
- Urgency classification should run

- [ ] **Step 3: Verify deduplication**

The same message should NOT appear twice (once from WebView, once from LevelDB if it also detects it).
Check Messages window for duplicates.

- [ ] **Step 4: Check DOM diagnostic if no messages detected**

If poll returns no unread items, check log for:
- "poll returned null" → MessageMonitor WebView not ready
- "poll error" → JS error, check selectors
- Empty unread array → selectors don't match Teams v2 DOM

Open Debug WebView → MessageMonitor to visually inspect the chat list DOM structure.
