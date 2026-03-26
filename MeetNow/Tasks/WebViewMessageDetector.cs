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
        // Teams v2 uses role=""treeitem"" for chat list items
        var items = document.querySelectorAll('[role=""treeitem""]');
        // Fallback to role=""listitem"" for older Teams versions
        if (items.length === 0) items = document.querySelectorAll('[role=""listitem""]');
        var unread = [];

        for (var i = 0; i < items.length && unread.length < {MaxUnreadItems}; i++) {{
            var item = items[i];

            // Check for unread indicators
            // Teams v2 uses CounterBadge for unread count
            var badgeEl = item.querySelector('[class*=""CounterBadge""], [class*=""badge"" i], [class*=""unread"" i]');
            var hasUnreadBadge = !!badgeEl;
            var ariaLabel = (item.getAttribute('aria-label') || '').toLowerCase();
            var hasUnreadAria = ariaLabel.indexOf('unread') >= 0 || ariaLabel.indexOf('new message') >= 0;

            // Check for bold text (Teams bolds unread chat names)
            var hasBoldText = false;
            var titleEl = item.querySelector('[data-tid=""chat-title""]');
            if (titleEl) {{
                var fontWeight = window.getComputedStyle(titleEl).fontWeight;
                hasBoldText = fontWeight === 'bold' || fontWeight === '700' || parseInt(fontWeight) >= 600;
            }}

            if (!hasUnreadBadge && !hasUnreadAria && !hasBoldText) continue;

            // Extract raw aria-label for deduplication/parsing
            var rawAria = item.getAttribute('aria-label') || '';

            // Extract sender from data-tid=""chat-title"" (Teams v2)
            var sender = '';
            var chatTitleEl = item.querySelector('[data-tid=""chat-title""]');
            if (chatTitleEl) {{
                sender = chatTitleEl.textContent.trim();
            }}
            if (!sender && rawAria) {{
                var parts = rawAria.split(',');
                sender = parts[0].trim();
            }}
            if (!sender) {{
                var lines = (item.textContent || '').trim().split('\n');
                sender = lines[0].trim();
            }}

            // Extract content from data-tid=""chat-description"" (Teams v2)
            var content = '';
            var descEl = item.querySelector('[data-tid=""chat-description""]');
            if (descEl) {{
                content = descEl.textContent.trim();
            }}
            if (!content) {{
                var previewEl = item.querySelector('[class*=""preview"" i], [class*=""subtitle"" i]');
                if (previewEl) content = previewEl.textContent.trim();
            }}
            if (!content && rawAria) {{
                var ariaParts = rawAria.split(',');
                if (ariaParts.length > 1) content = ariaParts.slice(1).join(',').trim();
            }}

            // Badge count (if any)
            var badgeCount = badgeEl ? (badgeEl.textContent || '').trim() : '';

            // Detect mention
            var isMention = content.indexOf('@') >= 0
                || rawAria.toLowerCase().indexOf('mention') >= 0
                || !!item.querySelector('[class*=""mention"" i]');

            // Detect thread type
            var threadType = 'chat';
            var ariaLower = rawAria.toLowerCase();
            if (ariaLower.indexOf('channel') >= 0) threadType = 'space';
            else if (ariaLower.indexOf('meeting') >= 0) threadType = 'meeting';

            if (sender || content) {{
                unread.push({{
                    sender: sender.substring(0, 200),
                    content: content.substring(0, 500),
                    threadType: threadType,
                    isMention: isMention,
                    badge: badgeCount,
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
                    Log.Information("WebViewMessageDetector: empty JS result (page may be loading)");
                    return;
                }

                Log.Information("WebViewMessageDetector: poll result ({Len} chars): {Preview}",
                    json.Length, json.Length > 500 ? json[..500] + "..." : json);

                // If no items found, dump DOM diagnostic to understand the structure
                if (json.Contains("\"total\":0"))
                {
                    var diagJson = await Application.Current.Dispatcher.InvokeAsync(
                        () => _instance.EvaluateJsAsync(@"
(function() {
    try {
        var diag = {};
        // Check various selectors
        diag.listItems = document.querySelectorAll('[role=""listitem""]').length;
        diag.treeItems = document.querySelectorAll('[role=""treeitem""]').length;
        diag.options = document.querySelectorAll('[role=""option""]').length;
        diag.rows = document.querySelectorAll('[role=""row""]').length;
        diag.buttons = document.querySelectorAll('[role=""button""]').length;
        diag.links = document.querySelectorAll('a[href]').length;

        // Find elements with chat/conversation related classes or data attributes
        var chatElements = document.querySelectorAll('[class*=""chat"" i], [class*=""conversation"" i], [data-tid*=""chat""], [data-tid*=""conversation""]');
        diag.chatElements = chatElements.length;
        var chatSamples = [];
        for (var i = 0; i < chatElements.length && i < 5; i++) {
            chatSamples.push({
                tag: chatElements[i].tagName,
                role: chatElements[i].getAttribute('role') || '',
                class: (chatElements[i].className || '').substring(0, 80),
                dataTid: chatElements[i].getAttribute('data-tid') || '',
                ariaLabel: (chatElements[i].getAttribute('aria-label') || '').substring(0, 100),
                childCount: chatElements[i].children.length
            });
        }
        diag.chatSamples = chatSamples;

        // Find elements with unread/badge indicators
        var badges = document.querySelectorAll('[class*=""badge"" i], [class*=""unread"" i], [class*=""bold"" i]');
        diag.badgeElements = badges.length;
        var badgeSamples = [];
        for (var j = 0; j < badges.length && j < 5; j++) {
            badgeSamples.push({
                tag: badges[j].tagName,
                class: (badges[j].className || '').substring(0, 80),
                text: (badges[j].textContent || '').trim().substring(0, 40),
                parentTag: badges[j].parentElement ? badges[j].parentElement.tagName : ''
            });
        }
        diag.badgeSamples = badgeSamples;

        return JSON.stringify(diag);
    } catch(e) { return JSON.stringify({error: e.message}); }
})();")).Task.Unwrap();
                    Log.Information("WebViewMessageDetector: DOM diagnostic: {Diag}", diagJson);
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
