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
        // Find all chat-title elements and work upward to their chat item container
        var chatTitles = document.querySelectorAll('[data-tid=""chat-title""]');
        var items = [];
        for (var k = 0; k < chatTitles.length; k++) {{
            // Walk up to find the closest treeitem or clickable container
            var container = chatTitles[k].closest('[role=""treeitem""]')
                         || chatTitles[k].closest('[role=""listitem""]')
                         || chatTitles[k].parentElement?.parentElement; // fallback: grandparent
            if (container) items.push({{ container: container, titleEl: chatTitles[k] }});
        }}

        var unread = [];

        for (var i = 0; i < items.length && unread.length < {MaxUnreadItems}; i++) {{
            var container = items[i].container;
            var chatTitleEl = items[i].titleEl;

            // Extract sender
            var sender = chatTitleEl.textContent.trim();
            if (!sender) continue;

            // Extract content from data-tid=""chat-description""
            var descEl = container.querySelector('[data-tid=""chat-description""]');
            var content = descEl ? descEl.textContent.trim() : '';

            // Check for unread indicators
            // 1. CounterBadge with a number
            var badgeEl = container.querySelector('[class*=""CounterBadge""]');
            var badgeCount = badgeEl ? (badgeEl.textContent || '').trim() : '';
            var hasUnreadBadge = badgeCount.length > 0 && badgeCount !== '0';

            // 2. Bold title = unread
            var hasBoldTitle = false;
            var fw = window.getComputedStyle(chatTitleEl).fontWeight;
            hasBoldTitle = fw === 'bold' || fw === '700' || parseInt(fw) >= 600;

            // 3. Aria-label unread indicator
            var rawAria = container.getAttribute('aria-label') || '';
            var hasUnreadAria = rawAria.toLowerCase().indexOf('unread') >= 0;

            if (!hasUnreadBadge && !hasBoldTitle && !hasUnreadAria) continue;

            // Detect mention
            var isMention = content.indexOf('@') >= 0
                || rawAria.toLowerCase().indexOf('mention') >= 0
                || !!container.querySelector('[class*=""mention"" i]');

            // Detect thread type
            var threadType = 'chat';
            var ariaLower = rawAria.toLowerCase();
            if (ariaLower.indexOf('channel') >= 0) threadType = 'space';
            else if (ariaLower.indexOf('meeting') >= 0) threadType = 'meeting';

            unread.push({{
                sender: sender.substring(0, 200),
                content: content.substring(0, 500),
                threadType: threadType,
                isMention: isMention,
                badge: badgeCount,
                bold: hasBoldTitle,
                ariaLabel: rawAria.substring(0, 300)
            }});
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
