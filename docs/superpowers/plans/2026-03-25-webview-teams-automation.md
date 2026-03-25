# WebView-based Teams Automation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Win32 P/Invoke Teams automation with DOM automation on a dedicated offscreen WebViewInstance, eliminating focus stealing and desktop Teams dependency.

**Architecture:** New persistent "TeamsAutomation" WebViewInstance navigates to `teams.microsoft.com`. TeamsStatusManager methods rewritten to use JS DOM automation instead of Win32 P/Invoke. Shortcut discovery runs on page load to detect correct keyboard shortcuts for embedded WebView2. Existing TeamsOperationQueue unchanged.

**Tech Stack:** Microsoft.Web.WebView2, WPF .NET 8, JavaScript DOM manipulation

**Spec:** `docs/superpowers/specs/2026-03-25-webview-teams-automation-design.md`

---

### Task 1: Create TeamsShortcutDiscovery

**Files:**
- Create: `MeetNow/Tasks/TeamsShortcutDiscovery.cs`

Discovers keyboard shortcuts from the Teams web DOM. Run once after Teams page loads. Stores results for use by automation methods.

- [ ] **Step 1: Create TeamsShortcutDiscovery.cs**

```csharp
using Serilog;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MeetNow.Tasks
{
    public static class TeamsShortcutDiscovery
    {
        private static readonly Dictionary<string, string> _shortcuts = new(StringComparer.OrdinalIgnoreCase);
        public static IReadOnlyDictionary<string, string> Shortcuts => _shortcuts;

        /// <summary>
        /// Scan the Teams web DOM for keyboard shortcuts.
        /// Reads aria-labels and tooltips from nav buttons and search box.
        /// </summary>
        public static async Task DiscoverAsync(WebViewInstance instance)
        {
            if (instance == null) return;

            Log.Information("TeamsShortcutDiscovery: scanning for shortcuts");

            var js = @"
(function() {
    try {
        var shortcuts = {};

        // Scan left rail nav buttons for shortcuts in aria-label/title
        // Format: 'Chat Ctrl+Alt+3' or 'Activity Ctrl+Alt+1'
        var navButtons = document.querySelectorAll('button[aria-label], [role=""tab""][aria-label]');
        for (var i = 0; i < navButtons.length; i++) {
            var label = navButtons[i].getAttribute('aria-label') || '';
            var title = navButtons[i].getAttribute('title') || '';
            var text = label || title;

            // Extract shortcut from text like 'Chat Ctrl+Alt+3'
            var match = text.match(/(Ctrl\+[A-Za-z0-9+]+)/i);
            if (match) {
                var name = text.replace(match[0], '').trim();
                shortcuts[name] = match[0];
            }
        }

        // Find search box shortcut
        var searchInput = document.querySelector('input[aria-label*=""Search""], input[placeholder*=""Search""]');
        if (searchInput) {
            var searchLabel = searchInput.getAttribute('aria-label') || '';
            var searchPlaceholder = searchInput.getAttribute('placeholder') || '';
            var searchText = searchLabel || searchPlaceholder;
            var searchMatch = searchText.match(/(Ctrl\+[A-Za-z0-9+]+)/i);
            if (searchMatch) shortcuts['Search'] = searchMatch[0];
        }

        // Also check tooltip elements that might be visible
        var tooltips = document.querySelectorAll('[class*=""tooltip""], [class*=""Tooltip""]');
        for (var j = 0; j < tooltips.length; j++) {
            var tt = tooltips[j].textContent || '';
            var ttMatch = tt.match(/(\w+)\s+(Ctrl\+[A-Za-z0-9+]+)/i);
            if (ttMatch) shortcuts[ttMatch[1]] = ttMatch[2];
        }

        return JSON.stringify(shortcuts);
    } catch(e) { return JSON.stringify({ error: e.message }); }
})();";

            try
            {
                var result = await instance.EvaluateJsAsync(js);
                if (result == null) return;

                Log.Information("TeamsShortcutDiscovery: raw result: {Result}", result);

                using var doc = System.Text.Json.JsonDocument.Parse(result);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var err))
                {
                    Log.Warning("TeamsShortcutDiscovery: error: {Error}", err.GetString());
                    return;
                }

                _shortcuts.Clear();
                foreach (var prop in root.EnumerateObject())
                {
                    _shortcuts[prop.Name] = prop.Value.GetString() ?? "";
                    Log.Information("TeamsShortcutDiscovery: {Name} = {Shortcut}", prop.Name, prop.Value.GetString());
                }

                // Set defaults for anything not discovered
                _shortcuts.TryAdd("Search", "Ctrl+Alt+E");
                _shortcuts.TryAdd("Chat", "Ctrl+Alt+3");
                _shortcuts.TryAdd("Activity", "Ctrl+Alt+1");
                _shortcuts.TryAdd("Calendar", "Ctrl+Alt+4");

                Log.Information("TeamsShortcutDiscovery: discovered {Count} shortcuts", _shortcuts.Count);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "TeamsShortcutDiscovery: failed");
                // Fall back to known WebView2 defaults
                _shortcuts.TryAdd("Search", "Ctrl+Alt+E");
                _shortcuts.TryAdd("Chat", "Ctrl+Alt+3");
            }
        }

        public static string GetShortcut(string name, string fallback)
        {
            return _shortcuts.TryGetValue(name, out var shortcut) ? shortcut : fallback;
        }
    }
}
```

- [ ] **Step 2: Build to verify**
- [ ] **Step 3: Commit**

```bash
git add MeetNow/Tasks/TeamsShortcutDiscovery.cs
git commit -m "feat: add TeamsShortcutDiscovery for WebView2 keyboard shortcut detection"
```

---

### Task 2: Add TeamsAutomation Instance to WebViewManager

**Files:**
- Modify: `MeetNow/WebViewManager.cs`

Add a third persistent WebViewInstance for Teams automation. Runs shortcut discovery after page loads.

- [ ] **Step 1: Add TeamsAutomation fields and property**

Add alongside `_calendarMonitor`:
```csharp
private WebViewInstance? _teamsAutomation;
public WebViewInstance? TeamsAutomationInstance => _teamsAutomation;
```

Include in `ActiveInstances` list.

- [ ] **Step 2: Add StartTeamsAutomationAsync method**

```csharp
private async Task StartTeamsAutomationAsync()
{
    if (_environment == null) return;

    try
    {
        _teamsAutomation = new WebViewInstance("TeamsAutomation", InstanceType.Persistent);
        await _teamsAutomation.InitializeAsync(_environment);
        await _teamsAutomation.NavigateAndWaitAsync("https://teams.microsoft.com");

        // Run shortcut discovery after Teams loads
        _teamsAutomation.CoreWebView2!.NavigationCompleted += async (s, e) =>
        {
            if (e.IsSuccess && _teamsAutomation.CurrentUrl?.Contains("teams.microsoft.com") == true)
            {
                await Task.Delay(5000); // Wait for Teams to fully render
                await Tasks.TeamsShortcutDiscovery.DiscoverAsync(_teamsAutomation);
            }
        };

        Log.Information("WebViewManager: TeamsAutomation started");
    }
    catch (Exception ex)
    {
        Log.Error(ex, "WebViewManager: failed to start TeamsAutomation");
        _teamsAutomation?.Dispose();
        _teamsAutomation = null;
    }
}
```

- [ ] **Step 3: Call from InitializeAsync after CalendarMonitor**

```csharp
await StartTeamsAutomationAsync();
```

- [ ] **Step 4: Add heartbeat check for TeamsAutomation** (same pattern as CalendarMonitor)

- [ ] **Step 5: Dispose in Shutdown**

```csharp
_teamsAutomation?.Dispose();
_teamsAutomation = null;
```

- [ ] **Step 6: Build to verify**
- [ ] **Step 7: Commit**

```bash
git add MeetNow/WebViewManager.cs
git commit -m "feat: add TeamsAutomation persistent WebViewInstance"
```

---

### Task 3: Rewrite TeamsStatusManager with WebView DOM Automation

**Files:**
- Modify: `MeetNow/TeamsStatusManager.cs`

Replace all Win32 P/Invoke with WebViewManager-based DOM automation. Keep the same public API (`SetStatusAsync`, `SimulateTypingAsync`, `SendMessageAsync`).

- [ ] **Step 1: Remove all Win32 P/Invoke declarations and helpers**

Delete: all `[DllImport]`, `RECT struct`, `MOUSEEVENTF_*`, `VK_*` constants, `FindTeamsWindow`, `FindTeamsWindowCore`, `BringToForeground`, `ClickComposeBox`, `PressEscMultiple`, `TypeTextCharByChar` (Win32 version).

Keep: `TeamsStatus enum`, `ExtractSearchName`.

- [ ] **Step 2: Rewrite SetStatusAsync**

Use the TeamsAutomation WebViewInstance to type slash commands via JS DOM manipulation:

```csharp
public static async Task<bool> SetStatusAsync(TeamsStatus status)
{
    var command = status switch
    {
        TeamsStatus.Available => "/available",
        TeamsStatus.Busy => "/busy",
        TeamsStatus.Away => "/away",
        TeamsStatus.DoNotDisturb => "/dnd",
        TeamsStatus.BeRightBack => "/brb",
        _ => "/available"
    };

    Log.Information("Setting Teams status: {Status} (command: {Command})", status, command);

    var instance = WebViewManager.Instance.TeamsAutomationInstance;
    if (instance == null)
    {
        Log.Warning("TeamsAutomation instance not available");
        return false;
    }

    try
    {
        TeamsOperationQueue.CurrentStep = "Focusing search box";

        // Focus and type into the search box via DOM
        var safeCommand = command.Replace("'", "\\'");
        var result = await instance.EvaluateJsAsync($@"
(function() {{
    try {{
        // Find search input
        var searchInput = document.querySelector('input[id=""searchInput""]')
            || document.querySelector('input[aria-label*=""Search""]')
            || document.querySelector('input[placeholder*=""Search""]');

        if (!searchInput) {{
            // Try clicking search button first
            var searchBtn = document.querySelector('button[aria-label*=""Search""]')
                || document.querySelector('[data-tid=""search-button""]');
            if (searchBtn) searchBtn.click();
            return 'search_clicked';
        }}

        searchInput.focus();
        searchInput.value = '{safeCommand}';
        searchInput.dispatchEvent(new Event('input', {{ bubbles: true }}));
        return 'typed';
    }} catch(e) {{ return 'error:' + e.message; }}
}})();");

        if (result == "search_clicked")
        {
            await Task.Delay(500);
            // Retry typing after search opened
            result = await instance.EvaluateJsAsync($@"
(function() {{
    var input = document.querySelector('input[aria-label*=""Search""]')
        || document.querySelector('input[placeholder*=""Search""]');
    if (!input) return 'no_input';
    input.focus();
    input.value = '{safeCommand}';
    input.dispatchEvent(new Event('input', {{ bubbles: true }}));
    return 'typed';
}})();");
        }

        Log.Information("SetStatus: type result = {Result}", result);

        TeamsOperationQueue.CurrentStep = "Waiting for autocomplete";
        await Task.Delay(1500);

        // Press Enter via keyboard event
        TeamsOperationQueue.CurrentStep = "Executing command";
        await instance.EvaluateJsAsync(@"
(function() {
    var input = document.querySelector('input[aria-label*=""Search""]')
        || document.querySelector('input[placeholder*=""Search""]')
        || document.activeElement;
    if (input) {
        input.dispatchEvent(new KeyboardEvent('keydown', {key: 'Enter', code: 'Enter', keyCode: 13, bubbles: true}));
        input.dispatchEvent(new KeyboardEvent('keyup', {key: 'Enter', code: 'Enter', keyCode: 13, bubbles: true}));
    }
    return 'ok';
})();");

        await Task.Delay(1000);

        // Dismiss by pressing Escape
        await instance.EvaluateJsAsync(@"
(function() {
    document.dispatchEvent(new KeyboardEvent('keydown', {key: 'Escape', keyCode: 27, bubbles: true}));
    return 'ok';
})();");

        TeamsOperationQueue.CurrentStep = "Done";
        Log.Information("Teams status command sent: {Command}", command);
        return true;
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Error setting Teams status");
        return false;
    }
}
```

- [ ] **Step 3: Rewrite SimulateTypingAsync**

Navigate to direct chat URL, type in compose box, hold, clear:

```csharp
public static async Task<bool> SimulateTypingAsync(string senderName)
{
    Log.Information("SimulateTyping: start for '{Name}'", senderName);

    var instance = WebViewManager.Instance.TeamsAutomationInstance;
    if (instance == null)
    {
        Log.Warning("TeamsAutomation instance not available");
        return false;
    }

    try
    {
        // Resolve Teams user ID from ContactDatabase
        var userId = ResolveTeamsUserId(senderName);
        if (userId == null)
        {
            Log.Warning("SimulateTyping: could not resolve user ID for '{Name}'", senderName);
            return false;
        }

        // Navigate to direct chat URL
        TeamsOperationQueue.CurrentStep = "Opening chat";
        var chatUrl = $"https://teams.microsoft.com/l/chat/0/0?users={Uri.EscapeDataString(userId)}";
        await instance.NavigateAndWaitAsync(chatUrl);
        await Task.Delay(3000);

        // Find and focus compose box
        TeamsOperationQueue.CurrentStep = "Typing 'Hi'";
        await instance.EvaluateJsAsync(@"
(function() {
    var compose = document.querySelector('[data-tid=""ckeditor-replyConversation""]')
        || document.querySelector('[role=""textbox""][aria-label*=""message""]')
        || document.querySelector('[role=""textbox""][contenteditable=""true""]')
        || document.querySelector('div[contenteditable=""true""]');
    if (compose) {
        compose.focus();
        compose.textContent = 'Hi';
        compose.dispatchEvent(new Event('input', {bubbles: true}));
    }
    return compose ? 'typed' : 'no_compose';
})();");

        // Hold for configured duration (shows "is typing..." to recipient)
        var holdMs = MeetNowSettings.Instance.SimulateTypingDurationSeconds * 1000;
        TeamsOperationQueue.CurrentStep = $"Holding typing ({holdMs/1000}s)";
        await Task.Delay(holdMs);

        // Clear text (do NOT send)
        TeamsOperationQueue.CurrentStep = "Clearing text";
        await instance.EvaluateJsAsync(@"
(function() {
    var compose = document.querySelector('[data-tid=""ckeditor-replyConversation""]')
        || document.querySelector('[role=""textbox""][contenteditable=""true""]')
        || document.querySelector('div[contenteditable=""true""]');
    if (compose) {
        compose.textContent = '';
        compose.dispatchEvent(new Event('input', {bubbles: true}));
    }
    return 'cleared';
})();");

        // Navigate back to Teams home
        await instance.NavigateAndWaitAsync("https://teams.microsoft.com");

        TeamsOperationQueue.CurrentStep = "Done";
        Log.Information("SimulateTyping: complete for '{Name}'", senderName);
        return true;
    }
    catch (Exception ex)
    {
        Log.Error(ex, "SimulateTyping: error for '{Name}'", senderName);
        return false;
    }
}
```

- [ ] **Step 4: Rewrite SendMessageAsync**

Same as SimulateTyping but press Enter to send:

```csharp
public static async Task<bool> SendMessageAsync(string senderName, string message)
{
    Log.Information("SendMessage: start for '{Name}', message: '{Message}'", senderName, message);

    var instance = WebViewManager.Instance.TeamsAutomationInstance;
    if (instance == null)
    {
        Log.Warning("TeamsAutomation instance not available");
        return false;
    }

    try
    {
        var userId = ResolveTeamsUserId(senderName);
        if (userId == null)
        {
            Log.Warning("SendMessage: could not resolve user ID for '{Name}'", senderName);
            return false;
        }

        TeamsOperationQueue.CurrentStep = "Opening chat";
        var chatUrl = $"https://teams.microsoft.com/l/chat/0/0?users={Uri.EscapeDataString(userId)}";
        await instance.NavigateAndWaitAsync(chatUrl);
        await Task.Delay(3000);

        // Type message
        var safeMessage = message.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n");
        TeamsOperationQueue.CurrentStep = "Typing message";
        await instance.EvaluateJsAsync($@"
(function() {{
    var compose = document.querySelector('[data-tid=""ckeditor-replyConversation""]')
        || document.querySelector('[role=""textbox""][contenteditable=""true""]')
        || document.querySelector('div[contenteditable=""true""]');
    if (compose) {{
        compose.focus();
        compose.textContent = '{safeMessage}';
        compose.dispatchEvent(new Event('input', {{bubbles: true}}));
    }}
    return compose ? 'typed' : 'no_compose';
}})();");

        await Task.Delay(500);

        // Press Enter to SEND
        TeamsOperationQueue.CurrentStep = "Sending";
        await instance.EvaluateJsAsync(@"
(function() {
    var compose = document.querySelector('[data-tid=""ckeditor-replyConversation""]')
        || document.querySelector('[role=""textbox""][contenteditable=""true""]')
        || document.querySelector('div[contenteditable=""true""]');
    if (compose) {
        compose.dispatchEvent(new KeyboardEvent('keydown', {key: 'Enter', code: 'Enter', keyCode: 13, bubbles: true}));
    }
    return 'sent';
})();");

        await Task.Delay(1000);
        await instance.NavigateAndWaitAsync("https://teams.microsoft.com");

        TeamsOperationQueue.CurrentStep = "Done";
        Log.Information("SendMessage: sent '{Message}' to '{Name}'", message, senderName);
        return true;
    }
    catch (Exception ex)
    {
        Log.Error(ex, "SendMessage: error for '{Name}'", senderName);
        return false;
    }
}
```

- [ ] **Step 5: Add ResolveTeamsUserId helper**

Looks up the sender name in ContactDatabase to find their `8:orgid:GUID` for direct chat URL:

```csharp
private static string? ResolveTeamsUserId(string senderName)
{
    var searchName = ExtractSearchName(senderName);

    // Search ContactDatabase by name
    var contacts = ContactDatabase.GetByName(searchName);
    if (contacts.Count > 0)
        return contacts[0].TeamsUserId;

    // Try original name
    contacts = ContactDatabase.GetByName(senderName);
    if (contacts.Count > 0)
        return contacts[0].TeamsUserId;

    Log.Warning("ResolveTeamsUserId: no contact found for '{Name}'", senderName);
    return null;
}
```

- [ ] **Step 6: Build to verify**
- [ ] **Step 7: Commit**

```bash
git add MeetNow/TeamsStatusManager.cs
git commit -m "feat: rewrite TeamsStatusManager with WebView DOM automation"
```

---

### Task 4: Wire Up TeamsAutomation in MainWindow

**Files:**
- Modify: `MeetNow/MainWindow.xaml.cs`

Ensure TeamsAutomation starts when the WebViewManager initializes. No changes to operation queueing — `TeamsOperationQueue` calls `TeamsStatusManager` methods which now use WebView internally.

- [ ] **Step 1: No MainWindow changes needed for the queue wiring**

The existing code already works:
- `AutopilotOverlay.Enable()` enqueues `TeamsStatusManager.SetStatusAsync(Busy)` — this now uses WebView
- `OnTeamsMessageDetected` enqueues `SimulateTypingAsync` — this now uses WebView
- Debug menu buttons enqueue status changes — these now use WebView

The only change: ensure WebViewManager starts TeamsAutomation (done in Task 2).

- [ ] **Step 2: Remove System.Windows.Forms reference if no longer needed**

`TypeTextCharByChar` used `System.Windows.Forms.SendKeys.SendWait()`. After removing Win32 code, check if anything else uses `System.Windows.Forms`. If not, remove the package reference from `MeetNow.csproj`.

- [ ] **Step 3: Build and verify**
- [ ] **Step 4: Commit**

```bash
git add MeetNow/MainWindow.xaml.cs MeetNow/MeetNow.csproj
git commit -m "feat: wire TeamsAutomation WebView into MainWindow startup"
```

---

### Task 5: Manual Validation

**Files:** None (testing only)

- [ ] **Step 1: Test status change**

1. Start app, wait ~30s for TeamsAutomation to load
2. In tray menu debug options, click "Set Teams: Available" / "Set Teams: Busy"
3. Verify Teams status changes (check Teams desktop or mobile)
4. Verify no focus stealing — no windows pop up
5. Check log for "Teams status command sent"

- [ ] **Step 2: Test simulate typing**

1. In debug menu, click "Test: Simulate Typing"
2. Ask a colleague to confirm they see "is typing..." in their chat
3. Verify text is cleared after configured duration
4. Verify no windows visible during the operation

- [ ] **Step 3: Test send message**

1. Enable autopilot, trigger urgent message forwarding
2. Verify message arrives at the configured contact
3. Verify no focus stealing

- [ ] **Step 4: Verify QueueOverlay still works**

1. Queue multiple operations (e.g. Set Busy + Simulate Typing)
2. Verify QueueOverlay shows progress
3. Verify operations execute sequentially
