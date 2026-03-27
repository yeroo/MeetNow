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

                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        Log.Information("AutopilotAgent: session {Id} not found, will recreate", sessionId);
                        _sessionId = null;
                    }

                    return null;
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(responseJson);

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
                if (_log.Count > 200)
                    _log.RemoveAt(0);
            }
            try { LogUpdated?.Invoke(); }
            catch { }
        }
    }
}
