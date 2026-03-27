# MCP Server Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Embed an MCP server (SSE transport) inside MeetNow so LLM clients can read messages, meetings, contacts, and trigger Teams actions.

**Architecture:** An `HttpListener` on localhost serves SSE connections and JSON-RPC tool calls. Read-only tools query existing static APIs directly. Action tools route through `TeamsOperationQueue`. A `MessageHistory` ring buffer captures classified messages for querying.

**Tech Stack:** C# .NET 8, `System.Net.HttpListener`, `System.Text.Json`, MCP protocol (JSON-RPC 2.0 over SSE)

**Spec:** `docs/superpowers/specs/2026-03-27-mcp-server-design.md`

---

### Task 1: Thread-safety fix for ContactPriorityProvider

**Files:**
- Modify: `MeetNow/ContactPriorityProvider.cs`

This must be done first since the MCP server will call `SetPriority` directly (no queue) while other threads read.

- [ ] **Step 1: Replace Dictionary with ConcurrentDictionary**

In `MeetNow/ContactPriorityProvider.cs`, change the `_cache` field and all usages:

```csharp
// Line 30: change field type and use Lazy<T> for thread-safe initialization
private static readonly Lazy<ConcurrentDictionary<string, ContactPriority>> _cache = new(LoadFromDisk);
```

Replace `Load()` (line 32-51) with:
```csharp
private static ConcurrentDictionary<string, ContactPriority> Load() => _cache.Value;

private static ConcurrentDictionary<string, ContactPriority> LoadFromDisk()
{
    try
    {
        if (File.Exists(FilePath))
        {
            var json = File.ReadAllText(FilePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, ContactPriority>>(json);
            if (dict != null)
                return new ConcurrentDictionary<string, ContactPriority>(dict, StringComparer.OrdinalIgnoreCase);
        }
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Failed to load contact priorities");
    }

    return new ConcurrentDictionary<string, ContactPriority>(StringComparer.OrdinalIgnoreCase);
}
```

Update `SetPriority()` (line 84-96) — change `dict.Remove(sender)` to `dict.TryRemove(sender, out _)`.

Add `using System.Collections.Concurrent;` to the top of the file.

- [ ] **Step 2: Add GetAllOverrides method**

Add after `GetContactsByPriority()` (after line 111):

```csharp
/// <summary>
/// Returns all contacts with non-Default priority overrides.
/// </summary>
public static IReadOnlyDictionary<string, ContactPriority> GetAllOverrides()
{
    var dict = Load();
    return dict;
}
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build MeetNow/MeetNow.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 4: Commit**

```bash
git add MeetNow/ContactPriorityProvider.cs
git commit -m "refactor: make ContactPriorityProvider thread-safe for MCP"
```

---

### Task 2: Add McpPort setting

**Files:**
- Modify: `MeetNow/MeetNowSettings.cs`

- [ ] **Step 1: Add McpPort property**

In `MeetNow/MeetNowSettings.cs`, add after the WebView2 POC section (after line 35):

```csharp
// MCP server
public int McpPort { get; set; } = 27182;
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build MeetNow/MeetNow.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 3: Commit**

```bash
git add MeetNow/MeetNowSettings.cs
git commit -m "feat: add McpPort setting (default 27182)"
```

---

### Task 3: Create MessageHistory

**Files:**
- Create: `MeetNow/MessageHistory.cs`
- Modify: `MeetNow/MainWindow.xaml.cs` (line ~321, inside `OnTeamsMessageDetected`)

- [ ] **Step 1: Create MessageHistory.cs**

Create `MeetNow/MessageHistory.cs`:

```csharp
using MeetNow.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MeetNow
{
    /// <summary>
    /// In-memory ring buffer of recent Teams messages for MCP queries.
    /// Consumes already-classified messages (urgency + priority overrides applied).
    /// </summary>
    public static class MessageHistory
    {
        private const int MaxMessages = 500;
        private static readonly ConcurrentQueue<TeamsMessage> _messages = new();
        private static int _count;

        /// <summary>
        /// Add a classified message to the history. Called from MainWindow.OnTeamsMessageDetected
        /// after urgency classification and priority overrides have been applied.
        /// </summary>
        public static void Add(TeamsMessage message)
        {
            _messages.Enqueue(message);
            var count = System.Threading.Interlocked.Increment(ref _count);

            // Trim oldest when over capacity
            while (count > MaxMessages && _messages.TryDequeue(out _))
            {
                count = System.Threading.Interlocked.Decrement(ref _count);
            }
        }

        /// <summary>
        /// Query recent messages with optional filters.
        /// </summary>
        public static List<TeamsMessage> GetRecent(int minutes = 60, string? sender = null, MessageUrgency? urgency = null)
        {
            var cutoff = DateTime.Now.AddMinutes(-minutes);
            var results = new List<TeamsMessage>();

            foreach (var msg in _messages)
            {
                if (msg.Timestamp < cutoff) continue;

                if (sender != null && !msg.Sender.Contains(sender, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (urgency.HasValue && msg.Urgency != urgency.Value)
                    continue;

                results.Add(msg);
            }

            return results;
        }
    }
}
```

- [ ] **Step 2: Wire MessageHistory into MainWindow.OnTeamsMessageDetected**

In `MeetNow/MainWindow.xaml.cs`, add `MessageHistory.Add(message);` after line 321 (after `MessageSummaryWindow.AddMessage(message);`):

Find the line:
```csharp
                MessageSummaryWindow.AddMessage(message);
```

Add immediately after it:
```csharp
                MessageHistory.Add(message);
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build MeetNow/MeetNow.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 4: Commit**

```bash
git add MeetNow/MessageHistory.cs MeetNow/MainWindow.xaml.cs
git commit -m "feat: add MessageHistory ring buffer for MCP message queries"
```

---

### Task 4: Create McpServer — core SSE/JSON-RPC infrastructure

**Files:**
- Create: `MeetNow/McpServer.cs`

This task creates the HTTP listener, SSE connection handling, MCP handshake, and JSON-RPC dispatch loop — without tool implementations (those come in Tasks 5-7).

- [ ] **Step 1: Create McpServer.cs with infrastructure**

Create `MeetNow/McpServer.cs`:

```csharp
using MeetNow.Models;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MeetNow
{
    /// <summary>
    /// Embedded MCP server using SSE transport over HTTP.
    /// Exposes MeetNow capabilities to LLM clients (Claude Code, OpenCode).
    /// </summary>
    public static class McpServer
    {
        private static HttpListener? _listener;
        private static CancellationTokenSource? _cts;
        private static Thread? _listenerThread;
        private static int _activePort;

        // Track active SSE connections for cleanup
        private static readonly ConcurrentDictionary<string, SseConnection> _connections = new();

        private record SseConnection(
            string SessionId,
            HttpListenerResponse Response,
            StreamWriter Writer,
            CancellationTokenSource Cts);

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        public static int ActivePort => _activePort;

        public static void Start()
        {
            var port = MeetNowSettings.Instance.McpPort;
            _cts = new CancellationTokenSource();

            // Try configured port, then port+1 as fallback
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var tryPort = port + attempt;
                try
                {
                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://localhost:{tryPort}/");
                    _listener.Start();
                    _activePort = tryPort;
                    Log.Information("MCP server started on http://localhost:{Port}/", tryPort);
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "MCP server failed to start on port {Port}", tryPort);
                    _listener?.Close();
                    _listener = null;
                    if (attempt == 1)
                    {
                        Log.Error("MCP server: could not bind to any port, giving up");
                        return;
                    }
                }
            }

            // Write port file for client discovery
            try
            {
                var portFile = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MeetNow", "mcp_port");
                Directory.CreateDirectory(Path.GetDirectoryName(portFile)!);
                File.WriteAllText(portFile, _activePort.ToString());
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "MCP server: failed to write port file");
            }

            _listenerThread = new Thread(ListenerLoop) { IsBackground = true, Name = "McpServer" };
            _listenerThread.Start();
        }

        public static void Stop()
        {
            _cts?.Cancel();

            // Close all SSE connections
            foreach (var conn in _connections.Values)
            {
                try { conn.Cts.Cancel(); conn.Response.Close(); }
                catch { }
            }
            _connections.Clear();

            try { _listener?.Stop(); _listener?.Close(); }
            catch { }
            Log.Information("MCP server stopped");
        }

        private static void ListenerLoop()
        {
            while (_listener?.IsListening == true && !_cts!.IsCancellationRequested)
            {
                try
                {
                    var context = _listener.GetContext();
                    _ = Task.Run(() => HandleRequestAsync(context));
                }
                catch (HttpListenerException) when (_cts?.IsCancellationRequested == true)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "MCP server: listener error");
                }
            }
        }

        private static async Task HandleRequestAsync(HttpListenerContext context)
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            var method = context.Request.HttpMethod;

            // Add CORS headers for all responses
            context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            context.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            context.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            if (method == "OPTIONS")
            {
                context.Response.StatusCode = 204;
                context.Response.Close();
                return;
            }

            try
            {
                if (path == "/sse" && method == "GET")
                {
                    await HandleSseAsync(context);
                }
                else if (path.StartsWith("/messages") && method == "POST")
                {
                    await HandleJsonRpcAsync(context);
                }
                else if (path == "/health")
                {
                    var health = JsonSerializer.Serialize(new { status = "ok", port = _activePort }, JsonOpts);
                    var bytes = Encoding.UTF8.GetBytes(health);
                    context.Response.ContentType = "application/json";
                    context.Response.StatusCode = 200;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
                else
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "MCP server: request error on {Path}", path);
                try { context.Response.StatusCode = 500; context.Response.Close(); }
                catch { }
            }
        }

        private static async Task HandleSseAsync(HttpListenerContext context)
        {
            var sessionId = Guid.NewGuid().ToString("N");
            var messagesUrl = $"http://localhost:{_activePort}/messages?sessionId={sessionId}";

            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.Add("Cache-Control", "no-cache");
            context.Response.Headers.Add("Connection", "keep-alive");
            context.Response.StatusCode = 200;
            context.Response.SendChunked = true;

            var writer = new StreamWriter(context.Response.OutputStream, Encoding.UTF8) { AutoFlush = true };
            var connCts = CancellationTokenSource.CreateLinkedTokenSource(_cts!.Token);
            var conn = new SseConnection(sessionId, context.Response, writer, connCts);
            _connections[sessionId] = conn;

            Log.Information("MCP SSE client connected: {SessionId}", sessionId);

            try
            {
                // Send the endpoint event per MCP spec
                await writer.WriteAsync($"event: endpoint\ndata: {messagesUrl}\n\n");
                await writer.FlushAsync();

                // Keep-alive loop: send ping every 30s
                while (!connCts.IsCancellationRequested)
                {
                    await Task.Delay(30000, connCts.Token);
                    await writer.WriteAsync(": ping\n\n");
                    await writer.FlushAsync();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Debug(ex, "MCP SSE connection ended: {SessionId}", sessionId);
            }
            finally
            {
                _connections.TryRemove(sessionId, out _);
                try { context.Response.Close(); } catch { }
                Log.Information("MCP SSE client disconnected: {SessionId}", sessionId);
            }
        }

        private static async Task HandleJsonRpcAsync(HttpListenerContext context)
        {
            // Read session ID from query string
            var sessionId = context.Request.QueryString["sessionId"];

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();

            Log.Debug("MCP request: {Body}", body);

            JsonElement request;
            try
            {
                request = JsonSerializer.Deserialize<JsonElement>(body);
            }
            catch
            {
                await SendJsonRpcError(context, null, -32700, "Parse error");
                return;
            }

            var methodName = request.TryGetProperty("method", out var m) ? m.GetString() : null;
            var id = request.TryGetProperty("id", out var idProp) ? idProp.Clone() : (JsonElement?)null;
            var paramsEl = request.TryGetProperty("params", out var p) ? p : (JsonElement?)null;

            // Handle notifications (no id) — just acknowledge
            if (id == null)
            {
                // "initialized" notification — no response needed
                context.Response.StatusCode = 202;
                context.Response.Close();
                return;
            }

            object? result = null;
            string? errorMessage = null;
            int errorCode = 0;

            switch (methodName)
            {
                case "initialize":
                    result = HandleInitialize();
                    break;

                case "tools/list":
                    result = HandleToolsList();
                    break;

                case "tools/call":
                    var toolName = paramsEl?.TryGetProperty("name", out var tn) == true ? tn.GetString() : null;
                    var toolArgs = paramsEl?.TryGetProperty("arguments", out var ta) == true ? ta : (JsonElement?)null;
                    if (toolName == null)
                    {
                        errorCode = -32602;
                        errorMessage = "Missing tool name";
                    }
                    else
                    {
                        (result, errorCode, errorMessage) = HandleToolCall(toolName, toolArgs);
                    }
                    break;

                default:
                    errorCode = -32601;
                    errorMessage = $"Method not found: {methodName}";
                    break;
            }

            if (errorMessage != null)
            {
                await SendJsonRpcError(context, id, errorCode, errorMessage);
            }
            else
            {
                await SendJsonRpcResult(context, id!.Value, result);
            }

            // If SSE connection exists, send a notification
            if (sessionId != null && _connections.TryGetValue(sessionId, out var conn))
            {
                try
                {
                    var notification = JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/message", @params = new { level = "info", data = $"Handled {methodName}" } }, JsonOpts);
                    await conn.Writer.WriteAsync($"event: message\ndata: {notification}\n\n");
                    await conn.Writer.FlushAsync();
                }
                catch { }
            }
        }

        private static object HandleInitialize()
        {
            return new
            {
                protocolVersion = "2024-11-05",
                capabilities = new
                {
                    tools = new { }
                },
                serverInfo = new
                {
                    name = "meetnow",
                    version = BuildInfo.Number
                }
            };
        }

        private static object HandleToolsList()
        {
            return new { tools = GetToolDefinitions() };
        }

        private static (object? result, int errorCode, string? errorMessage) HandleToolCall(string toolName, JsonElement? args)
        {
            try
            {
                var result = DispatchTool(toolName, args);
                // MCP tools/call result format: { content: [{ type: "text", text: "..." }] }
                var json = JsonSerializer.Serialize(result, JsonOpts);
                return (new { content = new[] { new { type = "text", text = json } } }, 0, null);
            }
            catch (ArgumentException ex)
            {
                return (null, -32602, ex.Message);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "MCP tool error: {Tool}", toolName);
                return (null, -32603, $"Internal error: {ex.Message}");
            }
        }

        // DispatchTool and tool definitions will be added in Tasks 5-7
        private static object DispatchTool(string toolName, JsonElement? args)
        {
            throw new ArgumentException($"Unknown tool: {toolName}");
        }

        private static object[] GetToolDefinitions()
        {
            return Array.Empty<object>();
        }

        private static async Task SendJsonRpcResult(HttpListenerContext context, JsonElement id, object? result)
        {
            var response = new { jsonrpc = "2.0", id = id, result = result };
            var json = JsonSerializer.Serialize(response, JsonOpts);
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Response.ContentType = "application/json";
            context.Response.StatusCode = 200;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }

        private static async Task SendJsonRpcError(HttpListenerContext context, JsonElement? id, int code, string message)
        {
            var response = new { jsonrpc = "2.0", id = (object?)(id.HasValue ? id.Value : null), error = new { code, message } };
            var json = JsonSerializer.Serialize(response, JsonOpts);
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Response.ContentType = "application/json";
            context.Response.StatusCode = 200; // JSON-RPC errors still use HTTP 200
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }
}
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build MeetNow/MeetNow.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 3: Commit**

```bash
git add MeetNow/McpServer.cs
git commit -m "feat: add MCP server core — SSE listener, JSON-RPC dispatch, handshake"
```

---

### Task 5: Implement read-only MCP tools

**Files:**
- Modify: `MeetNow/McpServer.cs` — add `DispatchTool` cases and `GetToolDefinitions`

- [ ] **Step 1: Replace DispatchTool and GetToolDefinitions stubs**

In `MeetNow/McpServer.cs`, replace the `DispatchTool` method with:

```csharp
private static object DispatchTool(string toolName, JsonElement? args)
{
    return toolName switch
    {
        "get_messages" => ToolGetMessages(args),
        "get_meetings" => ToolGetMeetings(args),
        "get_contacts" => ToolGetContacts(args),
        "get_favorites" => ToolGetFavorites(),
        "get_contact_priorities" => ToolGetContactPriorities(args),
        "get_status" => ToolGetStatus(),
        "set_availability" => ToolSetAvailability(args),
        "send_message" => ToolSendMessage(args),
        "simulate_typing" => ToolSimulateTyping(args),
        "set_contact_priority" => ToolSetContactPriority(args),
        _ => throw new ArgumentException($"Unknown tool: {toolName}")
    };
}
```

Replace `GetToolDefinitions` with the full tool schema array. Each tool definition follows the MCP JSON Schema format:

```csharp
private static object[] GetToolDefinitions()
{
    return new object[]
    {
        new
        {
            name = "get_messages",
            description = "Query recent Teams messages. Returns sender, content, timestamp, urgency, and thread type.",
            inputSchema = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["sender"] = new { type = "string", description = "Filter by sender name (partial match)" },
                    ["urgency"] = new { type = "string", description = "Filter by urgency: Urgent, Normal, Low", @enum = new[] { "Urgent", "Normal", "Low" } },
                    ["minutes"] = new { type = "integer", description = "How far back to look (default 60)", @default = 60 }
                }
            }
        },
        new
        {
            name = "get_meetings",
            description = "Get calendar meetings for a date. Returns subject, start/end times, organizer, attendees, Teams join URL.",
            inputSchema = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["date"] = new { type = "string", description = "Date in YYYY-MM-DD format (default today)" }
                }
            }
        },
        new
        {
            name = "get_contacts",
            description = "Search or list enriched contacts with name, email, job title, department, phone.",
            inputSchema = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["query"] = new { type = "string", description = "Search by name or email (min 3 chars)" },
                    ["pinned_only"] = new { type = "boolean", description = "Only return pinned contacts (default false)" }
                }
            }
        },
        new
        {
            name = "get_favorites",
            description = "List favorite contacts from Teams.",
            inputSchema = new { type = "object", properties = new Dictionary<string, object>() }
        },
        new
        {
            name = "get_contact_priorities",
            description = "List per-contact urgency priority overrides (Urgent, Normal, Low, or Default).",
            inputSchema = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["priority"] = new { type = "string", description = "Filter by priority level", @enum = new[] { "Urgent", "Normal", "Low", "Default" } }
                }
            }
        },
        new
        {
            name = "get_status",
            description = "Get current MeetNow status: autopilot state, pending auto-replies, operation queue.",
            inputSchema = new { type = "object", properties = new Dictionary<string, object>() }
        },
        new
        {
            name = "set_availability",
            description = "Set Teams presence status (Available, Busy, Away, DoNotDisturb, BeRightBack). Queued for sequential execution.",
            inputSchema = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["status"] = new { type = "string", description = "Teams status to set", @enum = new[] { "Available", "Busy", "Away", "DoNotDisturb", "BeRightBack" } }
                },
                required = new[] { "status" }
            }
        },
        new
        {
            name = "send_message",
            description = "Send a Teams message to a contact. Queued for sequential execution.",
            inputSchema = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["recipient"] = new { type = "string", description = "Contact name or email" },
                    ["message"] = new { type = "string", description = "Message text to send" }
                },
                required = new[] { "recipient", "message" }
            }
        },
        new
        {
            name = "simulate_typing",
            description = "Show 'is typing...' indicator to a contact without sending. Queued, subject to cooldown.",
            inputSchema = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["recipient"] = new { type = "string", description = "Contact name or email" }
                },
                required = new[] { "recipient" }
            }
        },
        new
        {
            name = "set_contact_priority",
            description = "Set or remove urgency priority override for a contact. Executes immediately.",
            inputSchema = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["sender"] = new { type = "string", description = "Contact name" },
                    ["priority"] = new { type = "string", description = "Priority level", @enum = new[] { "Urgent", "Normal", "Low", "Default" } }
                },
                required = new[] { "sender", "priority" }
            }
        }
    };
}
```

- [ ] **Step 2: Add read-only tool implementations**

Add these methods to `McpServer.cs`:

```csharp
// --- Read-only tool implementations ---

private static object ToolGetMessages(JsonElement? args)
{
    var minutes = args?.TryGetProperty("minutes", out var m) == true ? m.GetInt32() : 60;
    var sender = args?.TryGetProperty("sender", out var s) == true ? s.GetString() : null;
    MessageUrgency? urgency = null;
    if (args?.TryGetProperty("urgency", out var u) == true)
    {
        if (Enum.TryParse<MessageUrgency>(u.GetString(), true, out var parsed))
            urgency = parsed;
        else
            throw new ArgumentException($"Invalid urgency: {u.GetString()}. Use Urgent, Normal, or Low.");
    }

    var messages = MessageHistory.GetRecent(minutes, sender, urgency);
    return messages.Select(msg => new
    {
        sender = msg.Sender,
        content = msg.Content,
        timestamp = msg.Timestamp.ToString("o"),
        threadType = msg.ThreadType,
        isMention = msg.IsMention,
        urgency = msg.Urgency.ToString(),
        urgencyReason = msg.UrgencyReason
    }).ToArray();
}

private static string MapResponseStatus(ResponseStatus status)
{
    return status switch
    {
        ResponseStatus.olResponseNone => "None",
        ResponseStatus.olResponseOrganized => "Organized",
        ResponseStatus.olResponseTentative => "Tentative",
        ResponseStatus.olResponseAccepted => "Accepted",
        ResponseStatus.olResponseDeclined => "Declined",
        ResponseStatus.olResponseNotResponded => "NotResponded",
        _ => status.ToString()
    };
}

private static object ToolGetMeetings(JsonElement? args)
{
    DateTime date = DateTime.Today;
    if (args?.TryGetProperty("date", out var d) == true)
    {
        if (!DateTime.TryParse(d.GetString(), out date))
            throw new ArgumentException($"Invalid date format: {d.GetString()}. Use YYYY-MM-DD.");
    }

    var aggregator = new MeetingDataAggregator();
    var meetings = aggregator.GetMeetings(date, MeetNowSettings.Instance.OutlookSource);

    return meetings.Select(m => new
    {
        subject = m.Subject,
        start = m.Start.ToString("o"),
        end = m.End.ToString("o"),
        organizer = m.Organizer,
        location = m.Location,
        teamsUrl = m.TeamsUrl,
        responseStatus = MapResponseStatus(m.ResponseStatus),
        isRequired = m.IsRequired,
        recurrent = m.Recurrent,
        requiredAttendees = m.RequiredAttendees,
        optionalAttendees = m.OptionalAttendees
    }).ToArray();
}

private static object ToolGetContacts(JsonElement? args)
{
    var query = args?.TryGetProperty("query", out var q) == true ? q.GetString() : null;
    var pinnedOnly = args?.TryGetProperty("pinned_only", out var po) == true && po.GetBoolean();

    List<Contact> contacts;
    if (pinnedOnly)
        contacts = ContactDatabase.GetPinned();
    else if (!string.IsNullOrWhiteSpace(query))
        contacts = ContactDatabase.GetByName(query);
    else
        contacts = ContactDatabase.GetAll();

    return contacts.Select(c => new
    {
        teamsUserId = c.TeamsUserId,
        displayName = c.DisplayName,
        email = c.Email,
        jobTitle = c.JobTitle,
        department = c.Department,
        phone = c.Phone,
        isPinned = c.IsPinned,
        lastSeen = c.LastSeenTimestamp.ToString("o")
    }).ToArray();
}

private static object ToolGetFavorites()
{
    var favorites = FavoriteContactsProvider.GetFavoriteContactNames();
    return favorites.OrderBy(n => n).ToArray();
}

private static object ToolGetContactPriorities(JsonElement? args)
{
    if (args?.TryGetProperty("priority", out var p) == true)
    {
        if (!Enum.TryParse<ContactPriorityProvider.ContactPriority>(p.GetString(), true, out var priority))
            throw new ArgumentException($"Invalid priority: {p.GetString()}. Use Urgent, Normal, Low, or Default.");

        return ContactPriorityProvider.GetContactsByPriority(priority)
            .Select(s => new { sender = s, priority = priority.ToString() })
            .ToArray();
    }

    // No filter — return all overrides
    var all = ContactPriorityProvider.GetAllOverrides();
    return all.Select(kvp => new { sender = kvp.Key, priority = kvp.Value.ToString() }).ToArray();
}

private static object ToolGetStatus()
{
    var pending = TeamsOperationQueue.PendingSnapshot;
    var current = TeamsOperationQueue.Current;

    return new
    {
        autopilotActive = AutopilotOverlay.IsActive,
        pendingAutoReplies = AutopilotOverlay.GetPendingAutoReplies()
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToString("o")),
        queueCurrent = current?.Description,
        queueCurrentStep = TeamsOperationQueue.CurrentStep,
        queuePending = pending.Select(e => e.Description).ToArray(),
        isExecuting = TeamsOperationQueue.IsExecuting
    };
}
```

Also add `using System.Linq;` to the top of the file if not already present.

- [ ] **Step 3: Build and verify**

Run: `dotnet build MeetNow/MeetNow.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 4: Commit**

```bash
git add MeetNow/McpServer.cs
git commit -m "feat: implement read-only MCP tools (messages, meetings, contacts, status)"
```

---

### Task 6: Implement action MCP tools

**Files:**
- Modify: `MeetNow/McpServer.cs` — add action tool methods

- [ ] **Step 1: Add action tool implementations**

Add these methods to `McpServer.cs`:

```csharp
// --- Action tool implementations ---

private static object ToolSetAvailability(JsonElement? args)
{
    var statusStr = args?.TryGetProperty("status", out var s) == true ? s.GetString() : null;
    if (string.IsNullOrEmpty(statusStr))
        throw new ArgumentException("Missing required parameter: status");

    if (!Enum.TryParse<TeamsStatusManager.TeamsStatus>(statusStr, true, out var status))
        throw new ArgumentException($"Invalid status: {statusStr}. Use Available, Busy, Away, DoNotDisturb, or BeRightBack.");

    var description = $"Set Teams {status}";
    TeamsOperationQueue.Enqueue(description,
        () => TeamsStatusManager.SetStatusAsync(status));

    return new { queued = true, description };
}

private static object ToolSendMessage(JsonElement? args)
{
    var recipient = args?.TryGetProperty("recipient", out var r) == true ? r.GetString() : null;
    var message = args?.TryGetProperty("message", out var m) == true ? m.GetString() : null;

    if (string.IsNullOrEmpty(recipient))
        throw new ArgumentException("Missing required parameter: recipient");
    if (string.IsNullOrEmpty(message))
        throw new ArgumentException("Missing required parameter: message");

    var description = $"Send '{message}' to {recipient}";
    TeamsOperationQueue.Enqueue(description,
        () => TeamsStatusManager.SendMessageAsync(recipient, message));

    return new { queued = true, description };
}

private static object ToolSimulateTyping(JsonElement? args)
{
    var recipient = args?.TryGetProperty("recipient", out var r) == true ? r.GetString() : null;

    if (string.IsNullOrEmpty(recipient))
        throw new ArgumentException("Missing required parameter: recipient");

    if (!TeamsOperationQueue.TryClaimSimulateTyping(recipient))
    {
        return new { skipped = true, reason = $"Cooldown active for {recipient}" };
    }

    var description = $"Simulate typing to {recipient}";
    TeamsOperationQueue.Enqueue(description,
        () => TeamsStatusManager.SimulateTypingAsync(recipient));

    return new { queued = true, description };
}

private static object ToolSetContactPriority(JsonElement? args)
{
    var sender = args?.TryGetProperty("sender", out var s) == true ? s.GetString() : null;
    var priorityStr = args?.TryGetProperty("priority", out var p) == true ? p.GetString() : null;

    if (string.IsNullOrEmpty(sender))
        throw new ArgumentException("Missing required parameter: sender");
    if (string.IsNullOrEmpty(priorityStr))
        throw new ArgumentException("Missing required parameter: priority");

    if (!Enum.TryParse<ContactPriorityProvider.ContactPriority>(priorityStr, true, out var priority))
        throw new ArgumentException($"Invalid priority: {priorityStr}. Use Urgent, Normal, Low, or Default.");

    ContactPriorityProvider.SetPriority(sender, priority);

    return new { success = true };
}
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build MeetNow/MeetNow.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 3: Commit**

```bash
git add MeetNow/McpServer.cs
git commit -m "feat: implement action MCP tools (availability, messaging, priorities)"
```

---

### Task 7: Wire up startup, shutdown, and CLAUDE.md

**Files:**
- Modify: `MeetNow/App.xaml.cs`
- Create: `CLAUDE.md`

- [ ] **Step 1: Add McpServer.Start() to App.OnStartup**

In `MeetNow/App.xaml.cs`, add after `ScreenLockPrevention.Start();` (line 50):

```csharp
            McpServer.Start();
```

- [ ] **Step 2: Add McpServer.Stop() on exit**

In `MeetNow/App.xaml.cs`, add an `OnExit` override after the `OnStartup` method (after line 59):

```csharp
        protected override void OnExit(ExitEventArgs e)
        {
            McpServer.Stop();
            ScreenLockPrevention.Stop();
            base.OnExit(e);
        }
```

- [ ] **Step 3: Create CLAUDE.md**

Create `CLAUDE.md` at the repo root:

```markdown
# MeetNow Development Guidelines

## MCP Server Rule

When adding new features that expose data or actions, also add corresponding MCP tools in `MeetNow/McpServer.cs`:
1. Add a `ToolXxx` method implementing the tool logic
2. Add a case to `DispatchTool()` switch
3. Add a tool definition to `GetToolDefinitions()`
4. Action tools must go through `TeamsOperationQueue.Enqueue()`, read-only tools call APIs directly

## Build

```
dotnet build MeetNow/MeetNow.csproj
```

## Constraints

- No Microsoft Graph API (requires IT approval)
- No registry changes (IT monitors registry)
- No external API dependencies (everything must work locally/offline)
- No certificates/code signing (IT noise)
- No speculative API calls that return 401/403 (IT monitors auth failures)
- User-level privileges only (no admin)
```

- [ ] **Step 4: Build and verify full project**

Run: `dotnet build MeetNow/MeetNow.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 5: Commit**

```bash
git add MeetNow/App.xaml.cs CLAUDE.md
git commit -m "feat: wire MCP server startup/shutdown, add CLAUDE.md"
```

---

### Task 8: Smoke test and verify

**Files:** None (manual verification)

- [ ] **Step 1: Kill running MeetNow if any**

Run: `taskkill //IM MeetNow.exe //F` (ignore if not running)

- [ ] **Step 2: Build and launch**

Run: `dotnet build MeetNow/MeetNow.csproj && MeetNow/bin/Debug/net8.0-windows10.0.19041.0/win-x64/MeetNow.exe &`

- [ ] **Step 3: Verify MCP server is listening**

Run: `curl -s http://localhost:27182/health`
Expected: `{"status":"ok","port":27182}`

- [ ] **Step 4: Verify tools/list works**

Run:
```bash
curl -s -X POST http://localhost:27182/messages -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```
Expected: JSON response with all 10 tool definitions

- [ ] **Step 5: Test a read-only tool**

Run:
```bash
curl -s -X POST http://localhost:27182/messages -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_status","arguments":{}}}'
```
Expected: JSON with `autopilotActive`, `queueCurrent`, etc.

- [ ] **Step 6: Test get_meetings**

Run:
```bash
curl -s -X POST http://localhost:27182/messages -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_meetings","arguments":{}}}'
```
Expected: JSON array of today's meetings (may be empty if no meetings)

- [ ] **Step 7: Commit all remaining changes and push**

```bash
git push origin HEAD
```
