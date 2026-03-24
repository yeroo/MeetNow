using Serilog;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace MeetNow.Tasks
{
    /// <summary>
    /// Persistent listener on a dedicated CalendarMonitor WebViewInstance.
    /// Reads calendar events from the Outlook web calendar DOM after page loads.
    /// Refreshes by re-navigating every 15 min.
    /// </summary>
    public static class CalendarCollectorTask
    {
        private static readonly object _lock = new();
        private static TeamsMeeting[] _lastCollected = Array.Empty<TeamsMeeting>();
        private static WebViewInstance? _instance;
        private static DispatcherTimer? _refreshTimer;

        public static TeamsMeeting[] LastCollectedMeetings
        {
            get { lock (_lock) return _lastCollected; }
        }

        /// <summary>
        /// Fired after meetings are parsed, so MainWindow can trigger a refresh.
        /// </summary>
        public static event Action? MeetingsUpdated;

        /// <summary>
        /// Attach to a dedicated WebViewInstance as a persistent listener.
        /// Called once from WebViewManager.StartCalendarMonitorAsync().
        /// </summary>
        // Join URLs captured from network traffic, keyed by nearby subject text
        private static readonly Dictionary<string, string> _capturedJoinUrls = new(StringComparer.OrdinalIgnoreCase);

        public static void StartListening(WebViewInstance instance)
        {
            _instance = instance;

            // Listen to ALL network responses to capture join URLs passively
            _instance.ResponseReceived += OnNetworkResponse;

            // Parse DOM after initial page load (20s warmup for page to render)
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _refreshTimer.Tick += OnFirstParse;
            _refreshTimer.Start();

            Log.Information("CalendarCollectorTask: listening on [{Name}]", instance.Name);
        }

        public static void StopListening()
        {
            _refreshTimer?.Stop();
            _refreshTimer = null;
            if (_instance != null)
                _instance.ResponseReceived -= OnNetworkResponse;
            _instance = null;
            Log.Information("CalendarCollectorTask: stopped listening");
        }

        /// <summary>
        /// Persistent network listener — scans ALL response bodies for Teams join URLs.
        /// </summary>
        private static void OnNetworkResponse(string uri, string? body, IDictionary<string, string> headers)
        {
            if (body == null) return;

            // Search for Teams join URL patterns
            var searchPatterns = new[] { "teams.microsoft.com/l/meetup-join/", "teams.microsoft.com/meet/" };
            foreach (var pattern in searchPatterns)
            {
                var idx = body.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                while (idx >= 0)
                {
                    var urlStart = body.LastIndexOf("https://", idx, Math.Min(idx, 200), StringComparison.OrdinalIgnoreCase);
                    if (urlStart >= 0)
                    {
                        var urlEnd = body.IndexOfAny(new[] { '"', '\\', ' ', '\n', '\r', '<', '>', '\'' }, urlStart);
                        if (urlEnd < 0) urlEnd = Math.Min(urlStart + 500, body.Length);
                        var url = body[urlStart..urlEnd];

                        if (url.Contains("%2F", StringComparison.OrdinalIgnoreCase))
                            url = Uri.UnescapeDataString(url);

                        // Extract nearby subject: search backwards for "Subject" or "subject" field
                        var subject = ExtractNearbySubject(body, urlStart);

                        lock (_capturedJoinUrls)
                        {
                            var key = subject ?? url; // use subject as key, fallback to URL itself
                            _capturedJoinUrls[key] = url;
                            Log.Information("CalendarCollectorTask: captured join URL [{Subject}] from [{Uri}]: {Url}",
                                subject ?? "(no subject)", uri.Length > 80 ? uri[..80] : uri, url);
                        }
                    }

                    idx = body.IndexOf(pattern, idx + pattern.Length, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        /// <summary>
        /// Search backwards from a position in the response body to find a nearby Subject field value.
        /// </summary>
        private static string? ExtractNearbySubject(string body, int position)
        {
            // Look backwards up to 2000 chars for "Subject" or "subject" field
            var searchStart = Math.Max(0, position - 2000);
            var searchRegion = body[searchStart..position];

            // Try patterns: "Subject":"value", "subject":"value", "Subject\": \"value\"
            var patterns = new[] { "\"Subject\":", "\"subject\":", "\"Subject\\\":", "\"subject\\\":" };
            foreach (var pat in patterns)
            {
                var lastIdx = searchRegion.LastIndexOf(pat, StringComparison.OrdinalIgnoreCase);
                if (lastIdx >= 0)
                {
                    // Find the value after the colon
                    var valueStart = searchRegion.IndexOf('"', lastIdx + pat.Length);
                    if (valueStart >= 0)
                    {
                        valueStart++; // skip opening quote
                        var valueEnd = searchRegion.IndexOf('"', valueStart);
                        if (valueEnd > valueStart && valueEnd - valueStart < 200)
                        {
                            var subject = searchRegion[valueStart..valueEnd];
                            // Unescape if needed
                            subject = subject.Replace("\\\"", "\"").Replace("\\\\", "\\");
                            if (!string.IsNullOrWhiteSpace(subject))
                                return subject;
                        }
                    }
                }
            }
            return null;
        }

        private static async void OnFirstParse(object? sender, EventArgs e)
        {
            await ParseCalendarDomAsync();

            // Switch to 15-min refresh cycle
            _refreshTimer!.Stop();
            _refreshTimer.Interval = TimeSpan.FromMinutes(15);
            _refreshTimer.Tick -= OnFirstParse;
            _refreshTimer.Tick += OnRefreshTick;
            _refreshTimer.Start();

            Log.Information("CalendarCollectorTask: initial parse done, switching to 15-min refresh");
        }

        private static async void OnRefreshTick(object? sender, EventArgs e)
        {
            if (_instance == null) return;

            try
            {
                Log.Information("CalendarCollectorTask: refreshing calendar");

                // Re-navigate to get fresh data
                await _instance.NavigateAndWaitAsync("https://outlook.cloud.microsoft/calendar/view/day");

                // Check for auth redirect
                if (_instance.CurrentUrl != null &&
                    _instance.CurrentUrl.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warning("CalendarCollectorTask: auth redirect on refresh, skipping");
                    return;
                }

                // Wait for page to render, then parse DOM
                await Task.Delay(10000);
                await ParseCalendarDomAsync();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "CalendarCollectorTask: refresh failed");
            }
        }

        private static async Task ParseCalendarDomAsync()
        {
            if (_instance == null) return;

            try
            {
                // Extract calendar events from the rendered Outlook calendar DOM
                var js = @"
(function() {
    try {
        var events = [];

        // OWA day view renders calendar items as div elements with aria-label
        // containing subject, time, and other details
        var calItems = document.querySelectorAll('[data-app-section=""CalendarItemPart""]');

        // Fallback: try aria-label on calendar surface items
        if (calItems.length === 0) {
            calItems = document.querySelectorAll('[class*=""calendarItem""], [class*=""CalendarItem""], [class*=""calendar-item""]');
        }

        // Broader fallback: items with role=""button"" inside the calendar surface
        if (calItems.length === 0) {
            var surface = document.querySelector('[class*=""calendarSurface""], [class*=""CalendarSurface""], [role=""main""]');
            if (surface) {
                calItems = surface.querySelectorAll('[role=""button""][aria-label]');
            }
        }

        // Even broader: any element with aria-label that looks like a calendar event
        if (calItems.length === 0) {
            var all = document.querySelectorAll('[aria-label]');
            var filtered = [];
            for (var i = 0; i < all.length; i++) {
                var label = all[i].getAttribute('aria-label') || '';
                // Calendar events typically have time patterns like ""10:00"" or ""2:00 PM""
                if (/\d{1,2}:\d{2}/.test(label) && label.length > 15 && label.length < 500) {
                    filtered.push(all[i]);
                }
            }
            calItems = filtered;
        }

        for (var i = 0; i < calItems.length; i++) {
            var el = calItems[i];
            var ariaLabel = el.getAttribute('aria-label') || '';
            var innerText = el.innerText || '';

            // Try to extract subject and times from aria-label
            // Typical format: ""Subject, Start time End time, Location, Organizer""
            // or ""Subject, Monday, March 24, 2026, 4:00 PM 4:30 PM""
            var event = {
                ariaLabel: ariaLabel.substring(0, 500),
                text: innerText.substring(0, 500),
                tag: el.tagName,
                classes: (el.className || '').substring(0, 200)
            };

            // Try to find subject text element inside
            var subjectEl = el.querySelector('[class*=""subject""], [class*=""Subject""], [class*=""title""], [class*=""Title""]');
            if (subjectEl) event.subject = subjectEl.textContent.trim();

            // Try to find time text
            var timeEl = el.querySelector('[class*=""time""], [class*=""Time""], [class*=""duration""]');
            if (timeEl) event.time = timeEl.textContent.trim();

            // Try to find location
            var locEl = el.querySelector('[class*=""location""], [class*=""Location""]');
            if (locEl) event.location = locEl.textContent.trim();

            events.push(event);
        }

        return JSON.stringify({
            count: events.length,
            url: window.location.href,
            title: document.title,
            events: events
        });
    } catch(e) {
        return JSON.stringify({ error: e.message, url: window.location.href });
    }
})();";

                var resultJson = await _instance.EvaluateJsAsync(js);
                if (resultJson == null)
                {
                    Log.Warning("CalendarCollectorTask: DOM parse returned null");
                    return;
                }

                Log.Information("CalendarCollectorTask: DOM result: {Result}",
                    resultJson.Length > 2000 ? resultJson[..2000] + "..." : resultJson);

                using var doc = JsonDocument.Parse(resultJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var err))
                {
                    Log.Warning("CalendarCollectorTask: DOM parse error: {Error}", err.GetString());
                    return;
                }

                if (!root.TryGetProperty("events", out var eventsArr))
                    return;

                var meetings = new List<TeamsMeeting>();
                var today = DateTime.Today;

                foreach (var evt in eventsArr.EnumerateArray())
                {
                    var meeting = ParseDomEvent(evt, today);
                    if (meeting != null)
                        meetings.Add(meeting);
                }

                Log.Information("CalendarCollectorTask: parsed {Count} meetings from DOM", meetings.Count);

                // Try to assign captured join URLs to meetings by matching subject
                lock (_capturedJoinUrls)
                {
                    if (_capturedJoinUrls.Count > 0)
                    {
                        Log.Information("CalendarCollectorTask: {Count} join URLs captured from network", _capturedJoinUrls.Count);
                        foreach (var m in meetings)
                        {
                            if (m.TeamsUrl != "teams-meeting" || string.IsNullOrEmpty(m.Subject)) continue;

                            // Try exact subject match
                            if (_capturedJoinUrls.TryGetValue(m.Subject, out var exactUrl))
                            {
                                m.TeamsUrl = exactUrl;
                                Log.Information("CalendarCollectorTask: matched URL to [{Subject}]: {Url}", m.Subject, exactUrl);
                                continue;
                            }

                            // Try partial match (subject contains or is contained)
                            foreach (var (key, url) in _capturedJoinUrls)
                            {
                                if (key.Contains(m.Subject, StringComparison.OrdinalIgnoreCase)
                                    || m.Subject.Contains(key, StringComparison.OrdinalIgnoreCase))
                                {
                                    m.TeamsUrl = url;
                                    Log.Information("CalendarCollectorTask: partial matched URL [{Key}] to [{Subject}]: {Url}",
                                        key, m.Subject, url);
                                    break;
                                }
                            }
                        }
                    }
                }

                foreach (var m in meetings)
                    Log.Information("  Meeting: {Subject} {Start}-{End} TeamsUrl={Url}",
                        m.Subject, m.Start.ToString("HH:mm"), m.End.ToString("HH:mm"), m.TeamsUrl);

                if (meetings.Count > 0)
                {
                    lock (_lock)
                    {
                        _lastCollected = meetings.ToArray();
                    }
                    MeetingsUpdated?.Invoke();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "CalendarCollectorTask: DOM parse failed");
            }
        }

        /// <summary>
        /// Lazy enrichment: click a specific meeting in Outlook DOM to extract its Teams join URL.
        /// Called when user clicks a meeting in the tray menu that has a placeholder URL.
        /// </summary>
        public static async Task<string?> GetJoinUrlAsync(string subject)
        {
            if (_instance == null) return null;

            Log.Information("CalendarCollectorTask: fetching join URL for [{Subject}]", subject);

            // Check cached URLs from network interception first
            lock (_capturedJoinUrls)
            {
                // Exact match
                if (_capturedJoinUrls.TryGetValue(subject, out var exactUrl))
                {
                    Log.Information("CalendarCollectorTask: using cached URL for [{Subject}]: {Url}", subject, exactUrl);
                    return exactUrl;
                }
                // Partial match
                foreach (var (key, url) in _capturedJoinUrls)
                {
                    if (key.Contains(subject, StringComparison.OrdinalIgnoreCase)
                        || subject.Contains(key, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Information("CalendarCollectorTask: using partial-matched URL [{Key}] for [{Subject}]: {Url}", key, subject, url);
                        return url;
                    }
                }
            }

            // Watch network for Teams URL while we click
            string? capturedTeamsUrl = null;
            void onResponse(string uri, string? body, IDictionary<string, string> headers)
            {
                if (body == null) return;
                // Look for Teams join URL in response body
                var idx = body.IndexOf("https://teams.microsoft.com/l/meetup-join/", StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                    idx = body.IndexOf("https://teams.microsoft.com/meet/", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var urlEnd = body.IndexOfAny(new[] { '"', '\\', ' ', '\n', '\r' }, idx);
                    if (urlEnd < 0) urlEnd = Math.Min(idx + 500, body.Length);
                    capturedTeamsUrl = body[idx..urlEnd];
                    Log.Information("CalendarCollectorTask: captured Teams URL from network: {Url}", capturedTeamsUrl);
                }
            }

            _instance.ResponseReceived += onResponse;

            try
            {
                var safeSubject = subject.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\"", "\\\"");

                // Step 1: Click the meeting to open its detail/reading pane
                var clickJs = $@"
(function() {{
    try {{
        var items = document.querySelectorAll('[role=""button""][aria-label]');
        for (var i = 0; i < items.length; i++) {{
            var label = items[i].getAttribute('aria-label') || '';
            if (label.indexOf('{safeSubject}') >= 0) {{
                items[i].click();
                return 'clicked';
            }}
        }}
        return 'not_found';
    }} catch(e) {{ return 'error:' + e.message; }}
}})();";

                var clickResult = await _instance.EvaluateJsAsync(clickJs);
                Log.Information("CalendarCollectorTask: click result for [{Subject}]: {Result}", subject, clickResult);

                if (clickResult != "clicked")
                    return capturedTeamsUrl;

                // Step 2: Wait for meeting detail to load
                await Task.Delay(4000);

                // Step 3: Search the page for Teams join URL — it's in the meeting body HTML
                var searchJs = @"
(function() {
    try {
        // Search all links for Teams meeting URL
        var links = document.querySelectorAll('a[href]');
        for (var i = 0; i < links.length; i++) {
            var href = links[i].href || '';
            if (href.indexOf('teams.microsoft.com/l/meetup-join/') >= 0 ||
                href.indexOf('teams.microsoft.com/meet/') >= 0) {
                return JSON.stringify({ joinUrl: href, source: 'link_href' });
            }
        }

        // Search page HTML for encoded Teams URLs
        var html = document.body.innerHTML;
        var match = html.match(/https:\/\/teams\.microsoft\.com\/l\/meetup-join\/[^""'<\s\\]+/i);
        if (match) return JSON.stringify({ joinUrl: match[0], source: 'html_regex' });

        var meetMatch = html.match(/https:\/\/teams\.microsoft\.com\/meet\/[^""'<\s\\]+/i);
        if (meetMatch) return JSON.stringify({ joinUrl: meetMatch[0], source: 'meet_regex' });

        return JSON.stringify({ noUrl: true });
    } catch(e) { return JSON.stringify({ error: e.message }); }
})();";

                var resultJson = await _instance.EvaluateJsAsync(searchJs);
                Log.Information("CalendarCollectorTask: search result for [{Subject}]: {Result}",
                    subject, resultJson ?? "null");

                string? joinUrl = null;

                if (resultJson != null)
                {
                    using var searchDoc = JsonDocument.Parse(resultJson);
                    if (searchDoc.RootElement.TryGetProperty("joinUrl", out var ju))
                        joinUrl = ju.GetString();
                }

                // Fallback: network-captured URL
                joinUrl ??= capturedTeamsUrl;

                // Close the detail pane
                await _instance.EvaluateJsAsync(
                    "(function() { document.dispatchEvent(new KeyboardEvent('keydown', {key: 'Escape', keyCode: 27, bubbles: true})); return 'ok'; })();");

                if (joinUrl != null)
                    Log.Information("CalendarCollectorTask: resolved join URL for [{Subject}]: {Url}", subject, joinUrl);
                else
                    Log.Warning("CalendarCollectorTask: could not find join URL for [{Subject}]", subject);

                return joinUrl;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "CalendarCollectorTask: join URL fetch failed for [{Subject}]", subject);
                return null;
            }
            finally
            {
                _instance.ResponseReceived -= onResponse;
            }
        }

        private static TeamsMeeting? ParseDomEvent(JsonElement evt, DateTime today)
        {
            try
            {
                // Get subject from explicit element or aria-label
                var subject = evt.TryGetProperty("subject", out var subj) ? subj.GetString() : null;
                var ariaLabel = evt.TryGetProperty("ariaLabel", out var al) ? al.GetString() ?? "" : "";
                var text = evt.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                var timeText = evt.TryGetProperty("time", out var tt) ? tt.GetString() : null;

                // Skip work location blocks and non-event items
                if (ariaLabel.Contains("Work location", StringComparison.OrdinalIgnoreCase))
                    return null;

                // If no explicit subject, try first line of text
                if (string.IsNullOrWhiteSpace(subject))
                {
                    var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 0)
                        subject = lines[0].Trim();
                }

                // Last resort: first part of aria-label, but only if it's not just a time
                if (string.IsNullOrWhiteSpace(subject))
                {
                    var parts = ariaLabel.Split(',');
                    if (parts.Length > 0)
                    {
                        var candidate = parts[0].Trim();
                        // Skip if it's just a time range like "14:30 to 15:00"
                        if (!System.Text.RegularExpressions.Regex.IsMatch(candidate, @"^\d{1,2}:\d{2}\s"))
                            subject = candidate;
                    }
                }

                if (string.IsNullOrWhiteSpace(subject)) return null;

                // Parse times from aria-label or time text
                DateTime start = default, end = default;
                var timeSource = timeText ?? ariaLabel;
                ParseTimes(timeSource, today, out start, out end);

                // Location
                var location = evt.TryGetProperty("location", out var loc) ? loc.GetString() : null;

                // Check if Teams meeting (look for Teams URL in aria-label or text)
                string? teamsUrl = null;
                var fullText = ariaLabel + " " + text;
                var teamsIdx = fullText.IndexOf("teams.microsoft.com", StringComparison.OrdinalIgnoreCase);
                if (teamsIdx >= 0)
                {
                    // Try to extract URL
                    var urlStart = fullText.LastIndexOf("https://", teamsIdx, StringComparison.OrdinalIgnoreCase);
                    if (urlStart >= 0)
                    {
                        var urlEnd = fullText.IndexOfAny(new[] { ' ', '\n', '\r', '"', '\'' }, urlStart);
                        if (urlEnd < 0) urlEnd = fullText.Length;
                        teamsUrl = fullText[urlStart..urlEnd];
                    }
                }

                // If no URL found but text mentions Teams Meeting, mark as Teams meeting
                // so it still shows in the tray menu
                if (teamsUrl == null && fullText.Contains("Teams Meeting", StringComparison.OrdinalIgnoreCase))
                    teamsUrl = "teams-meeting"; // placeholder — indicates it's a Teams meeting

                // Extract organizer from aria-label "By Name"
                string? organizer = null;
                var byIdx = ariaLabel.IndexOf(", By ", StringComparison.OrdinalIgnoreCase);
                if (byIdx >= 0)
                {
                    var orgStart = byIdx + 5;
                    var orgEnd = ariaLabel.IndexOf(',', orgStart);
                    organizer = orgEnd >= 0 ? ariaLabel[orgStart..orgEnd].Trim() : ariaLabel[orgStart..].Trim();
                }

                return new TeamsMeeting
                {
                    Subject = subject,
                    Start = start,
                    End = end,
                    TeamsUrl = teamsUrl ?? "",
                    Location = location,
                    Organizer = organizer,
                    Recurrent = ariaLabel.Contains("Recurring", StringComparison.OrdinalIgnoreCase)
                };
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "CalendarCollectorTask: failed to parse DOM event");
                return null;
            }
        }

        /// <summary>
        /// Parse time strings like "4:00 PM 4:30 PM", "16:00 16:30",
        /// or "4:00 PM - 4:30 PM" from the given text.
        /// </summary>
        private static void ParseTimes(string text, DateTime today, out DateTime start, out DateTime end)
        {
            start = default;
            end = default;

            // Find all time patterns (HH:mm or h:mm AM/PM)
            var timePattern = new System.Text.RegularExpressions.Regex(
                @"(\d{1,2}:\d{2}\s*(?:AM|PM)?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var matches = timePattern.Matches(text);
            if (matches.Count >= 1)
            {
                if (DateTime.TryParse(matches[0].Value.Trim(), out var t1))
                    start = today.Add(t1.TimeOfDay);
            }
            if (matches.Count >= 2)
            {
                if (DateTime.TryParse(matches[1].Value.Trim(), out var t2))
                    end = today.Add(t2.TimeOfDay);
            }
        }
    }
}
