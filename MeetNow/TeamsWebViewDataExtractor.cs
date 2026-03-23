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
    }
}
