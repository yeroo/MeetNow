using Microsoft.Web.WebView2.Core;
using MeetNow.Models;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace MeetNow
{
    public class TeamsWebViewDataExtractor
    {
        private static readonly string TrafficLogPath = Path.Combine(
            Path.GetTempPath(), "MeetNow_WebView_Traffic.log");

        // URL patterns to capture response bodies for
        private static readonly string[] InterestingPatterns = new[]
        {
            "/api/calendar/",
            "/me/calendarview",
            "/api/chatsvc/",
            "/messages",
            "/threads",
            "/presence/",
            "/status",
            "/api/mt/",
            "/api/csa/",
        };

        private CoreWebView2? _webView;
        private readonly bool _logAllTraffic;
        private readonly ConcurrentBag<TeamsMeeting> _meetings = new();
        private readonly ConcurrentBag<TeamsMessage> _messages = new();
        private DispatcherTimer? _jsProbeTimer;
        private bool _jsProbingStarted;

        public event Action<TeamsMeeting>? MeetingDetected;
        public event Action<TeamsMessage>? MessageDetected;

        public TeamsWebViewDataExtractor(bool logAllTraffic = true)
        {
            _logAllTraffic = logAllTraffic;
        }

        public void Attach(CoreWebView2 webView)
        {
            _webView = webView;
            _webView.WebResourceResponseReceived += OnResponseReceived;
            Log.Information("TeamsWebViewDataExtractor attached to WebView2");

            if (_logAllTraffic)
            {
                Log.Information("Traffic logging enabled: {Path}", TrafficLogPath);
            }
        }

        public void Detach()
        {
            if (_webView != null)
            {
                _webView.WebResourceResponseReceived -= OnResponseReceived;
                _webView = null;
            }
        }

        public IReadOnlyList<TeamsMeeting> GetMeetings() => _meetings.ToList();
        public IReadOnlyList<TeamsMessage> GetMessages() => _messages.ToList();

        private async void OnResponseReceived(object? sender,
            CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            try
            {
                var uri = e.Request.Uri;
                var status = e.Response.StatusCode;
                var contentType = e.Response.Headers.GetHeader("Content-Type") ?? "";

                // Log all traffic if enabled
                if (_logAllTraffic)
                {
                    var method = e.Request.Method;
                    LogTraffic($"{method} {status} {contentType} {uri}");
                }

                // Only process JSON responses from interesting endpoints
                if (status < 200 || status >= 300) return;
                if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase)) return;
                if (!IsInteresting(uri)) return;

                // Try to read the response body
                var body = await ReadResponseBody(e.Response);
                if (body == null) return;

                LogTraffic($"  BODY ({body.Length} chars): {Truncate(body, 500)}");
                Log.Debug("Captured response from {Uri} ({Length} chars)", uri, body.Length);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error processing WebView2 response");
            }
        }

        private static async Task<string?> ReadResponseBody(CoreWebView2WebResourceResponseView response)
        {
            try
            {
                var stream = await response.GetContentAsync();
                if (stream == null) return null;

                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not read response body");
                return null;
            }
        }

        private static bool IsInteresting(string uri)
        {
            return InterestingPatterns.Any(p =>
                uri.Contains(p, StringComparison.OrdinalIgnoreCase));
        }

        private static void LogTraffic(string line)
        {
            try
            {
                File.AppendAllText(TrafficLogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
            }
            catch
            {
                // Swallow file write errors — logging is best-effort
            }
        }

        private static string Truncate(string s, int maxLength)
        {
            return s.Length <= maxLength ? s : s[..maxLength] + "...";
        }

        private bool _hooksInjected;

        public async void StartJsProbing(int intervalSeconds = 30)
        {
            if (_jsProbingStarted) return; // Guard against multiple navigation events
            _jsProbingStarted = true;

            // Inject WebSocket hook and webpack explorer once
            if (!_hooksInjected && _webView != null)
            {
                await InjectWebSocketHook();
                await InjectWebpackExplorer();
                _hooksInjected = true;
            }

            _jsProbeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(intervalSeconds)
            };
            _jsProbeTimer.Tick += async (s, e) => await ProbeTeamsStateAsync();
            _jsProbeTimer.Start();

            Log.Information("JS state probing started (every {Interval}s)", intervalSeconds);
        }

        public void StopJsProbing()
        {
            _jsProbeTimer?.Stop();
            _jsProbeTimer = null;
            _jsProbingStarted = false;
        }

        /// <summary>
        /// Monkey-patch WebSocket to intercept all frames sent/received by Teams.
        /// Stores recent messages in window.__meetNowWsMessages (circular buffer).
        /// </summary>
        private async Task InjectWebSocketHook()
        {
            if (_webView == null) return;

            var hookJs = @"
(function() {
    if (window.__meetNowWsHooked) return 'already_hooked';
    window.__meetNowWsHooked = true;
    window.__meetNowWsMessages = [];
    window.__meetNowWsStats = { sent: 0, received: 0, connections: 0 };
    var MAX_MESSAGES = 200;

    var OrigWS = window.WebSocket;
    window.WebSocket = function(url, protocols) {
        var ws = protocols ? new OrigWS(url, protocols) : new OrigWS(url);
        window.__meetNowWsStats.connections++;

        var pushMsg = function(dir, data) {
            var entry = { dir: dir, ts: Date.now(), url: url };
            if (typeof data === 'string') {
                entry.data = data.substring(0, 2000);
                entry.len = data.length;
            } else {
                entry.data = '[binary ' + (data.byteLength || data.size || '?') + ' bytes]';
                entry.len = data.byteLength || data.size || 0;
            }
            window.__meetNowWsMessages.push(entry);
            if (window.__meetNowWsMessages.length > MAX_MESSAGES) {
                window.__meetNowWsMessages.shift();
            }
        };

        // Intercept send
        var origSend = ws.send.bind(ws);
        ws.send = function(data) {
            window.__meetNowWsStats.sent++;
            pushMsg('out', data);
            return origSend(data);
        };

        // Intercept incoming messages
        ws.addEventListener('message', function(event) {
            window.__meetNowWsStats.received++;
            pushMsg('in', event.data);
        });

        return ws;
    };
    window.WebSocket.prototype = OrigWS.prototype;
    window.WebSocket.CONNECTING = OrigWS.CONNECTING;
    window.WebSocket.OPEN = OrigWS.OPEN;
    window.WebSocket.CLOSING = OrigWS.CLOSING;
    window.WebSocket.CLOSED = OrigWS.CLOSED;

    return 'hooked';
})();";

            try
            {
                var result = await _webView.ExecuteScriptAsync(hookJs);
                LogTraffic($"  WS_HOOK: {result}");
                Log.Information("WebSocket hook injection result: {Result}", result);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to inject WebSocket hook");
            }
        }

        /// <summary>
        /// Tap into webpack's module cache to find Teams internal state.
        /// Stores discovered modules in window.__meetNowWebpackInfo.
        /// </summary>
        private async Task InjectWebpackExplorer()
        {
            if (_webView == null) return;

            var explorerJs = @"
(function() {
    if (window.__meetNowWebpackExplored) return 'already_explored';
    window.__meetNowWebpackExplored = true;
    window.__meetNowWebpackInfo = { modules: 0, interesting: [] };

    try {
        // Tap into webpack chunk loading to get __webpack_require__
        var chunkName = 'webpackChunk_msteams_react_web_client';
        var chunks = window[chunkName];
        if (!chunks) return JSON.stringify({ error: 'no chunks found' });

        // Push a fake module to get access to __webpack_require__
        var wpRequire = null;
        chunks.push([['__meetNow_probe__'], {}, function(require) {
            wpRequire = require;
        }]);

        if (!wpRequire || !wpRequire.c) {
            return JSON.stringify({ error: 'could not get webpack require', hasRequire: !!wpRequire });
        }

        var moduleCache = wpRequire.c;
        var moduleIds = Object.keys(moduleCache);
        window.__meetNowWebpackInfo.modules = moduleIds.length;
        window.__meetNowWebpackRequire = wpRequire;

        // Scan modules for interesting exports
        var interesting = [];
        for (var i = 0; i < moduleIds.length && interesting.length < 50; i++) {
            try {
                var mod = moduleCache[moduleIds[i]];
                if (!mod || !mod.exports) continue;
                var exp = mod.exports;
                var keys = Object.keys(exp);

                // Look for stores, services, state containers
                for (var j = 0; j < keys.length; j++) {
                    var k = keys[j].toLowerCase();
                    if (k.indexOf('calendar') >= 0 || k.indexOf('presence') >= 0 ||
                        k.indexOf('chat') >= 0 || k.indexOf('message') >= 0 ||
                        k.indexOf('meeting') >= 0 || k.indexOf('status') >= 0 ||
                        k.indexOf('store') >= 0 || k.indexOf('state') >= 0 ||
                        k.indexOf('event') >= 0 || k.indexOf('notification') >= 0) {
                        var val = exp[keys[j]];
                        var valType = typeof val;
                        if (valType === 'function' || valType === 'object') {
                            interesting.push({
                                moduleId: moduleIds[i],
                                key: keys[j],
                                type: valType,
                                isClass: valType === 'function' && /^class\s/.test(val.toString().substring(0, 10)),
                                proto: valType === 'object' && val !== null ? Object.keys(val).slice(0, 10) : null
                            });
                        }
                    }
                }
            } catch(e) { /* skip inaccessible modules */ }
        }
        window.__meetNowWebpackInfo.interesting = interesting;
        return JSON.stringify({ modules: moduleIds.length, interesting: interesting.length });
    } catch(e) {
        return JSON.stringify({ error: e.message });
    }
})();";

            try
            {
                var result = await _webView.ExecuteScriptAsync(explorerJs);
                var inner = result != null && result != "null"
                    ? JsonSerializer.Deserialize<string>(result) : result;
                LogTraffic($"  WEBPACK_EXPLORE: {inner}");
                Log.Information("Webpack explorer result: {Result}", inner);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to inject webpack explorer");
            }
        }

        private async Task ProbeTeamsStateAsync()
        {
            if (_webView == null) return;

            try
            {
                // Probe 1: Harvest ALL WebSocket messages (log every frame)
                var wsHarvestJs = @"
(function() {
    try {
        var result = { stats: window.__meetNowWsStats || {} };
        var msgs = window.__meetNowWsMessages || [];
        result.totalBuffered = msgs.length;

        // Log ALL messages, not just filtered ones
        result.messages = [];
        for (var i = 0; i < msgs.length; i++) {
            var m = msgs[i];
            // Parse Trouter frame format: '5:N+::{json}' or '5:N::{json}'
            var parsed = null;
            if (typeof m.data === 'string' && m.data.indexOf('::{') >= 0) {
                try {
                    var jsonStart = m.data.indexOf('::{') + 2;
                    parsed = JSON.parse(m.data.substring(jsonStart));
                } catch(e) {}
            } else if (typeof m.data === 'string' && m.data.charAt(0) === '{') {
                try { parsed = JSON.parse(m.data); } catch(e) {}
            }
            result.messages.push({
                dir: m.dir,
                ts: m.ts,
                url: m.url ? m.url.substring(0, 60) : '',
                len: m.len,
                name: parsed && parsed.name ? parsed.name : null,
                preview: typeof m.data === 'string' ? m.data.substring(0, 300) : m.data
            });
        }

        window.__meetNowWsMessages = [];
        return JSON.stringify(result);
    } catch(e) {
        return JSON.stringify({ error: e.message });
    }
})();";

                var wsResult = await _webView.ExecuteScriptAsync(wsHarvestJs);
                if (wsResult != null && wsResult != "null")
                {
                    var inner = JsonSerializer.Deserialize<string>(wsResult);
                    if (inner != null && !inner.Contains("\"messages\":[]"))
                    {
                        LogTraffic($"  WS_ALL: {Truncate(inner, 4000)}");
                    }
                    else if (inner != null)
                    {
                        using var doc = JsonDocument.Parse(inner);
                        if (doc.RootElement.TryGetProperty("stats", out var stats))
                            LogTraffic($"  WS_STATS: {stats}");
                    }
                }

                // Probe 2: Read React Context _currentValue for app state
                var contextJs = @"
(function() {
    try {
        var wpReq = window.__meetNowWebpackRequire;
        if (!wpReq || !wpReq.c) return JSON.stringify({ error: 'no webpack require' });

        var cache = wpReq.c;
        var mod = cache['93952'] || cache['101262'];
        if (!mod || !mod.exports) return JSON.stringify({ error: 'context module not found' });

        var exp = mod.exports;
        var result = {};

        // Read AppStateContext._currentValue
        if (exp.AppStateContext && exp.AppStateContext._currentValue) {
            var appState = exp.AppStateContext._currentValue;
            var stateKeys = Object.keys(appState);
            result.appStateKeys = stateKeys;
            result.appStateType = typeof appState;

            // Drill into each key to find calendar/presence/chat data
            var drilled = {};
            for (var i = 0; i < stateKeys.length; i++) {
                var k = stateKeys[i];
                var v = appState[k];
                if (v === null || v === undefined) {
                    drilled[k] = null;
                } else if (typeof v === 'string' || typeof v === 'number' || typeof v === 'boolean') {
                    drilled[k] = v;
                } else if (typeof v === 'object') {
                    drilled[k] = { type: Array.isArray(v) ? 'array(' + v.length + ')' : 'object', keys: Object.keys(v).slice(0, 15) };
                } else if (typeof v === 'function') {
                    drilled[k] = { type: 'function', name: v.name || '(anon)' };
                }
            }
            result.appState = drilled;
        } else {
            result.appStateContext = exp.AppStateContext ? '_currentValue is ' + typeof (exp.AppStateContext._currentValue) : 'missing';
        }

        // Read ClientStateContext._currentValue
        if (exp.ClientStateContext && exp.ClientStateContext._currentValue) {
            var clientState = exp.ClientStateContext._currentValue;
            var cKeys = Object.keys(clientState);
            result.clientStateKeys = cKeys;
            var cDrilled = {};
            for (var i = 0; i < cKeys.length; i++) {
                var k = cKeys[i];
                var v = clientState[k];
                if (v === null || v === undefined) {
                    cDrilled[k] = null;
                } else if (typeof v === 'string' || typeof v === 'number' || typeof v === 'boolean') {
                    cDrilled[k] = String(v).substring(0, 200);
                } else if (typeof v === 'object') {
                    cDrilled[k] = { type: Array.isArray(v) ? 'array(' + v.length + ')' : 'object', keys: Object.keys(v).slice(0, 15) };
                } else if (typeof v === 'function') {
                    cDrilled[k] = { type: 'function', name: v.name || '(anon)' };
                }
            }
            result.clientState = cDrilled;
        }

        return JSON.stringify(result);
    } catch(e) {
        return JSON.stringify({ error: e.message, stack: e.stack ? e.stack.substring(0, 200) : '' });
    }
})();";

                try
                {
                    var ctxResult = await _webView.ExecuteScriptAsync(contextJs);
                    LogTraffic($"  REACT_CTX_RAW: {Truncate(ctxResult ?? "null", 4000)}");
                    if (ctxResult != null && ctxResult != "null" && ctxResult != "\"null\"")
                    {
                        var inner = JsonSerializer.Deserialize<string>(ctxResult);
                        if (inner != null)
                            LogTraffic($"  REACT_CTX: {Truncate(inner, 4000)}");
                    }
                }
                catch (Exception ctxEx)
                {
                    LogTraffic($"  REACT_CTX_ERROR: {ctxEx.Message}");
                }

                // Probe 4: Deep webpack scan — dump ALL 78 interesting modules
                var deepScanJs = @"
(function() {
    try {
        var wpReq = window.__meetNowWebpackRequire;
        if (!wpReq || !wpReq.c) return JSON.stringify({ error: 'no webpack' });

        var cache = wpReq.c;
        var ids = Object.keys(cache);
        var found = [];

        for (var i = 0; i < ids.length; i++) {
            try {
                var mod = cache[ids[i]];
                if (!mod || !mod.exports) continue;
                var exp = mod.exports;
                var keys = Object.keys(exp);
                var hasInteresting = false;

                for (var j = 0; j < keys.length; j++) {
                    var kl = keys[j].toLowerCase();
                    if (kl.indexOf('calendar') >= 0 || kl.indexOf('presence') >= 0 ||
                        kl.indexOf('chat') >= 0 || kl.indexOf('message') >= 0 ||
                        kl.indexOf('meeting') >= 0 || kl.indexOf('status') >= 0 ||
                        kl.indexOf('availability') >= 0 || kl.indexOf('notification') >= 0 ||
                        kl.indexOf('conversation') >= 0 || kl.indexOf('contact') >= 0) {
                        hasInteresting = true;
                        break;
                    }
                }
                if (!hasInteresting) continue;

                // Dump this module's full export structure
                var modInfo = { id: ids[i], exports: {} };
                for (var j = 0; j < keys.length; j++) {
                    var k = keys[j];
                    var v = exp[k];
                    if (v === null || v === undefined) {
                        modInfo.exports[k] = null;
                    } else if (typeof v === 'string' || typeof v === 'number') {
                        modInfo.exports[k] = String(v).substring(0, 100);
                    } else if (typeof v === 'function') {
                        modInfo.exports[k] = 'fn(' + v.length + ')' + (v.name ? ':' + v.name : '');
                    } else if (typeof v === 'object') {
                        var subKeys = Object.keys(v).slice(0, 20);
                        var methods = subKeys.filter(function(sk) { return typeof v[sk] === 'function'; });
                        modInfo.exports[k] = { keys: subKeys, methods: methods, isArray: Array.isArray(v), len: Array.isArray(v) ? v.length : subKeys.length };
                    }
                }
                found.push(modInfo);
                if (found.length >= 40) break;
            } catch(e) {}
        }
        return JSON.stringify({ total: found.length, modules: found });
    } catch(e) {
        return JSON.stringify({ error: e.message });
    }
})();";

                try
                {
                    var deepResult = await _webView.ExecuteScriptAsync(deepScanJs);
                    LogTraffic($"  WEBPACK_DEEP_RAW_LEN: {deepResult?.Length ?? 0}");
                    if (deepResult != null && deepResult != "null")
                    {
                        var inner = JsonSerializer.Deserialize<string>(deepResult);
                        if (inner != null)
                            LogTraffic($"  WEBPACK_DEEP: {Truncate(inner, 6000)}");
                    }
                }
                catch (Exception wpEx)
                {
                    LogTraffic($"  WEBPACK_DEEP_ERROR: {wpEx.Message}");
                }

                // Probe 4: Title
                var titleJs = @"(function() { return JSON.stringify({ title: document.title }); })();";
                var titleResult = await _webView.ExecuteScriptAsync(titleJs);
                if (titleResult != null && titleResult != "null")
                {
                    var inner = JsonSerializer.Deserialize<string>(titleResult);
                    if (inner != null) LogTraffic($"  TITLE: {inner}");
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "JS probe failed (page may still be loading)");
            }
        }

        /// <summary>
        /// Execute arbitrary JS in the Teams page context. For POC exploration.
        /// </summary>
        public async Task<string?> EvaluateJsAsync(string script)
        {
            if (_webView == null) return null;

            try
            {
                var result = await _webView.ExecuteScriptAsync(script);
                if (result == "null") return null;
                return JsonSerializer.Deserialize<string>(result);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "JS evaluation failed");
                return null;
            }
        }
    }
}
