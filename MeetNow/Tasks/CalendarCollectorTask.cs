using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace MeetNow.Tasks
{
    /// <summary>
    /// Transient task — navigates to Outlook calendar, captures events via passive
    /// interception of OWA API calls (service.svc, startupdata.ashx).
    /// Results stored in LastCollectedMeetings for MeetingDataAggregator to consume.
    /// </summary>
    public static class CalendarCollectorTask
    {
        private static readonly object _lock = new();
        private static TeamsMeeting[] _lastCollected = Array.Empty<TeamsMeeting>();

        public static TeamsMeeting[] LastCollectedMeetings
        {
            get { lock (_lock) return _lastCollected; }
        }

        public static async Task RunAsync(WebViewInstance instance)
        {
            Log.Information("CalendarCollectorTask: starting calendar collection");

            var capturedMeetings = new List<TeamsMeeting>();

            // Subscribe to response events to capture calendar data
            void onResponse(string uri, string? body, IDictionary<string, string> headers)
            {
                if (body == null) return;
                TryParseCalendarEvents(uri, body, capturedMeetings);
            }

            instance.ResponseReceived += onResponse;

            try
            {
                // Navigate to Outlook calendar day view
                await instance.NavigateAndWaitAsync("https://outlook.cloud.microsoft/calendar/view/day");

                // Check for auth redirect
                if (instance.CurrentUrl != null &&
                    instance.CurrentUrl.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warning("CalendarCollectorTask: auth redirect detected, aborting");
                    return;
                }

                // Wait for OWA API calls to complete
                await Task.Delay(15000);

                Log.Information("CalendarCollectorTask: captured {Count} meetings from OWA",
                    capturedMeetings.Count);

                if (capturedMeetings.Count > 0)
                {
                    lock (_lock)
                    {
                        _lastCollected = capturedMeetings.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "CalendarCollectorTask: collection failed");
            }
            finally
            {
                instance.ResponseReceived -= onResponse;
            }
        }

        private static void TryParseCalendarEvents(string uri, string body, List<TeamsMeeting> meetings)
        {
            try
            {
                // OWA service.svc calendar responses
                if (uri.Contains("service.svc", StringComparison.OrdinalIgnoreCase)
                    && body.Contains("\"Subject\"", StringComparison.OrdinalIgnoreCase)
                    && body.Contains("\"Start\"", StringComparison.OrdinalIgnoreCase))
                {
                    ParseOwaServiceResponse(body, meetings);
                    return;
                }

                // OWA startupdata.ashx
                if (uri.Contains("startupdata.ashx", StringComparison.OrdinalIgnoreCase))
                {
                    ParseOwaStartupData(body, meetings);
                    return;
                }

                // Graph-style calendarview responses
                if ((uri.Contains("/calendarview", StringComparison.OrdinalIgnoreCase)
                     || uri.Contains("/api/calendar/", StringComparison.OrdinalIgnoreCase))
                    && body.Contains("\"subject\"", StringComparison.OrdinalIgnoreCase))
                {
                    ParseGraphCalendarResponse(body, meetings);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "CalendarCollectorTask: failed to parse calendar response from {Uri}", uri);
            }
        }

        private static void ParseOwaServiceResponse(string body, List<TeamsMeeting> meetings)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                // Navigate Body.ResponseMessages.Items[].CalendarView.Items[]
                if (!root.TryGetProperty("Body", out var responseBody)) return;
                if (!responseBody.TryGetProperty("ResponseMessages", out var responseMessages)) return;
                if (!responseMessages.TryGetProperty("Items", out var items)) return;

                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("CalendarView", out var calendarView)) continue;
                    if (!calendarView.TryGetProperty("Items", out var events)) continue;

                    foreach (var evt in events.EnumerateArray())
                    {
                        var meeting = ParseOwaEvent(evt);
                        if (meeting != null)
                            meetings.Add(meeting);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "CalendarCollectorTask: failed to parse OWA service response");
            }
        }

        private static void ParseOwaStartupData(string body, List<TeamsMeeting> meetings)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                // startupdata may have calendar items embedded
                if (root.TryGetProperty("calendarEvents", out var events))
                {
                    foreach (var evt in events.EnumerateArray())
                    {
                        var meeting = ParseOwaEvent(evt);
                        if (meeting != null)
                            meetings.Add(meeting);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "CalendarCollectorTask: failed to parse OWA startup data");
            }
        }

        private static void ParseGraphCalendarResponse(string body, List<TeamsMeeting> meetings)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                JsonElement eventsArray;
                if (root.TryGetProperty("value", out var val))
                    eventsArray = val;
                else if (root.ValueKind == JsonValueKind.Array)
                    eventsArray = root;
                else
                    return;

                foreach (var evt in eventsArray.EnumerateArray())
                {
                    var subject = evt.TryGetProperty("subject", out var s) ? s.GetString() : null;
                    if (string.IsNullOrWhiteSpace(subject)) continue;

                    DateTime start = default, end = default;
                    if (evt.TryGetProperty("start", out var startObj))
                    {
                        var startStr = startObj.TryGetProperty("dateTime", out var dt) ? dt.GetString() : null;
                        if (startStr != null) DateTime.TryParse(startStr, out start);
                    }
                    if (evt.TryGetProperty("end", out var endObj))
                    {
                        var endStr = endObj.TryGetProperty("dateTime", out var dt) ? dt.GetString() : null;
                        if (endStr != null) DateTime.TryParse(endStr, out end);
                    }

                    string? joinUrl = null;
                    if (evt.TryGetProperty("onlineMeetingUrl", out var omu) && omu.ValueKind == JsonValueKind.String)
                        joinUrl = omu.GetString();
                    if (joinUrl == null && evt.TryGetProperty("onlineMeeting", out var om)
                        && om.TryGetProperty("joinUrl", out var ju))
                        joinUrl = ju.GetString();

                    meetings.Add(new TeamsMeeting
                    {
                        Subject = subject,
                        Start = start,
                        End = end,
                        TeamsUrl = joinUrl ?? ""
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "CalendarCollectorTask: failed to parse Graph calendar response");
            }
        }

        private static TeamsMeeting? ParseOwaEvent(JsonElement evt)
        {
            try
            {
                var subject = evt.TryGetProperty("Subject", out var s) ? s.GetString() : null;
                if (string.IsNullOrWhiteSpace(subject)) return null;

                DateTime start = default, end = default;

                // OWA uses Start/End as ISO strings or as objects
                if (evt.TryGetProperty("Start", out var startProp))
                {
                    if (startProp.ValueKind == JsonValueKind.String)
                        DateTime.TryParse(startProp.GetString(), out start);
                    else if (startProp.TryGetProperty("DateTime", out var dt))
                        DateTime.TryParse(dt.GetString(), out start);
                }
                if (evt.TryGetProperty("End", out var endProp))
                {
                    if (endProp.ValueKind == JsonValueKind.String)
                        DateTime.TryParse(endProp.GetString(), out end);
                    else if (endProp.TryGetProperty("DateTime", out var dt))
                        DateTime.TryParse(dt.GetString(), out end);
                }

                // Extract Teams join URL from various fields
                string? joinUrl = null;

                // OnlineMeetingJoinUrl
                if (evt.TryGetProperty("OnlineMeetingJoinUrl", out var omju) && omju.ValueKind == JsonValueKind.String)
                    joinUrl = omju.GetString();

                // Location might contain Teams URL
                if (joinUrl == null && evt.TryGetProperty("Location", out var loc))
                {
                    var locStr = loc.ValueKind == JsonValueKind.String ? loc.GetString()
                        : (loc.TryGetProperty("DisplayName", out var dn) ? dn.GetString() : null);
                    if (locStr != null && locStr.Contains("teams.microsoft.com", StringComparison.OrdinalIgnoreCase))
                        joinUrl = locStr;
                }

                // FreeBusyType
                var freeBusy = evt.TryGetProperty("FreeBusyType", out var fb) ? fb.GetString() : null;

                var organizer = evt.TryGetProperty("Organizer", out var org)
                    && org.TryGetProperty("EmailAddress", out var ea)
                    && ea.TryGetProperty("Name", out var orgName) ? orgName.GetString() : null;

                return new TeamsMeeting
                {
                    Subject = subject,
                    Start = start,
                    End = end,
                    TeamsUrl = joinUrl ?? "",
                    Organizer = organizer,
                    Location = evt.TryGetProperty("Location", out var l)
                        && l.TryGetProperty("DisplayName", out var ldn) ? ldn.GetString() : null
                };
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "CalendarCollectorTask: failed to parse individual OWA event");
                return null;
            }
        }
    }
}
