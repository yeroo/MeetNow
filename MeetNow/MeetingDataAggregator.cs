using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MeetNow
{
    public class MeetingDataAggregator
    {
        /// <summary>
        /// Get today's meetings from all sources, deduplicated and merged.
        /// </summary>
        public TeamsMeeting[] GetMeetings(DateTime date, string outlookSource, bool debug = false)
        {
            var allMeetings = new List<TeamsMeeting>();

            if (outlookSource == "WebView")
            {
                // WebView-only: calendar data comes from CalendarCollectorTask
                try
                {
                    var webViewMeetings = Tasks.CalendarCollectorTask.LastCollectedMeetings;
                    Log.Debug("CalendarCollectorTask returned {Count} cached meetings", webViewMeetings.Length);
                    allMeetings.AddRange(webViewMeetings);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "CalendarCollectorTask cache read failed");
                }
            }
            else if (outlookSource == "New")
            {
                try
                {
                    var outlookMeetings = OutlookCacheReader.GetTodaysMeetings(date);
                    Log.Debug("OutlookCacheReader returned {Count} meetings", outlookMeetings.Length);
                    allMeetings.AddRange(outlookMeetings);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "OutlookCacheReader failed");
                }
            }
            else
            {
                try
                {
                    var (meetings, _) = OutlookHelper.GetTeamsMeetings(date, debug);
                    Log.Debug("OutlookHelper returned {Count} meetings", meetings?.Length ?? 0);
                    if (meetings != null)
                        allMeetings.AddRange(meetings);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "OutlookHelper failed");
                }
            }

            // Deduplicate
            return Deduplicate(allMeetings);
        }

        /// <summary>
        /// Deduplicate meetings by normalized subject + start time.
        /// When duplicates found, prefer the entry with the richest data.
        /// </summary>
        private static TeamsMeeting[] Deduplicate(List<TeamsMeeting> meetings)
        {
            var grouped = meetings.GroupBy(m => new
            {
                Subject = (m.Subject ?? "").Trim().ToLowerInvariant(),
                Start = new DateTime(m.Start.Year, m.Start.Month, m.Start.Day,
                    m.Start.Hour, m.Start.Minute, 0)
            });

            var result = new List<TeamsMeeting>();
            foreach (var group in grouped)
            {
                var best = group.First();
                foreach (var candidate in group.Skip(1))
                {
                    best = MergeMeetings(best, candidate);
                }
                result.Add(best);
            }

            return result.OrderBy(m => m.Start).ToArray();
        }

        /// <summary>
        /// Merge two duplicate meetings, preferring whichever has richer data for each field.
        /// </summary>
        private static TeamsMeeting MergeMeetings(TeamsMeeting a, TeamsMeeting b)
        {
            return new TeamsMeeting
            {
                Start = a.Start,
                End = a.End != default ? a.End : b.End,
                Subject = !string.IsNullOrWhiteSpace(a.Subject) ? a.Subject : b.Subject,
                TeamsUrl = !string.IsNullOrWhiteSpace(a.TeamsUrl) ? a.TeamsUrl : b.TeamsUrl,
                Recurrent = a.Recurrent || b.Recurrent,
                ResponseStatus = a.ResponseStatus != ResponseStatus.olResponseNone
                    ? a.ResponseStatus : b.ResponseStatus,
                Location = !string.IsNullOrWhiteSpace(a.Location) ? a.Location : b.Location,
                Organizer = !string.IsNullOrWhiteSpace(a.Organizer) ? a.Organizer : b.Organizer,
                IsRequired = a.IsRequired || b.IsRequired,
                RequiredAttendees = (a.RequiredAttendees?.Length ?? 0) >= (b.RequiredAttendees?.Length ?? 0)
                    ? a.RequiredAttendees : b.RequiredAttendees,
                OptionalAttendees = (a.OptionalAttendees?.Length ?? 0) >= (b.OptionalAttendees?.Length ?? 0)
                    ? a.OptionalAttendees : b.OptionalAttendees,
                Body = !string.IsNullOrWhiteSpace(a.Body) ? a.Body : b.Body,
                Categories = !string.IsNullOrWhiteSpace(a.Categories) ? a.Categories : b.Categories,
                RTFBody = (a.RTFBody?.Length ?? 0) >= (b.RTFBody?.Length ?? 0) ? a.RTFBody : b.RTFBody,
            };
        }
    }
}
