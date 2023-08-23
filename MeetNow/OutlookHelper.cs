using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
namespace MeetNow
{
    internal static class OutlookHelper
    {
        static T InvokeProperty<T>(this object item, string name)
        {
            return (T)item.GetType().InvokeMember(name, BindingFlags.GetProperty, null, item, null);
        }
        static T InvokeMethod<T>(this object item, string name)
        {
            return (T)item.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, item, null);
        }
        static T InvokeMethod1<T>(this object item, string name, object arg1)
        {
            return (T)item.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, item, new object[] { arg1 });
        }
        static T InvokeMethod2<T>(this object item, string name, object arg1, object arg2)
        {
            return (T)item.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, item, new object[] { arg1, arg2 });
        }

        static TeamsMeeting CreateTeamsMeeting(object item, bool reccurent, string username)
        {
            var retVal = new TeamsMeeting
            {
                Body = item.InvokeProperty<string>("Body"),
                Subject = item.InvokeProperty<string>("Subject"),
                Start = item.InvokeProperty<DateTime>("Start"),
                End = item.InvokeProperty<DateTime>("End"),
                Categories = item.InvokeProperty<string>("Categories"),
                Location =  item.InvokeProperty<string>("Location"),
                Organizer = item.InvokeProperty<string>("Organizer"),
                RTFBody = item.InvokeProperty<byte[]>("RTFBody"),

                Recurrent = reccurent,
            };

            if (retVal.Body.Contains("teams.microsoft.com/l/meetup-join"))
            {
                var match = Regex.Match(retVal.Body, @"https://teams\.microsoft\.com/l/meetup-join/[^ \t\n\r<>"+'"'+@"]+");
                if (match.Success)
                {
                    retVal.TeamsUrl = match.Value;
                }
            }
            try
            {
                var recepients = item.InvokeProperty<object>("Recipients");
                var recepientCount = recepients.InvokeProperty<int>("Count");
                for (int i = 1; i <= recepientCount; i++)
                {
                    var recepient = recepients.InvokeMethod1<object>("Item", i);
                    var name = recepient.InvokeProperty<string>("Name");
                    if (name == username)
                    {
                        retVal.ResponseStatus = (ResponseStatus)Enum.Parse(typeof(ResponseStatus), recepient.InvokeProperty<int>("MeetingResponseStatus").ToString());
                    }
                    Marshal.ReleaseComObject(recepient);
                }
                Marshal.ReleaseComObject(recepients);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Error parcing item {retVal.Subject}: {ex.Message}");
            }
            return retVal;
        }

        internal static (TeamsMeeting[], string) GetTeamsMeetings(DateTime date, bool debug = false)
        {
            if (debug)

            {
                return (new[]{ new TeamsMeeting {
                    Body = "[Daily (except Friday) portfolio sync] ",
                    Categories = "Blue category",
                    Start = date.AddSeconds(5),
                    End = date.AddMinutes(30),
                    IsRequired = true,
                    Location = "Microsoft Teams Meeting",
                    OptionalAttendees = new[] { "Christina White" },
                    RequiredAttendees = new[] { "Matt Black" },
                    Organizer = "Veronica Brown",
                    Recurrent = true,
                    ResponseStatus = ResponseStatus.olResponseAccepted,
                    Subject = "Portfolio Sync (Daily)",
                    TeamsUrl = @"https://teams.microsoft.com/l/meetup-join/"
                },new TeamsMeeting {
                    Body = "Hi All,\r\nMeeting placeholder for discussion and planning of Electric Trees Planting. Please let me know if this doesn’t work for you. \r\n\r\nRegards,\r\nMatt\r\n",
                    Categories = "Orange category",
                    Start = date.AddSeconds(5),
                    End = date.AddMinutes(30),
                    IsRequired = true,
                    Location = "Microsoft Teams Meeting",
                    OptionalAttendees = new[] { "Veronica Brown" },
                    RequiredAttendees = new[] { "Christina White" },
                    Organizer = "Matt Black",
                    Recurrent = true,
                    ResponseStatus = ResponseStatus.olResponseAccepted,
                    Subject = "Acid House data analysis discussion ",
                    TeamsUrl = @"https://teams.microsoft.com/l/meetup-join/"
                },new TeamsMeeting {
                    Body = "[Daily (except Friday) portfolio sync] ",
                    Categories = "Blue category",
                    Start = date.AddSeconds(5),
                    End = date.AddMinutes(30),
                    IsRequired = true,
                    Location = "Microsoft Teams Meeting",
                    OptionalAttendees = new[] { "Christina White" },
                    RequiredAttendees = new[] { "Matt Black" },
                    Organizer = "Veronica Brown",
                    Recurrent = true,
                    ResponseStatus = ResponseStatus.olResponseAccepted,
                    Subject = "Portfolio Sync (Daily)",
                    TeamsUrl = @"https://teams.microsoft.com/l/meetup-join/"
                },new TeamsMeeting {
                    Body = "Hi All,\r\nMeeting placeholder for discussion and planning of Electric Trees Planting. Please let me know if this doesn’t work for you. \r\n\r\nRegards,\r\nMatt\r\n",
                    Categories = "Orange category",
                    Start = date.AddSeconds(5),
                    End = date.AddMinutes(30),
                    IsRequired = true,
                    Location = "Microsoft Teams Meeting",
                    OptionalAttendees = new[] { "Veronica Brown" },
                    RequiredAttendees = new[] { "Christina White" },
                    Organizer = "Matt Black",
                    Recurrent = true,
                    ResponseStatus = ResponseStatus.olResponseAccepted,
                    Subject = "Acid House data analysis discussion ",
                    TeamsUrl = @"https://teams.microsoft.com/l/meetup-join/"
                },
                new TeamsMeeting {
                    Body = "[Daily (except Friday) portfolio sync] ",
                    Categories = "Blue category",
                    Start = date.AddSeconds(5),
                    End = date.AddMinutes(30),
                    IsRequired = true,
                    Location = "Microsoft Teams Meeting",
                    OptionalAttendees = new[] { "Christina White" },
                    RequiredAttendees = new[] { "Matt Black" },
                    Organizer = "Veronica Brown",
                    Recurrent = true,
                    ResponseStatus = ResponseStatus.olResponseAccepted,
                    Subject = "Portfolio Sync (Daily)",
                    TeamsUrl = @"https://teams.microsoft.com/l/meetup-join/"
                },new TeamsMeeting {
                    Body = "Hi All,\r\nMeeting placeholder for discussion and planning of Electric Trees Planting. Please let me know if this doesn’t work for you. \r\n\r\nRegards,\r\nMatt\r\n",
                    Categories = "Orange category",
                    Start = date.AddSeconds(5),
                    End = date.AddMinutes(30),
                    IsRequired = true,
                    Location = "Microsoft Teams Meeting",
                    OptionalAttendees = new[] { "Veronica Brown" },
                    RequiredAttendees = new[] { "Christina White" },
                    Organizer = "Matt Black",
                    Recurrent = true,
                    ResponseStatus = ResponseStatus.olResponseAccepted,
                    Subject = "Acid House data analysis discussion ",
                    TeamsUrl = @"https://teams.microsoft.com/l/meetup-join/"
                },
                new TeamsMeeting {
                    Body = "[Daily (except Friday) portfolio sync] ",
                    Categories = "Blue category",
                    Start = date.AddSeconds(5),
                    End = date.AddMinutes(30),
                    IsRequired = true,
                    Location = "Microsoft Teams Meeting",
                    OptionalAttendees = new[] { "Christina White" },
                    RequiredAttendees = new[] { "Matt Black" },
                    Organizer = "Veronica Brown",
                    Recurrent = true,
                    ResponseStatus = ResponseStatus.olResponseAccepted,
                    Subject = "Portfolio Sync (Daily)",
                    TeamsUrl = @"https://teams.microsoft.com/l/meetup-join/"
                }},
                    "Matt Black");
            }
            else
            {
                List<TeamsMeeting> retVal = new List<TeamsMeeting>();
                Type outlookAppType = Type.GetTypeFromProgID("Outlook.Application");
                object outlookApp = Activator.CreateInstance(outlookAppType);
                // Get my name

                var session = InvokeProperty<object>(outlookApp, "Session");
                string username = string.Empty;
                if (session != null)
                {
                    var currentUser = InvokeProperty<object>(session, "CurrentUser");
                    if (currentUser != null)
                    {
                        username = InvokeProperty<string>(currentUser, "Name");
                        Marshal.ReleaseComObject(currentUser);
                    }
                    Marshal.ReleaseComObject(session);
                }

                // Get the Namespace and Logon to the session
                object outlookNamespace = outlookApp.InvokeMethod1<object>("GetNamespace", "MAPI");

                // Get the Calendar Folder
                object olFolderCalendar = 9; // Corresponds to OlDefaultFolders.olFolderCalendar
                object calendarFolder = outlookNamespace.InvokeMethod1<object>("GetDefaultFolder", olFolderCalendar);

                DateTime startOfDay = date.Date;
                DateTime endOfDay = startOfDay.AddDays(1).AddSeconds(-1);

                object calendarItems = calendarFolder.InvokeProperty<object>("Items");
                calendarItems.InvokeMethod2<object>("Sort", "[Start]", Type.Missing);
                int totalItems = calendarItems.InvokeProperty<int>("Count");

                for (int i = 1; i <= totalItems; i++)
                {
                    object item = calendarItems.InvokeMethod1<object>("Item", i);

                    // Check if the item is a recurring appointment
                    bool isRecurring = item.InvokeProperty<bool>("IsRecurring");

                    if (isRecurring)
                    {
                        object recurrencePattern = item.InvokeMethod<object>("GetRecurrencePattern");

                        if (recurrencePattern != null)
                        {
                            DateTime currentDate = startOfDay;
                            DateTime startTime = item.InvokeProperty<DateTime>("Start");
                            try
                            {
                                currentDate= currentDate +startTime.TimeOfDay;
                                object occurrence = recurrencePattern.InvokeMethod1<object>("GetOccurrence", currentDate);
                                if (occurrence != null)
                                {
                                    retVal.Add(CreateTeamsMeeting(occurrence, true, username));
                                    Marshal.ReleaseComObject(occurrence);
                                }
                            }
                            catch // (Exception ex)
                            {
                                // too much exceptions here
                                // Log.Error(ex, $"Error while parcing reccurent item: {ex.Message}");
                            }

                            Marshal.ReleaseComObject(recurrencePattern);
                        }
                        else
                        {
                            Log.Information("Recurrence pattern not found.");
                        }
                    }
                    else
                    {
                        // For non-recurring appointments, directly process
                        DateTime itemStart = item.InvokeProperty<DateTime>("Start");
                        if (itemStart >= startOfDay && itemStart <= endOfDay)
                        {
                            retVal.Add(CreateTeamsMeeting(item, false, username));
                        }
                    }
                    Marshal.ReleaseComObject(item);
                }


                Marshal.ReleaseComObject(calendarItems);
                Marshal.ReleaseComObject(calendarFolder);
                Marshal.ReleaseComObject(outlookNamespace);
                Marshal.ReleaseComObject(outlookApp);
                return (retVal.ToArray(), username);
            }
        }
        internal static DateTime RoundDownToNearestInterval(DateTime dt, TimeSpan interval)
        {
            return new DateTime(((dt.Ticks + interval.Ticks - 1) / interval.Ticks) * interval.Ticks);
        }

        internal static void StartTeamsMeeting(string teamsUrl)
        {
            if (!string.IsNullOrEmpty(teamsUrl))
            {
                // Open Teams meeting URL
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    Arguments = $"/c start {teamsUrl}"
                });
            }
        }

    }
}
