# WebView2 POC Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an embedded WebView2 control that loads Teams web to extract meeting/message data via network interception and JS evaluation, and to automate status changes — replacing fragile LevelDB parsing and keyboard simulation.

**Architecture:** MeetNow hosts a WPF window with a WebView2 control navigating to `teams.microsoft.com`. The extractor subscribes to network events and runs periodic JS evaluation to capture structured data. A new aggregator class merges data from WebView2, OutlookCacheReader, and TeamsMessageMonitor, deduplicating by composite keys before passing to the existing popup scheduling pipeline.

**Tech Stack:** Microsoft.Web.WebView2 (NuGet), WPF, .NET 8, System.Text.Json

**Spec:** `docs/superpowers/specs/2026-03-23-webview2-poc-design.md`

---

### Task 1: Add WebView2 NuGet Package

**Files:**
- Modify: `MeetNow/MeetNow.csproj:28-35` (PackageReference section)

- [ ] **Step 1: Add the WebView2 package reference**

Add to the `<ItemGroup>` containing other PackageReferences in `MeetNow/MeetNow.csproj`:

```xml
<PackageReference Include="Microsoft.Web.WebView2" Version="1.0.3124.44" />
```

- [ ] **Step 2: Restore packages and verify build**

Run:
```bash
cd MeetNow && dotnet restore && dotnet build
```
Expected: Build succeeds with no errors. WebView2 package restored.

- [ ] **Step 3: Commit**

```bash
git add MeetNow/MeetNow.csproj
git commit -m "feat: add Microsoft.Web.WebView2 NuGet package for POC"
```

---

### Task 2: Add Settings Properties

**Files:**
- Modify: `MeetNow/MeetNowSettings.cs:16-31` (add new properties)

- [ ] **Step 1: Add WebView2 settings to MeetNowSettings**

Add two new properties after the existing "Timings" section (after line 31) in `MeetNow/MeetNowSettings.cs`:

```csharp
// WebView2 POC
public bool ShowTeamsWebView { get; set; } = true;
public bool LogAllWebViewTraffic { get; set; } = true;
```

- [ ] **Step 2: Build to verify**

Run:
```bash
cd MeetNow && dotnet build
```
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add MeetNow/MeetNowSettings.cs
git commit -m "feat: add ShowTeamsWebView and LogAllWebViewTraffic settings"
```

---

### Task 3: Create TeamsWebViewWindow

**Files:**
- Create: `MeetNow/TeamsWebViewWindow.xaml`
- Create: `MeetNow/TeamsWebViewWindow.xaml.cs`

- [ ] **Step 1: Create the XAML file**

Create `MeetNow/TeamsWebViewWindow.xaml`:

```xml
<Window x:Class="MeetNow.TeamsWebViewWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:wv2="clr-namespace:Microsoft.Web.WebView2.Wpf;assembly=Microsoft.Web.WebView2.Wpf"
        Title="MeetNow — Teams WebView"
        Width="1200" Height="800"
        WindowStartupLocation="CenterScreen">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>
        <TextBlock x:Name="StatusBar" Grid.Row="0" Padding="6,4"
                   Background="#1B1A19" Foreground="#CCCCCC" FontSize="12"
                   Text="Initializing WebView2..."/>
        <wv2:WebView2 x:Name="webView" Grid.Row="1"/>
    </Grid>
</Window>
```

- [ ] **Step 2: Create the code-behind**

Create `MeetNow/TeamsWebViewWindow.xaml.cs`:

```csharp
using Microsoft.Web.WebView2.Core;
using Serilog;
using System;
using System.IO;
using System.Windows;

namespace MeetNow
{
    public partial class TeamsWebViewWindow : Window
    {
        private static readonly string UserDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MeetNow", "WebView2Profile");

        private bool _isInitialized;

        public TeamsWebViewWindow()
        {
            InitializeComponent();
        }

        public async void InitializeWebView()
        {
            if (_isInitialized) return;

            try
            {
                StatusBar.Text = "Creating WebView2 environment...";
                var env = await CoreWebView2Environment.CreateAsync(
                    userDataFolder: UserDataFolder);

                await webView.EnsureCoreWebView2Async(env);

                // Spoof Edge user-agent to avoid Teams blocking embedded access
                var edgeVersion = env.BrowserVersionString;
                webView.CoreWebView2.Settings.UserAgent =
                    $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{edgeVersion} Safari/537.36 Edg/{edgeVersion}";

                webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

                StatusBar.Text = "Navigating to Teams...";
                webView.CoreWebView2.Navigate("https://teams.microsoft.com");
                _isInitialized = true;

                Log.Information("WebView2 initialized, navigating to Teams");
            }
            catch (Exception ex)
            {
                StatusBar.Text = $"WebView2 init failed: {ex.Message}";
                Log.Error(ex, "Failed to initialize WebView2");
            }
        }

        private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            var url = webView.CoreWebView2.Source;
            if (e.IsSuccess)
            {
                StatusBar.Text = $"Loaded: {url}";
                Log.Information("WebView2 navigation completed: {Url}", url);
            }
            else
            {
                StatusBar.Text = $"Navigation failed ({e.WebErrorStatus}): {url}";
                Log.Warning("WebView2 navigation failed: {Status} {Url}", e.WebErrorStatus, url);
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Hide instead of close — re-show via tray menu
            e.Cancel = true;
            Hide();
        }

        public void DisposeWebView()
        {
            webView?.Dispose();
        }
    }
}
```

- [ ] **Step 3: Build to verify**

Run:
```bash
cd MeetNow && dotnet build
```
Expected: Build succeeds. No runtime test yet — just compilation.

- [ ] **Step 4: Commit**

```bash
git add MeetNow/TeamsWebViewWindow.xaml MeetNow/TeamsWebViewWindow.xaml.cs
git commit -m "feat: add TeamsWebViewWindow with WebView2 control and Edge user-agent spoofing"
```

---

### Task 4: Wire WebView2 Window into MainWindow

**Files:**
- Modify: `MeetNow/MainWindow.xaml:15-24` (add tray menu item)
- Modify: `MeetNow/MainWindow.xaml.cs` (launch window, add toggle handler)

- [ ] **Step 1: Add tray menu item**

In `MeetNow/MainWindow.xaml`, add a new MenuItem after the "Settings" item (line 20) and before the Separator (line 21):

```xml
<MenuItem Header="Teams WebView" Click="MenuItem_TeamsWebViewClick"/>
```

- [ ] **Step 2: Add field and launch logic to MainWindow.xaml.cs**

Add a field near the top of the `MainWindow` class (after other field declarations):

```csharp
private TeamsWebViewWindow? _teamsWebViewWindow;
```

Add the click handler method (near the other MenuItem handlers around line 310-333):

```csharp
private void MenuItem_TeamsWebViewClick(object sender, RoutedEventArgs e)
{
    if (_teamsWebViewWindow == null)
    {
        _teamsWebViewWindow = new TeamsWebViewWindow();
        _teamsWebViewWindow.InitializeWebView();
    }

    if (_teamsWebViewWindow.IsVisible)
        _teamsWebViewWindow.Hide();
    else
        _teamsWebViewWindow.Show();
}
```

- [ ] **Step 3: Launch on startup if setting enabled**

In the `MainWindow` constructor (after the existing initialization around line 54), add:

```csharp
if (MeetNowSettings.Instance.ShowTeamsWebView)
{
    _teamsWebViewWindow = new TeamsWebViewWindow();
    _teamsWebViewWindow.Show();
    _teamsWebViewWindow.InitializeWebView();
}
```

- [ ] **Step 4: Dispose on exit**

In the `OnClosed` override or exit handler in MainWindow.xaml.cs, add before the existing shutdown logic:

```csharp
_teamsWebViewWindow?.DisposeWebView();
```

- [ ] **Step 5: Build and manually test**

Run:
```bash
cd MeetNow && dotnet build
```
Expected: Build succeeds.

Manual test: Run the app. The WebView2 window should appear, navigate to `teams.microsoft.com`, and show the Teams login page (first run) or Teams web (if already authenticated). The tray menu should have a "Teams WebView" item that toggles the window.

- [ ] **Step 6: Commit**

```bash
git add MeetNow/MainWindow.xaml MeetNow/MainWindow.xaml.cs
git commit -m "feat: wire TeamsWebViewWindow into MainWindow with tray menu toggle"
```

---

### Task 5: Create TeamsWebViewDataExtractor — Traffic Logger

**Files:**
- Create: `MeetNow/TeamsWebViewDataExtractor.cs`
- Modify: `MeetNow/TeamsWebViewWindow.xaml.cs` (wire extractor to WebView2)

This task implements the discovery/logging layer first. Data parsing comes in Task 6.

- [ ] **Step 1: Create the extractor class with traffic logging**

Create `MeetNow/TeamsWebViewDataExtractor.cs`:

```csharp
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
```

- [ ] **Step 2: Wire extractor into TeamsWebViewWindow**

In `MeetNow/TeamsWebViewWindow.xaml.cs`, add a field:

```csharp
private TeamsWebViewDataExtractor? _extractor;
```

In the `InitializeWebView()` method, after `_isInitialized = true;`, add:

```csharp
// Attach data extractor
_extractor = new TeamsWebViewDataExtractor(
    MeetNowSettings.Instance.LogAllWebViewTraffic);
_extractor.Attach(webView.CoreWebView2);
```

Add a public property to expose the extractor:

```csharp
public TeamsWebViewDataExtractor? Extractor => _extractor;
```

In `DisposeWebView()`, add before `webView?.Dispose()`:

```csharp
_extractor?.Detach();
```

- [ ] **Step 3: Build and manually test**

Run:
```bash
cd MeetNow && dotnet build
```
Expected: Build succeeds.

Manual test: Run the app, let Teams web load, interact with it. Check `%TEMP%\MeetNow_WebView_Traffic.log` — it should contain logged HTTP requests with URLs, status codes, and content types. Interesting URLs should have body content logged.

- [ ] **Step 4: Commit**

```bash
git add MeetNow/TeamsWebViewDataExtractor.cs MeetNow/TeamsWebViewWindow.xaml.cs
git commit -m "feat: add TeamsWebViewDataExtractor with network traffic logging"
```

---

### Task 6: Add JS Evaluation for State Probing

**Files:**
- Modify: `MeetNow/TeamsWebViewDataExtractor.cs` (add JS evaluation methods)
- Modify: `MeetNow/TeamsWebViewWindow.xaml.cs` (start JS probe timer after navigation)

- [ ] **Step 1: Add JS evaluation methods to extractor**

Add the following to `TeamsWebViewDataExtractor.cs`:

```csharp
private DispatcherTimer? _jsProbeTimer;
private bool _jsProbingStarted;

public void StartJsProbing(int intervalSeconds = 30)
{
    if (_jsProbingStarted) return; // Guard against multiple navigation events
    _jsProbingStarted = true;

    _jsProbeTimer = new DispatcherTimer
    {
        Interval = TimeSpan.FromSeconds(intervalSeconds)
    };
    _jsProbeTimer.Tick += async (s, e) => await ProbeTeamsStateAsync();
    _jsProbeTimer.Start();

    Log.Information("JS state probing started (every {Interval}s)", intervalSeconds);
}

public void StopJsProbing()
{
    _jsProbeTimer?.Stop();
    _jsProbeTimer = null;
    _jsProbingStarted = false;
}

private async Task ProbeTeamsStateAsync()
{
    if (_webView == null) return;

    try
    {
        // Probe 1: Check what global state objects exist
        var globalsJs = @"
(function() {
    try {
        var found = {};
        // Check common React/Redux state patterns
        if (window.__REDUX_STORE__) found.redux = true;
        if (window.__NEXT_DATA__) found.nextData = true;
        if (window.store) found.store = true;
        if (window.__appState) found.appState = true;

        // Check for Teams-specific globals
        var teamsKeys = Object.keys(window).filter(function(k) {
            return k.toLowerCase().indexOf('teams') >= 0 ||
                   k.toLowerCase().indexOf('skype') >= 0 ||
                   k.toLowerCase().indexOf('presence') >= 0 ||
                   k.toLowerCase().indexOf('calendar') >= 0;
        });
        if (teamsKeys.length > 0) found.teamsGlobals = teamsKeys;

        // Check document title for state indicators
        found.title = document.title;
        found.url = window.location.href;

        return JSON.stringify(found);
    } catch(e) {
        return JSON.stringify({ error: e.message });
    }
})();";

        var result = await _webView.ExecuteScriptAsync(globalsJs);
        if (result != null && result != "null")
        {
            // ExecuteScriptAsync returns a JSON-encoded string, so the result
            // is double-quoted. Parse it to get the inner JSON.
            var inner = JsonSerializer.Deserialize<string>(result);
            if (inner != null)
            {
                LogTraffic($"  JS_PROBE globals: {inner}");
                Log.Debug("JS probe result: {Result}", inner);
            }
        }
    }
    catch (Exception ex)
    {
        Log.Debug(ex, "JS probe failed (page may still be loading)");
    }
}

/// <summary>
/// Execute arbitrary JS in the Teams page context. For POC exploration.
/// </summary>
public async Task<string?> EvaluateJsAsync(string script)
{
    if (_webView == null) return null;

    try
    {
        var result = await _webView.ExecuteScriptAsync(script);
        if (result == "null") return null;
        return JsonSerializer.Deserialize<string>(result);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "JS evaluation failed");
        return null;
    }
}
```

- [ ] **Step 2: Start JS probing after Teams loads**

In `TeamsWebViewWindow.xaml.cs`, modify `OnNavigationCompleted` to start probing once Teams is loaded:

```csharp
private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
{
    var url = webView.CoreWebView2.Source;
    if (e.IsSuccess)
    {
        StatusBar.Text = $"Loaded: {url}";
        Log.Information("WebView2 navigation completed: {Url}", url);

        // Start JS probing once Teams web has loaded
        if (url.Contains("teams.microsoft.com", StringComparison.OrdinalIgnoreCase))
        {
            _extractor?.StartJsProbing();
        }
    }
    else
    {
        StatusBar.Text = $"Navigation failed ({e.WebErrorStatus}): {url}";
        Log.Warning("WebView2 navigation failed: {Status} {Url}", e.WebErrorStatus, url);
    }
}
```

In `DisposeWebView()`, add before `_extractor?.Detach()`:

```csharp
_extractor?.StopJsProbing();
```

- [ ] **Step 3: Build and manually test**

Run:
```bash
cd MeetNow && dotnet build
```
Expected: Build succeeds.

Manual test: Run the app, let Teams load fully (past login). Check `%TEMP%\MeetNow_WebView_Traffic.log` for `JS_PROBE globals:` entries. These will reveal what Teams-specific global state objects are accessible — this is the discovery output the POC needs.

- [ ] **Step 4: Commit**

```bash
git add MeetNow/TeamsWebViewDataExtractor.cs MeetNow/TeamsWebViewWindow.xaml.cs
git commit -m "feat: add JS evaluation and state probing for Teams web discovery"
```

---

### Task 7: Create MeetingDataAggregator

**Files:**
- Create: `MeetNow/MeetingDataAggregator.cs`

- [ ] **Step 1: Create the aggregator class**

Create `MeetNow/MeetingDataAggregator.cs`:

```csharp
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MeetNow
{
    public class MeetingDataAggregator
    {
        private readonly Func<TeamsWebViewDataExtractor?> _getExtractor;

        public MeetingDataAggregator(Func<TeamsWebViewDataExtractor?>? getExtractor = null)
        {
            _getExtractor = getExtractor ?? (() => null);
        }

        /// <summary>
        /// Get today's meetings from all sources, deduplicated and merged.
        /// </summary>
        public TeamsMeeting[] GetMeetings(DateTime date, string outlookSource, bool debug = false)
        {
            var allMeetings = new List<TeamsMeeting>();

            // Source 1: OutlookCacheReader (LevelDB from New Outlook)
            if (outlookSource == "New")
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

            // Source 2: WebView2 data extractor (lazily resolved — may not be ready yet)
            var extractor = _getExtractor();
            if (extractor != null)
            {
                try
                {
                    var webViewMeetings = extractor.GetMeetings();
                    Log.Debug("WebView2 extractor returned {Count} meetings", webViewMeetings.Count);
                    allMeetings.AddRange(webViewMeetings);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "WebView2 extractor failed");
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
```

- [ ] **Step 2: Build to verify**

Run:
```bash
cd MeetNow && dotnet build
```
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add MeetNow/MeetingDataAggregator.cs
git commit -m "feat: add MeetingDataAggregator with multi-source dedup and merge"
```

---

### Task 8: Integrate Aggregator into MainWindow.RefreshOutlook

**Files:**
- Modify: `MeetNow/MainWindow.xaml.cs:345-485` (RefreshOutlook method)

- [ ] **Step 1: Add aggregator field**

Add a field to `MainWindow` class:

```csharp
private MeetingDataAggregator? _meetingAggregator;
```

- [ ] **Step 2: Initialize aggregator after WebView2 window creation**

In the MainWindow constructor, after the WebView2 window creation block, add:

```csharp
_meetingAggregator = new MeetingDataAggregator(
    () => _teamsWebViewWindow?.Extractor);
```

- [ ] **Step 3: Refactor RefreshOutlook to use aggregator**

Replace the meeting source selection logic in `RefreshOutlook()` (lines 353-390 approximately) with:

```csharp
TeamsMeeting[] meetings;
if (_meetingAggregator != null)
{
    meetings = _meetingAggregator.GetMeetings(
        now, MeetNowSettings.Instance.OutlookSource, debug);
}
else
{
    // Fallback: direct source access (aggregator not yet initialized)
    if (MeetNowSettings.Instance.OutlookSource == "New")
        meetings = OutlookCacheReader.GetTodaysMeetings(now);
    else
    {
        var (m, _) = OutlookHelper.GetTeamsMeetings(now, debug);
        meetings = m ?? Array.Empty<TeamsMeeting>();
    }
}
```

The rest of `RefreshOutlook()` (filtering, scheduling, menu building) remains unchanged — it already works with `TeamsMeeting[]`.

- [ ] **Step 4: Build and manually test**

Run:
```bash
cd MeetNow && dotnet build
```
Expected: Build succeeds. Existing meeting popup functionality should work exactly as before (WebView2 source won't contribute data yet — it only logs traffic at this point).

- [ ] **Step 5: Commit**

```bash
git add MeetNow/MainWindow.xaml.cs
git commit -m "feat: integrate MeetingDataAggregator into RefreshOutlook pipeline"
```

---

### Task 9: Add Settings UI for WebView2 Toggle

**Files:**
- Modify: `MeetNow/SettingsWindow.xaml` (add checkbox)
- Modify: `MeetNow/SettingsWindow.xaml.cs` (load/save logic)

- [ ] **Step 1: Add checkbox to SettingsWindow.xaml**

Add a new section after the "Autopilot Behavior" section (around line 112) in `MeetNow/SettingsWindow.xaml`:

```xml
<!-- WebView2 POC -->
<TextBlock Text="WebView2" FontWeight="Bold" FontSize="14" Margin="0,12,0,4" Foreground="#DDD"/>
<CheckBox x:Name="ShowWebViewCheckBox" Content="Show Teams WebView window"
          Foreground="#DDD" FontSize="13"
          Margin="0,4" Checked="ShowWebViewCheckBox_Changed"
          Unchecked="ShowWebViewCheckBox_Changed"/>
<CheckBox x:Name="LogTrafficCheckBox" Content="Log all WebView2 network traffic (POC)"
          Foreground="#DDD" FontSize="13"
          Margin="0,4" Checked="LogTrafficCheckBox_Changed"
          Unchecked="LogTrafficCheckBox_Changed"/>
```

- [ ] **Step 2: Add load/save handlers to SettingsWindow.xaml.cs**

In `LoadSettings()`, add after the existing checkbox loading:

```csharp
ShowWebViewCheckBox.IsChecked = settings.ShowTeamsWebView;
LogTrafficCheckBox.IsChecked = settings.LogAllWebViewTraffic;
```

Add event handlers:

```csharp
private void ShowWebViewCheckBox_Changed(object sender, RoutedEventArgs e)
{
    var settings = MeetNowSettings.Instance;
    settings.ShowTeamsWebView = ShowWebViewCheckBox.IsChecked == true;
    settings.Save();
}

private void LogTrafficCheckBox_Changed(object sender, RoutedEventArgs e)
{
    var settings = MeetNowSettings.Instance;
    settings.LogAllWebViewTraffic = LogTrafficCheckBox.IsChecked == true;
    settings.Save();
}
```

- [ ] **Step 3: Build to verify**

Run:
```bash
cd MeetNow && dotnet build
```
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add MeetNow/SettingsWindow.xaml MeetNow/SettingsWindow.xaml.cs
git commit -m "feat: add WebView2 settings UI (show window toggle, traffic logging)"
```

---

### Task 10: Manual Validation & POC Checkpoint

**Files:** None (testing and discovery only)

This is a manual validation step. No code changes.

- [ ] **Step 1: Full app launch test**

Run the app. Verify:
1. WebView2 window opens on startup (if `ShowTeamsWebView = true`)
2. Teams web loads and you can log in
3. After login, Teams web is functional (can see chats, calendar)
4. Tray menu "Teams WebView" toggles the window
5. Settings window shows WebView2 checkboxes
6. Existing meeting popups still work (from OutlookCacheReader)

- [ ] **Step 2: Check traffic log**

Open `%TEMP%\MeetNow_WebView_Traffic.log`. Verify:
1. HTTP requests are being logged with URLs, status codes, content types
2. Interesting endpoints (calendar, chatsvc, presence) have response bodies logged
3. JS probe results are logged — note what Teams-specific globals are found

- [ ] **Step 3: Document findings**

Create a brief notes file at `docs/superpowers/specs/2026-03-23-webview2-poc-findings.md` with:
- Which Teams API endpoints were discovered
- Which response bodies were successfully captured (and which returned null)
- What JS global state objects were found
- Whether user-agent spoofing worked or if Teams showed any interstitials
- Any Azure AD / Conditional Access issues encountered

This document is the primary output of the POC — it informs what to build next.

- [ ] **Step 4: Commit findings**

```bash
git add docs/superpowers/specs/2026-03-23-webview2-poc-findings.md
git commit -m "docs: WebView2 POC discovery findings"
```
