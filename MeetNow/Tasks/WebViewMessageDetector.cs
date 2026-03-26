using MeetNow.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace MeetNow.Tasks
{
    /// <summary>
    /// Polls the Teams web chat list sidebar DOM every 30 seconds via the MessageMonitor
    /// WebViewInstance to detect unread messages and fire NewMessageDetected events.
    /// </summary>
    public static class WebViewMessageDetector
    {
        public static event Action<TeamsMessage>? NewMessageDetected;

        private static DispatcherTimer? _timer;
        private static WebViewInstance? _instance;
        private static bool _polling;
        private static readonly HashSet<string> _processedIds = new();

        private const int MaxProcessedIds = 5000;
        private const int MaxUnreadItems = 20;

        // JS that queries the Teams chat list DOM for unread items
        private static readonly string ChatListJs = $@"
(function() {{
    try {{
        var items = document.querySelectorAll('[role=""listitem""]');
        var unread = [];

        for (var i = 0; i < items.length && unread.length < {MaxUnreadItems}; i++) {{
            var item = items[i];

            // Check for unread indicators
            var hasUnreadBadge = !!item.querySelector('[class*=""badge""], [class*=""unread""]');
            var ariaLabel = (item.getAttribute('aria-label') || '').toLowerCase();
            var hasUnreadAria = ariaLabel.indexOf('unread') >= 0 || ariaLabel.indexOf('new message') >= 0;

            // Check for bold text (common unread indicator in Teams)
            var hasBoldText = !!item.querySelector('b, strong');

            if (!hasUnreadBadge && !hasUnreadAria && !hasBoldText) continue;

            // Extract raw aria-label for deduplication/parsing
            var rawAria = item.getAttribute('aria-label') || '';

            // Extract sender: first segment of aria-label or name/title/sender elements
            var sender = '';
            var senderEl = item.querySelector('[class*=""name""], [class*=""title""], [class*=""sender""]');
            if (senderEl) {{
                sender = senderEl.textContent.trim();
            }}
            if (!sender && rawAria) {{
                // aria-label often starts with sender name
                var parts = rawAria.split(',');
                sender = parts[0].trim();
            }}
            if (!sender) {{
                var lines = (item.textContent || '').trim().split('\n');
                sender = lines[0].trim();
            }}

            // Extract content preview: second line of text or dedicated preview element
            var content = '';
            var previewEl = item.querySelector('[class*=""preview""], [class*=""content""], [class*=""subtitle""], [class*=""message""]');
            if (previewEl) {{
                content = previewEl.textContent.trim();
            }}
            if (!content) {{
                var allText = (item.textContent || '').trim().split('\n');
                // Skip blank lines and use the second non-empty line as content
                var nonEmpty = allText.filter(function(l) {{ return l.trim().length > 0; }});
                if (nonEmpty.length > 1) content = nonEmpty[1].trim();
            }}
            if (!content && rawAria) {{
                var ariaParts = rawAria.split(',');
                if (ariaParts.length > 1) content = ariaParts[1].trim();
            }}

            // Detect mention
            var isMention = content.indexOf('@') >= 0
                || rawAria.toLowerCase().indexOf('@mention') >= 0
                || rawAria.toLowerCase().indexOf('you were mentioned') >= 0
                || !!item.querySelector('[class*=""mention""]');

            // Detect thread type from aria-label keywords
            var threadType = 'chat';
            var ariaLower = rawAria.toLowerCase();
            if (ariaLower.indexOf('channel') >= 0 || ariaLower.indexOf('team') >= 0) {{
                threadType = 'space';
            }} else if (ariaLower.indexOf('meeting') >= 0 || ariaLower.indexOf('call') >= 0) {{
                threadType = 'meeting';
            }}

            if (sender || content) {{
                unread.push({{
                    sender: sender.substring(0, 200),
                    content: content.substring(0, 500),
                    threadType: threadType,
                    isMention: isMention,
                    ariaLabel: rawAria.substring(0, 300)
                }});
            }}
        }}

        return JSON.stringify({{ total: items.length, unread: unread }});
    }} catch (e) {{
        return JSON.stringify({{ error: e.message }});
    }}
}})();";

        public static void StartListening(WebViewInstance instance, int intervalSeconds = 30)
        {
            _instance = instance;

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(intervalSeconds)
            };
            _timer.Tick += async (s, e) => await PollChatListAsync();
            _timer.Start();

            Log.Information("WebViewMessageDetector started (interval={Interval}s)", intervalSeconds);
        }

        public static void StopListening()
        {
            _timer?.Stop();
            _timer = null;
            _instance = null;
            Log.Information("WebViewMessageDetector stopped");
        }

        public static async Task PollChatListAsync()
        {
            if (_polling) return;
            if (_instance == null) return;

            _polling = true;
            try
            {
                // EvaluateJsAsync must be called on the UI thread (WebView2 requirement)
                var json = await Application.Current.Dispatcher.InvokeAsync(
                    () => _instance.EvaluateJsAsync(ChatListJs)).Task.Unwrap();

                if (string.IsNullOrWhiteSpace(json))
                {
                    Log.Debug("WebViewMessageDetector: empty JS result");
                    return;
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var err))
                {
                    Log.Warning("WebViewMessageDetector: JS error: {Error}", err.GetString());
                    return;
                }

                if (!root.TryGetProperty("unread", out var unreadArray))
                    return;

                var newMessages = new List<TeamsMessage>();

                foreach (var item in unreadArray.EnumerateArray())
                {
                    var sender  = item.TryGetProperty("sender",    out var s) ? s.GetString() ?? "" : "";
                    var content = item.TryGetProperty("content",   out var c) ? c.GetString() ?? "" : "";
                    var type    = item.TryGetProperty("threadType", out var t) ? t.GetString() ?? "chat" : "chat";
                    var mention = item.TryGetProperty("isMention", out var m) && m.GetBoolean();
                    var aria    = item.TryGetProperty("ariaLabel", out var a) ? a.GetString() ?? "" : "";

                    // Stable dedup key: hash of sender + first 100 chars of content
                    var raw = $"{sender}|{content[..Math.Min(content.Length, 100)]}";
                    var hash = raw.GetHashCode().ToString("X8");
                    var id = $"wv_{hash}";

                    if (_processedIds.Contains(id)) continue;

                    // Trim set to avoid unbounded growth
                    if (_processedIds.Count >= MaxProcessedIds)
                        _processedIds.Clear();

                    _processedIds.Add(id);

                    newMessages.Add(new TeamsMessage
                    {
                        Id = id,
                        Sender = sender,
                        Content = content,
                        Timestamp = DateTime.Now,
                        ThreadType = type,
                        IsMention = mention,
                        MentionedNames = Array.Empty<string>(),
                        Urgency = MessageUrgency.Normal,
                        UrgencyReason = string.Empty
                    });
                }

                if (newMessages.Count > 0)
                {
                    Log.Information("WebViewMessageDetector: {Count} new unread item(s) detected", newMessages.Count);
                    foreach (var msg in newMessages)
                    {
                        Log.Debug("WebViewMessageDetector: [{Type}] from {Sender}: {Preview}",
                            msg.ThreadType, msg.Sender,
                            msg.Content.Length > 80 ? msg.Content[..80] + "..." : msg.Content);
                        NewMessageDetected?.Invoke(msg);
                    }
                }
                else
                {
                    Log.Debug("WebViewMessageDetector: poll complete, no new unread items");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "WebViewMessageDetector: poll failed");
            }
            finally
            {
                _polling = false;
            }
        }
    }
}
