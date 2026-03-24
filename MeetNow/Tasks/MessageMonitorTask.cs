using Serilog;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace MeetNow.Tasks
{
    /// <summary>
    /// Persistent task — stays on Teams, injects WS hooks and webpack explorer,
    /// probes state on a timer. Runs on the MessageMonitor (persistent) WebViewInstance.
    /// </summary>
    public static class MessageMonitorTask
    {
        private static readonly string TrafficLogPath = Path.Combine(
            Path.GetTempPath(), "MeetNow_WebView_Traffic.log");

        private static DispatcherTimer? _probeTimer;
        private static WebViewInstance? _instance;
        private static bool _started;

        public static async Task StartAsync(WebViewInstance instance, int probeIntervalSeconds = 30)
        {
            if (_started) return;
            _started = true;
            _instance = instance;

            // Inject WebSocket hook and webpack explorer once
            await InjectWebSocketHook();
            await InjectWebpackExplorer();

            // Start probe timer
            _probeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(probeIntervalSeconds)
            };
            _probeTimer.Tick += async (s, e) => await ProbeTeamsStateAsync();
            _probeTimer.Start();

            Log.Information("MessageMonitorTask started (probe every {Interval}s)", probeIntervalSeconds);
        }

        public static void Stop()
        {
            _probeTimer?.Stop();
            _probeTimer = null;
            _started = false;
            _instance = null;
            Log.Information("MessageMonitorTask stopped");
        }

        // Copied from TeamsWebViewDataExtractor.cs
        private static async Task InjectWebSocketHook()
        {
            if (_instance == null) return;

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
            if (typeof data === 'string' && data.indexOf('Bearer ') >= 0 && !window.__meetNowBearerToken) {
                var tokenMatch = data.match(/Bearer\s+(eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+)/);
                if (tokenMatch) window.__meetNowBearerToken = tokenMatch[1];
            }
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

        var origSend = ws.send.bind(ws);
        ws.send = function(data) {
            window.__meetNowWsStats.sent++;
            pushMsg('out', data);
            return origSend(data);
        };

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
                var result = await _instance.EvaluateJsAsync(hookJs);
                LogTraffic($"  WS_HOOK: {result}");
                Log.Information("WebSocket hook injection result: {Result}", result);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to inject WebSocket hook");
            }
        }

        // Copied from TeamsWebViewDataExtractor.cs
        private static async Task InjectWebpackExplorer()
        {
            if (_instance == null) return;

            var explorerJs = @"
(function() {
    if (window.__meetNowWebpackExplored) return 'already_explored';
    window.__meetNowWebpackExplored = true;
    window.__meetNowWebpackInfo = { modules: 0, interesting: [] };

    try {
        var chunkName = 'webpackChunk_msteams_react_web_client';
        var chunks = window[chunkName];
        if (!chunks) return JSON.stringify({ error: 'no chunks found' });

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

        var interesting = [];
        for (var i = 0; i < moduleIds.length && interesting.length < 50; i++) {
            try {
                var mod = moduleCache[moduleIds[i]];
                if (!mod || !mod.exports) continue;
                var exp = mod.exports;
                var keys = Object.keys(exp);

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
            } catch(e) { }
        }
        window.__meetNowWebpackInfo.interesting = interesting;
        return JSON.stringify({ modules: moduleIds.length, interesting: interesting.length });
    } catch(e) {
        return JSON.stringify({ error: e.message });
    }
})();";

            try
            {
                var result = await _instance.EvaluateJsAsync(explorerJs);
                LogTraffic($"  WEBPACK_EXPLORE: {result}");
                Log.Information("Webpack explorer result: {Result}", result);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to inject webpack explorer");
            }
        }

        // Copied from TeamsWebViewDataExtractor.cs — WS harvest + title probe
        private static async Task ProbeTeamsStateAsync()
        {
            if (_instance == null) return;

            try
            {
                // Probe 1: Harvest WebSocket messages
                var wsHarvestJs = @"
(function() {
    try {
        var result = { stats: window.__meetNowWsStats || {} };
        var msgs = window.__meetNowWsMessages || [];
        result.totalBuffered = msgs.length;

        result.messages = [];
        for (var i = 0; i < msgs.length; i++) {
            var m = msgs[i];
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

                var wsResult = await _instance.EvaluateJsAsync(wsHarvestJs);
                if (wsResult != null && !wsResult.Contains("\"messages\":[]"))
                {
                    LogTraffic($"  WS_ALL: {Truncate(wsResult, 4000)}");
                }

                // Probe 2: Page title
                var titleResult = await _instance.EvaluateJsAsync(
                    "(function() { return JSON.stringify({ title: document.title }); })();");
                if (titleResult != null)
                    LogTraffic($"  TITLE: {titleResult}");
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "MessageMonitorTask: probe failed (page may still be loading)");
            }
        }

        private static void LogTraffic(string line)
        {
            try
            {
                File.AppendAllText(TrafficLogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
            }
            catch { }
        }

        private static string Truncate(string s, int maxLength)
        {
            return s.Length <= maxLength ? s : s[..maxLength] + "...";
        }
    }
}
