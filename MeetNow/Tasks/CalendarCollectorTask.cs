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
        /// Attach to a dedicated WebViewInstance as a persistent listener.
        /// Called once from WebViewManager.StartCalendarMonitorAsync().
        /// </summary>
        public static void StartListening(WebViewInstance instance)
        {
            _instance = instance;

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
            _instance = null;
            Log.Information("CalendarCollectorTask: stopped listening");
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

                if (meetings.Count > 0)
                {
                    lock (_lock)
                    {
                        _lastCollected = meetings.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "CalendarCollectorTask: DOM parse failed");
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

                // If no explicit subject, try first line of text or first part of aria-label
                if (string.IsNullOrWhiteSpace(subject))
                {
                    // Try first line of innerText
                    var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 0)
                        subject = lines[0].Trim();
                }

                if (string.IsNullOrWhiteSpace(subject))
                {
                    // Try first part of aria-label (before first comma with time)
                    var parts = ariaLabel.Split(',');
                    if (parts.Length > 0)
                        subject = parts[0].Trim();
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

                return new TeamsMeeting
                {
                    Subject = subject,
                    Start = start,
                    End = end,
                    TeamsUrl = teamsUrl ?? "",
                    Location = location
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
