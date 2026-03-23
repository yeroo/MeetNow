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

        public void StartJsProbing(int intervalSeconds = 30)
        {
            if (_jsProbingStarted) return; // Guard against multiple navigation events
            _jsProbingStarted = true;

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

        private async Task ProbeTeamsStateAsync()
        {
            if (_webView == null) return;

            try
            {
                // Probe 1: Check what global state objects exist
                var globalsJs = @"
(function() {
    try {
        var found = {};
        // Check common React/Redux state patterns
        if (window.__REDUX_STORE__) found.redux = true;
        if (window.__NEXT_DATA__) found.nextData = true;
        if (window.store) found.store = true;
        if (window.__appState) found.appState = true;

        // Check for Teams-specific globals
        var teamsKeys = Object.keys(window).filter(function(k) {
            return k.toLowerCase().indexOf('teams') >= 0 ||
                   k.toLowerCase().indexOf('skype') >= 0 ||
                   k.toLowerCase().indexOf('presence') >= 0 ||
                   k.toLowerCase().indexOf('calendar') >= 0;
        });
        if (teamsKeys.length > 0) found.teamsGlobals = teamsKeys;

        // Check document title for state indicators
        found.title = document.title;
        found.url = window.location.href;

        return JSON.stringify(found);
    } catch(e) {
        return JSON.stringify({ error: e.message });
    }
})();";

                var result = await _webView.ExecuteScriptAsync(globalsJs);
                if (result != null && result != "null")
                {
                    // ExecuteScriptAsync returns a JSON-encoded string, so the result
                    // is double-quoted. Parse it to get the inner JSON.
                    var inner = JsonSerializer.Deserialize<string>(result);
                    if (inner != null)
                    {
                        LogTraffic($"  JS_PROBE globals: {inner}");
                        Log.Debug("JS probe result: {Result}", inner);
                    }
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
