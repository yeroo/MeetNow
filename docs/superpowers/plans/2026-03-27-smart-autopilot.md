# Smart Autopilot Agent Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace MeetNow's hardcoded autopilot with an LLM-powered agent loop via OpenCode serve API, enabling intelligent presence management and conservative message responses.

**Architecture:** `AutopilotAgent.cs` manages OpenCode serve process lifecycle, persistent sessions, and cycle triggers. Each cycle sends context to OpenCode, which uses MCP tools to observe and act. A chat window lets the user give live instructions.

**Tech Stack:** C# .NET 8 WPF, `System.Net.Http.HttpClient`, OpenCode serve API (JSON-RPC over HTTP), MCP tools

**Spec:** `docs/superpowers/specs/2026-03-27-smart-autopilot-design.md`

---

### Task 1: Add autopilot settings

**Files:**
- Modify: `MeetNow/MeetNowSettings.cs`

- [ ] **Step 1: Add new settings properties**

In `MeetNow/MeetNowSettings.cs`, add after the `McpPort` property (line 38):

```csharp

// Smart Autopilot
public string AutopilotModel { get; set; } = "copilot/claude-sonnet-4-6";
public int OpenCodePort { get; set; } = 4096;
public int AutopilotCycleIntervalSeconds { get; set; } = 300;
public int AutopilotMessageDebounceSeconds { get; set; } = 30;
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build MeetNow/MeetNow.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 3: Commit**

```bash
git add MeetNow/MeetNowSettings.cs
git commit -m "feat: add smart autopilot settings (model, port, cycle interval)"
```

---

### Task 2: Strip AutopilotOverlay to UI-only

**Files:**
- Modify: `MeetNow/AutopilotOverlay.cs`

Remove all decision-making logic, keeping only the UI overlay. The LLM now handles all decisions.

- [ ] **Step 1: Remove message tracking fields and methods**

In `MeetNow/AutopilotOverlay.cs`:

Remove these fields (lines 23-26):
```csharp
private static readonly ConcurrentDictionary<string, int> _urgentMessageCounts = new();
private record PendingReply(CancellationTokenSource Cts, DateTime ScheduledTime);
private static readonly ConcurrentDictionary<string, PendingReply> _pendingReplies = new();
```

Remove `_autoOffTimer` field (line 20):
```csharp
private static Timer? _autoOffTimer;
```

Remove the `using System.Collections.Concurrent;` and `using System.Threading;` imports if no longer needed.

- [ ] **Step 2: Simplify Enable() to UI-only**

Replace the `Enable()` method (lines 39-57) with:

```csharp
public static void Enable()
{
    if (IsActive) return;

    Application.Current.Dispatcher.Invoke(() =>
    {
        _borderWindow = new BorderOverlayWindow();
        _borderWindow.Show();

        _buttonWindow = new ButtonOverlayWindow();
        _buttonWindow.DisableClicked += Disable;
        _buttonWindow.Show();
    });

    Log.Information("Autopilot mode enabled (UI overlay shown)");
}
```

- [ ] **Step 3: Simplify Disable() to UI-only**

Replace the `Disable()` method (lines 59-85) with:

```csharp
public static void Disable()
{
    Application.Current.Dispatcher.Invoke(() =>
    {
        _borderWindow?.Close();
        _borderWindow = null;
        _buttonWindow?.Close();
        _buttonWindow = null;
    });

    Log.Information("Autopilot mode disabled (UI overlay hidden)");
}
```

- [ ] **Step 4: Remove TrackUrgentMessage, GetPendingAutoReplies, ScheduleAutoReplyAsync, StartAutoOffTimer**

Delete these methods entirely:
- `TrackUrgentMessage` (lines 90-109)
- `GetPendingAutoReplies` (lines 111-117)
- `ScheduleAutoReplyAsync` (lines 119-145)
- `StartAutoOffTimer` (lines 147-161)

- [ ] **Step 5: Build and verify**

Run: `dotnet build MeetNow/MeetNow.csproj`
Expected: Build errors in `MainWindow.xaml.cs`, `McpServer.cs`, and `QueueOverlayWindow.cs` referencing removed methods. These will be fixed in Tasks 3 and 4.

- [ ] **Step 6: Commit (even with build errors — they're fixed in next tasks)**

```bash
git add MeetNow/AutopilotOverlay.cs
git commit -m "refactor: strip AutopilotOverlay to UI-only, remove hardcoded decision logic"
```

---

### Task 3: Update downstream callers of removed AutopilotOverlay methods

**Files:**
- Modify: `MeetNow/MainWindow.xaml.cs`
- Modify: `MeetNow/QueueOverlayWindow.cs`

- [ ] **Step 1: Gut the hardcoded autopilot block in OnTeamsMessageDetected**

In `MeetNow/MainWindow.xaml.cs`, find `OnTeamsMessageDetected` (line 303). Replace the entire block from `if (AutopilotOverlay.IsActive)` (line 323) through the closing brace of that if block (line 349) with:

```csharp
                // Trigger autopilot cycle on new message (LLM decides what to do)
                AutopilotAgent.TriggerCycle();
```

Keep the lines above it (urgency classification, MessageSummaryWindow.AddMessage, MessageHistory.Add) untouched.

- [ ] **Step 2: Remove ForwardUrgentIfEnabled method**

Delete the `ForwardUrgentIfEnabled` method (lines 357-369) entirely.

- [ ] **Step 3: Fix QueueOverlayWindow.cs**

In `MeetNow/QueueOverlayWindow.cs`, replace the `GetPendingAutoReplies` references:

At lines 45-47, replace:
```csharp
var autoReplies = AutopilotOverlay.IsActive
    ? AutopilotOverlay.GetPendingAutoReplies()
    : new Dictionary<string, DateTime>();
```
with:
```csharp
var autoReplies = new Dictionary<string, DateTime>();
```

At lines 211-221 (the auto-replies display loop), remove or comment out the loop that renders pending auto-replies. Replace with nothing (empty section).

- [ ] **Step 4: Build and verify**

Run: `dotnet build MeetNow/MeetNow.csproj`
Expected: Build error — `AutopilotAgent` does not exist yet. This is expected and will be fixed in Task 5. However, `McpServer.cs` should also have errors from `GetPendingAutoReplies` — fix in next task.

- [ ] **Step 5: Commit**

```bash
git add MeetNow/MainWindow.xaml.cs MeetNow/QueueOverlayWindow.cs
git commit -m "refactor: remove hardcoded autopilot logic from MainWindow and QueueOverlay"
```

---

### Task 4: Add new MCP tools and update get_status

**Files:**
- Modify: `MeetNow/McpServer.cs`

- [ ] **Step 1: Add 3 new tool cases to DispatchTool**

In `MeetNow/McpServer.cs`, add to the `DispatchTool` switch expression (around line 375, before the `_ => throw` default):

```csharp
"enable_autopilot" => ToolEnableAutopilot(),
"disable_autopilot" => ToolDisableAutopilot(),
"send_instruction" => ToolSendInstruction(args),
```

- [ ] **Step 2: Add 3 tool definitions to GetToolDefinitions**

Add to the array in `GetToolDefinitions()` (before the closing `};`):

```csharp
new
{
    name = "enable_autopilot",
    description = "Switch autopilot to active mode. Shows red border overlay. Does NOT change Teams status — use set_availability separately.",
    inputSchema = new { type = "object", properties = new Dictionary<string, object>() }
},
new
{
    name = "disable_autopilot",
    description = "Switch autopilot to passive mode. Hides red border overlay. Does NOT change Teams status or clear queue.",
    inputSchema = new { type = "object", properties = new Dictionary<string, object>() }
},
new
{
    name = "send_instruction",
    description = "Send an instruction to the autopilot agent (e.g., 'I am in a doctor appointment, back at 3pm').",
    inputSchema = new
    {
        type = "object",
        properties = new Dictionary<string, object>
        {
            ["message"] = new { type = "string", description = "Instruction text" }
        },
        required = new[] { "message" }
    }
}
```

- [ ] **Step 3: Add tool implementation methods**

Add before `SendJsonRpcResult`:

```csharp
private static object ToolEnableAutopilot()
{
    Application.Current.Dispatcher.Invoke(() => AutopilotOverlay.Enable());
    AutopilotAgent.SetMode(true);
    return new { success = true, mode = "active" };
}

private static object ToolDisableAutopilot()
{
    Application.Current.Dispatcher.Invoke(() => AutopilotOverlay.Disable());
    AutopilotAgent.SetMode(false);
    return new { success = true, mode = "passive" };
}

private static object ToolSendInstruction(JsonElement? args)
{
    var message = args?.TryGetProperty("message", out var m) == true ? m.GetString() : null;
    if (string.IsNullOrEmpty(message))
        throw new ArgumentException("Missing required parameter: message");

    AutopilotAgent.AddInstruction(message);
    return new { success = true };
}
```

Add `using System.Windows;` at the top if not already present.

- [ ] **Step 4: Update ToolGetStatus**

Replace the `ToolGetStatus` method to remove `pendingAutoReplies` and add `autopilotMode`:

```csharp
private static object ToolGetStatus()
{
    var pending = TeamsOperationQueue.PendingSnapshot;
    var current = TeamsOperationQueue.Current;

    return new
    {
        autopilotActive = AutopilotOverlay.IsActive,
        autopilotMode = AutopilotAgent.IsActive ? "active" : "passive",
        queueCurrent = current?.Description,
        queueCurrentStep = TeamsOperationQueue.CurrentStep,
        queuePending = pending.Select(e => e.Description).ToArray(),
        isExecuting = TeamsOperationQueue.IsExecuting
    };
}
```

- [ ] **Step 5: Build (will fail — AutopilotAgent doesn't exist yet)**

Run: `dotnet build MeetNow/MeetNow.csproj`
Expected: Errors referencing `AutopilotAgent`. This is expected.

- [ ] **Step 6: Commit**

```bash
git add MeetNow/McpServer.cs
git commit -m "feat: add enable/disable_autopilot and send_instruction MCP tools"
```

---

### Task 5: Create AutopilotAgent core

**Files:**
- Create: `MeetNow/AutopilotAgent.cs`

This is the main new class — OpenCode serve management, session lifecycle, cycle triggers, instruction management.

- [ ] **Step 1: Create AutopilotAgent.cs**

Create `MeetNow/AutopilotAgent.cs`:

```csharp
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MeetNow
{
    /// <summary>
    /// LLM-powered autopilot agent that uses OpenCode serve API to make
    /// intelligent decisions about Teams presence and messaging.
    /// </summary>
    public static class AutopilotAgent
    {
        private static readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false });
        private static Timer? _cycleTimer;
        private static Timer? _healthTimer;
        private static Timer? _debounceTimer;
        private static readonly SemaphoreSlim _cycleLock = new(1, 1);
        private static bool _pendingRetrigger;
        private static string? _sessionId;
        private static int _cycleCount;
        private static DateTime _sessionDate;
        private static readonly List<string> _instructions = new();
        private static readonly List<string> _log = new();
        private static readonly object _logLock = new();
        private static string? _systemPrompt;

        /// <summary>True when the LLM is allowed to execute action tools.</summary>
        public static bool IsActive { get; private set; }

        /// <summary>True when the agent is connected to OpenCode serve.</summary>
        public static bool IsConnected { get; private set; }

        public static event Action? LogUpdated;

        private static string BaseUrl => $"http://localhost:{MeetNowSettings.Instance.OpenCodePort}";

        private static readonly string SystemPromptPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MeetNow", "autopilot-system.md");

        public static void Start()
        {
            Log.Information("AutopilotAgent: starting");
            EnsureSystemPromptFile();

            // Health check timer: every 60s
            _healthTimer = new Timer(_ => _ = CheckHealthAsync(),
                null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60));

            // Cycle timer: configurable interval
            var interval = TimeSpan.FromSeconds(MeetNowSettings.Instance.AutopilotCycleIntervalSeconds);
            _cycleTimer = new Timer(_ => TriggerCycle(),
                null, TimeSpan.FromSeconds(30), interval);

            Log.Information("AutopilotAgent: started (cycle interval: {Interval}s)", interval.TotalSeconds);
        }

        public static void Stop()
        {
            _cycleTimer?.Dispose();
            _healthTimer?.Dispose();
            _debounceTimer?.Dispose();
            _cycleTimer = null;
            _healthTimer = null;
            _debounceTimer = null;
            Log.Information("AutopilotAgent: stopped");
        }

        /// <summary>Set active/passive mode. Called by MCP tools.</summary>
        public static void SetMode(bool active)
        {
            IsActive = active;
            AddLog($"Mode changed to {(active ? "active" : "passive")}");
            Log.Information("AutopilotAgent: mode set to {Mode}", active ? "active" : "passive");
        }

        /// <summary>Add a user instruction. Triggers an immediate cycle.</summary>
        public static void AddInstruction(string instruction)
        {
            lock (_logLock)
            {
                _instructions.Add(instruction);
            }
            AddLog($"You: {instruction}");
            TriggerCycle();
        }

        /// <summary>Clear all standing instructions.</summary>
        public static void ClearInstructions()
        {
            lock (_logLock)
            {
                _instructions.Clear();
            }
            AddLog("Instructions cleared");
        }

        /// <summary>Get recent log entries for the chat window.</summary>
        public static List<string> GetLog()
        {
            lock (_logLock)
            {
                return _log.ToList();
            }
        }

        /// <summary>
        /// Trigger an autopilot cycle. Debounced when called from message events.
        /// Only one cycle runs at a time; additional triggers are coalesced.
        /// </summary>
        public static void TriggerCycle()
        {
            // Debounce: reset the timer each call
            var debounce = MeetNowSettings.Instance.AutopilotMessageDebounceSeconds;
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => _ = RunCycleAsync(),
                null, TimeSpan.FromSeconds(debounce), Timeout.InfiniteTimeSpan);
        }

        /// <summary>Send a user message directly to the OpenCode session (from chat panel).</summary>
        public static async Task SendChatMessageAsync(string message)
        {
            AddInstruction(message);
            // Run cycle immediately (bypasses debounce)
            await RunCycleAsync();
        }

        private static async Task RunCycleAsync()
        {
            if (!_cycleLock.Wait(0))
            {
                _pendingRetrigger = true;
                return;
            }

            try
            {
                if (!IsConnected)
                {
                    Log.Debug("AutopilotAgent: skipping cycle — not connected to OpenCode");
                    return;
                }

                // Reset session if date changed or cycle limit reached
                if (_sessionId != null && (_sessionDate.Date != DateTime.Today || _cycleCount >= 50))
                {
                    Log.Information("AutopilotAgent: resetting session (date changed or cycle limit)");
                    _sessionId = null;
                    _cycleCount = 0;
                }

                // Create session if needed
                if (_sessionId == null)
                {
                    _sessionId = await CreateSessionAsync();
                    if (_sessionId == null) return;
                    _sessionDate = DateTime.Today;
                    _cycleCount = 0;
                }

                // Load system prompt (re-read each cycle so user edits take effect)
                _systemPrompt = File.Exists(SystemPromptPath)
                    ? File.ReadAllText(SystemPromptPath)
                    : null;

                // Build context message
                var context = BuildContextMessage();

                // Send to OpenCode
                var response = await SendMessageAsync(_sessionId, context, _systemPrompt);
                _cycleCount++;

                if (response != null)
                {
                    AddLog($"Autopilot: {response}");
                }
            }
            catch (HttpRequestException ex)
            {
                Log.Warning(ex, "AutopilotAgent: cycle failed — connection error");
                IsConnected = false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "AutopilotAgent: cycle error");
            }
            finally
            {
                _cycleLock.Release();

                if (_pendingRetrigger)
                {
                    _pendingRetrigger = false;
                    _ = RunCycleAsync();
                }
            }
        }

        private static string BuildContextMessage()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Time: {DateTime.Now:HH:mm} {DateTime.Now:dddd} {DateTime.Now:yyyy-MM-dd}");
            sb.AppendLine($"Mode: {(IsActive ? "active" : "passive")}");
            sb.AppendLine($"Cycle: #{_cycleCount + 1}");

            lock (_logLock)
            {
                if (_instructions.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Standing instructions:");
                    foreach (var inst in _instructions)
                        sb.AppendLine($"- {inst}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("Check messages and calendar. Decide what to do.");
            return sb.ToString();
        }

        private static async Task<string?> CreateSessionAsync()
        {
            try
            {
                var body = JsonSerializer.Serialize(new { title = "MeetNow Autopilot" });
                var response = await _http.PostAsync($"{BaseUrl}/session",
                    new StringContent(body, Encoding.UTF8, "application/json"));

                if (!response.IsSuccessStatusCode)
                {
                    Log.Warning("AutopilotAgent: failed to create session: {Status}", response.StatusCode);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(json);

                // OpenCode returns the session object — extract the ID
                if (doc.RootElement.TryGetProperty("id", out var idProp))
                {
                    var id = idProp.GetString();
                    Log.Information("AutopilotAgent: created session {Id}", id);
                    return id;
                }

                Log.Warning("AutopilotAgent: session response missing id: {Json}", json);
                return null;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "AutopilotAgent: error creating session");
                return null;
            }
        }

        private static async Task<string?> SendMessageAsync(string sessionId, string text, string? systemPrompt)
        {
            try
            {
                var settings = MeetNowSettings.Instance;
                var body = new
                {
                    parts = new[] { new { type = "text", text } },
                    system = systemPrompt,
                    model = settings.AutopilotModel
                };

                var json = JsonSerializer.Serialize(body);
                var response = await _http.PostAsync($"{BaseUrl}/session/{sessionId}/message",
                    new StringContent(json, Encoding.UTF8, "application/json"));

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    Log.Warning("AutopilotAgent: message failed: {Status} {Body}",
                        response.StatusCode, errorBody);

                    // Session gone — invalidate
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        Log.Information("AutopilotAgent: session {Id} not found, will recreate", sessionId);
                        _sessionId = null;
                    }

                    return null;
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(responseJson);

                // Extract the assistant's text response from the message parts
                if (doc.RootElement.TryGetProperty("parts", out var parts))
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("type", out var typeProp) &&
                            typeProp.GetString() == "text" &&
                            part.TryGetProperty("text", out var textProp))
                        {
                            return textProp.GetString();
                        }
                    }
                }

                return null;
            }
            catch (TaskCanceledException)
            {
                Log.Warning("AutopilotAgent: message timed out");
                return null;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "AutopilotAgent: error sending message");
                return null;
            }
        }

        private static async Task CheckHealthAsync()
        {
            try
            {
                var response = await _http.GetAsync($"{BaseUrl}/global/health");
                var wasConnected = IsConnected;
                IsConnected = response.IsSuccessStatusCode;

                if (IsConnected && !wasConnected)
                {
                    Log.Information("AutopilotAgent: connected to OpenCode serve on port {Port}",
                        MeetNowSettings.Instance.OpenCodePort);
                    // Invalidate session after reconnect (OpenCode may have restarted)
                    _sessionId = null;
                }
                else if (!IsConnected && wasConnected)
                {
                    Log.Warning("AutopilotAgent: lost connection to OpenCode serve");
                }
            }
            catch (HttpRequestException)
            {
                if (IsConnected)
                {
                    Log.Warning("AutopilotAgent: OpenCode serve not responding, attempting launch");
                    IsConnected = false;
                }
                await TryLaunchOpenCodeAsync();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "AutopilotAgent: health check error");
                IsConnected = false;
            }
        }

        private static async Task TryLaunchOpenCodeAsync()
        {
            try
            {
                var port = MeetNowSettings.Instance.OpenCodePort;
                var startInfo = new ProcessStartInfo
                {
                    FileName = "opencode",
                    Arguments = $"serve --port {port}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                var process = Process.Start(startInfo);
                if (process == null)
                {
                    Log.Warning("AutopilotAgent: failed to launch opencode serve");
                    return;
                }

                Log.Information("AutopilotAgent: launched opencode serve --port {Port} (PID {Pid})",
                    port, process.Id);

                // Wait a few seconds for it to start, then health check will pick it up
                await Task.Delay(5000);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "AutopilotAgent: could not launch opencode (not installed?)");
            }
        }

        private static void EnsureSystemPromptFile()
        {
            if (File.Exists(SystemPromptPath)) return;

            try
            {
                var dir = Path.GetDirectoryName(SystemPromptPath)!;
                Directory.CreateDirectory(dir);

                var defaultPrompt = @"You are Boris's Teams autopilot. You run continuously and manage his presence.

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
- Only send brief holding messages (""In a meeting, will follow up shortly"")
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
- If nothing has changed since last cycle, just say ""No action needed.""

## Response format
After each cycle, briefly report what you did and why (1-2 sentences).
";
                File.WriteAllText(SystemPromptPath, defaultPrompt);
                Log.Information("AutopilotAgent: created default system prompt at {Path}", SystemPromptPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "AutopilotAgent: could not create default system prompt");
            }
        }

        private static void AddLog(string entry)
        {
            var timestamped = $"[{DateTime.Now:HH:mm:ss}] {entry}";
            lock (_logLock)
            {
                _log.Add(timestamped);
                // Keep last 200 entries
                if (_log.Count > 200)
                    _log.RemoveAt(0);
            }
            try { LogUpdated?.Invoke(); }
            catch { }
        }
    }
}
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build MeetNow/MeetNow.csproj`
Expected: Build succeeded, 0 errors (all references to `AutopilotAgent` from Tasks 3 and 4 are now resolved)

- [ ] **Step 3: Commit**

```bash
git add MeetNow/AutopilotAgent.cs
git commit -m "feat: add AutopilotAgent — OpenCode serve integration, session management, cycle triggers"
```

---

### Task 6: Create AutopilotChatWindow

**Files:**
- Create: `MeetNow/AutopilotChatWindow.xaml`
- Create: `MeetNow/AutopilotChatWindow.xaml.cs`
- Modify: `MeetNow/MainWindow.xaml` (add menu item)
- Modify: `MeetNow/MainWindow.xaml.cs` (add click handler)

- [ ] **Step 1: Create AutopilotChatWindow.xaml**

Create `MeetNow/AutopilotChatWindow.xaml`:

```xml
<Window x:Class="MeetNow.AutopilotChatWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Autopilot Chat" Width="500" Height="600"
        WindowStartupLocation="CenterScreen"
        Background="#1E1E1E">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <!-- Status bar -->
        <StackPanel Grid.Row="0" Orientation="Horizontal" Margin="10,8">
            <TextBlock x:Name="StatusText" Foreground="#AAA" FontSize="12"
                       Text="Passive — Not connected"/>
            <TextBlock Foreground="#666" FontSize="12" Margin="20,0,0,0"
                       x:Name="CycleText" Text=""/>
        </StackPanel>

        <!-- Log area -->
        <ListBox Grid.Row="1" x:Name="LogList" Margin="10,0,10,5"
                 Background="#111" Foreground="#DDD" BorderThickness="0"
                 FontFamily="Consolas" FontSize="12"
                 ScrollViewer.VerticalScrollBarVisibility="Auto"/>

        <!-- Input area -->
        <Grid Grid.Row="2" Margin="10,5,10,10">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <TextBox Grid.Column="0" x:Name="InputBox"
                     Background="#2D2D2D" Foreground="White" BorderBrush="#555"
                     FontSize="13" Padding="8,6"
                     KeyDown="InputBox_KeyDown"/>
            <Button Grid.Column="1" Content="Send" Margin="5,0,0,0"
                    Padding="12,6" Click="SendButton_Click"
                    Background="#0078D4" Foreground="White" BorderThickness="0"/>
            <Button Grid.Column="2" Content="Clear" Margin="5,0,0,0"
                    Padding="12,6" Click="ClearButton_Click"
                    Background="#444" Foreground="#CCC" BorderThickness="0"/>
        </Grid>
    </Grid>
</Window>
```

- [ ] **Step 2: Create AutopilotChatWindow.xaml.cs**

Create `MeetNow/AutopilotChatWindow.xaml.cs`:

```csharp
using System;
using System.Windows;
using System.Windows.Input;

namespace MeetNow
{
    public partial class AutopilotChatWindow : Window
    {
        private static AutopilotChatWindow? _instance;

        public AutopilotChatWindow()
        {
            InitializeComponent();
            AutopilotAgent.LogUpdated += RefreshLog;
            Closed += (_, _) =>
            {
                AutopilotAgent.LogUpdated -= RefreshLog;
                _instance = null;
            };
            RefreshLog();
        }

        public static void ShowOrActivate()
        {
            if (_instance != null)
            {
                _instance.Activate();
                return;
            }
            _instance = new AutopilotChatWindow();
            _instance.Show();
        }

        private void RefreshLog()
        {
            Dispatcher.InvokeAsync(() =>
            {
                var entries = AutopilotAgent.GetLog();
                LogList.Items.Clear();
                foreach (var entry in entries)
                    LogList.Items.Add(entry);

                if (LogList.Items.Count > 0)
                    LogList.ScrollIntoView(LogList.Items[^1]);

                var mode = AutopilotAgent.IsActive ? "Active" : "Passive";
                var conn = AutopilotAgent.IsConnected ? "Connected" : "Not connected";
                StatusText.Text = $"{mode} — {conn}";
            });
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            var text = InputBox.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            InputBox.Text = "";
            InputBox.IsEnabled = false;

            try
            {
                await AutopilotAgent.SendChatMessageAsync(text);
            }
            finally
            {
                InputBox.IsEnabled = true;
                InputBox.Focus();
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            AutopilotAgent.ClearInstructions();
        }

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                SendButton_Click(sender, e);
                e.Handled = true;
            }
        }
    }
}
```

- [ ] **Step 3: Add menu item to MainWindow.xaml**

In `MeetNow/MainWindow.xaml`, find the context menu (around line 14-26). Add after the `Autopilot` menu item:

```xml
<MenuItem Header="Autopilot Chat" Click="MenuItem_AutopilotChatClick"/>
```

- [ ] **Step 4: Add click handler to MainWindow.xaml.cs**

In `MeetNow/MainWindow.xaml.cs`, add a new method near the other MenuItem handlers:

```csharp
private void MenuItem_AutopilotChatClick(object sender, RoutedEventArgs e)
{
    AutopilotChatWindow.ShowOrActivate();
}
```

- [ ] **Step 5: Build and verify**

Run: `dotnet build MeetNow/MeetNow.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 6: Commit**

```bash
git add MeetNow/AutopilotChatWindow.xaml MeetNow/AutopilotChatWindow.xaml.cs MeetNow/MainWindow.xaml MeetNow/MainWindow.xaml.cs
git commit -m "feat: add Autopilot Chat window with instruction input and action log"
```

---

### Task 7: Wire startup/shutdown in App.xaml.cs

**Files:**
- Modify: `MeetNow/App.xaml.cs`

- [ ] **Step 1: Add AutopilotAgent.Start() on startup**

In `MeetNow/App.xaml.cs`, add after `McpServer.Start();` (line 51):

```csharp
            AutopilotAgent.Start();
```

- [ ] **Step 2: Add AutopilotAgent.Stop() on exit**

In the `OnExit` method (line 62-66), add `AutopilotAgent.Stop();` before `McpServer.Stop();`:

```csharp
        protected override void OnExit(ExitEventArgs e)
        {
            AutopilotAgent.Stop();
            McpServer.Stop();
            ScreenLockPrevention.Stop();
            base.OnExit(e);
        }
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build MeetNow/MeetNow.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 4: Commit**

```bash
git add MeetNow/App.xaml.cs
git commit -m "feat: wire AutopilotAgent startup/shutdown"
```

---

### Task 8: Smoke test

**Files:** None (manual verification)

- [ ] **Step 1: Kill running MeetNow**

Run: `taskkill //IM MeetNow.exe //F` (ignore if not running)

- [ ] **Step 2: Build and launch**

Run: `dotnet build MeetNow/MeetNow.csproj`
Then launch: `MeetNow/bin/Debug/net8.0-windows10.0.19041.0/win-x64/MeetNow.exe &`

- [ ] **Step 3: Verify MCP tools include new ones**

Run:
```bash
curl -s --noproxy localhost -X POST http://localhost:27182/messages -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' | grep -o '"name":"[^"]*"' | sort
```
Expected: Should include `enable_autopilot`, `disable_autopilot`, `send_instruction` alongside the original 10 tools.

- [ ] **Step 4: Test get_status shows new autopilotMode field**

Run:
```bash
curl -s --noproxy localhost -X POST http://localhost:27182/messages -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_status","arguments":{}}}'
```
Expected: Response includes `"autopilotMode":"passive"` (default state)

- [ ] **Step 5: Test send_instruction**

Run:
```bash
curl -s --noproxy localhost -X POST http://localhost:27182/messages -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"send_instruction","arguments":{"message":"Test instruction from curl"}}}'
```
Expected: `{"success":true}`

- [ ] **Step 6: Verify system prompt was created**

Check that `%LOCALAPPDATA%\MeetNow\autopilot-system.md` exists and contains the default prompt.

- [ ] **Step 7: Check logs for AutopilotAgent activity**

Look at `%TEMP%\MeetNow.log` for entries starting with "AutopilotAgent:". Should see startup, health check attempts.

- [ ] **Step 8: Push**

```bash
git push origin HEAD
```
