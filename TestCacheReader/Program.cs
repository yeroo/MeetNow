using System;
using System.Collections.Generic;
using System.Linq;
using MeetNow;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();

Console.WriteLine("=== MeetNow Source Comparison: Cache vs COM for March 2026 ===\n");

// Check if Outlook COM is available
bool comAvailable = false;
try
{
    var procs = System.Diagnostics.Process.GetProcessesByName("OUTLOOK");
    comAvailable = procs.Length > 0;
}
catch { }

if (!comAvailable)
{
    Console.WriteLine("WARNING: Classic Outlook (OUTLOOK.EXE) is not running.");
    Console.WriteLine("COM source will not be available. Only cache results will be shown.\n");
}

var now = DateTime.Now;
int year = now.Year;
int month = now.Month;
int daysInMonth = DateTime.DaysInMonth(year, month);

int totalCacheOnly = 0, totalComOnly = 0, totalBoth = 0, totalDays = 0;

for (int day = 1; day <= daysInMonth; day++)
{
    var date = new DateTime(year, month, day);
    if (date > now.Date.AddDays(1)) break; // Don't check future dates beyond tomorrow

    TeamsMeeting[] cacheMeetings = Array.Empty<TeamsMeeting>();
    TeamsMeeting[] comMeetings = Array.Empty<TeamsMeeting>();

    // Source 1: Cache
    try
    {
        cacheMeetings = OutlookCacheReader.GetTodaysMeetings(date);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Cache error for {date:yyyy-MM-dd}: {ex.Message}");
    }

    // Source 2: COM
    if (comAvailable)
    {
        try
        {
            (comMeetings, _) = OutlookHelper.GetTeamsMeetings(date);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  COM error for {date:yyyy-MM-dd}: {ex.Message}");
        }
    }

    if (cacheMeetings.Length == 0 && comMeetings.Length == 0)
        continue;

    totalDays++;
    Console.WriteLine($"--- {date:yyyy-MM-dd} ({date:ddd}) ---");

    // Build lookup sets
    var cacheSet = cacheMeetings
        .Select(m => new MeetingKey(m.Subject?.Trim() ?? "", m.Start, m.End, !string.IsNullOrEmpty(m.TeamsUrl)))
        .OrderBy(m => m.Start)
        .ToList();

    var comSet = comMeetings
        .Select(m => new MeetingKey(m.Subject?.Trim() ?? "", m.Start, m.End, !string.IsNullOrEmpty(m.TeamsUrl)))
        .OrderBy(m => m.Start)
        .ToList();

    // Match by Subject + Start time (rounded to minute)
    var cacheKeys = new HashSet<string>(cacheSet.Select(m => m.Key), StringComparer.OrdinalIgnoreCase);
    var comKeys = new HashSet<string>(comSet.Select(m => m.Key), StringComparer.OrdinalIgnoreCase);

    var inBoth = cacheKeys.Intersect(comKeys, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var cacheOnly = cacheKeys.Except(comKeys, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var comOnly = comKeys.Except(cacheKeys, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);

    foreach (var m in cacheSet)
    {
        string tag;
        if (inBoth.Contains(m.Key))
            tag = "BOTH";
        else
            tag = "CACHE-ONLY";

        string urlTag = m.HasUrl ? " [Teams]" : "";
        Console.WriteLine($"  {m.Start:HH:mm}-{m.End:HH:mm}  {tag,-12} {m.Subject}{urlTag}");
    }

    foreach (var m in comSet.Where(m => comOnly.Contains(m.Key)))
    {
        string urlTag = m.HasUrl ? " [Teams]" : "";
        Console.WriteLine($"  {m.Start:HH:mm}-{m.End:HH:mm}  COM-ONLY     {m.Subject}{urlTag}");
    }

    totalBoth += inBoth.Count;
    totalCacheOnly += cacheOnly.Count;
    totalComOnly += comOnly.Count;

    Console.WriteLine();
}

Console.WriteLine("=== Summary ===");
Console.WriteLine($"  Days with meetings: {totalDays}");
Console.WriteLine($"  Matched (both):     {totalBoth}");
Console.WriteLine($"  Cache-only:         {totalCacheOnly}");
Console.WriteLine($"  COM-only:           {totalComOnly}");
if (!comAvailable)
    Console.WriteLine("  (COM was not available - Outlook not running)");

record MeetingKey(string Subject, DateTime Start, DateTime End, bool HasUrl)
{
    public string Key => $"{Subject}|{Start:HH:mm}";
}
