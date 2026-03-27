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
