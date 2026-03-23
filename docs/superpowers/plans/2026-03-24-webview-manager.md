# WebViewManager Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the single TeamsWebViewWindow/TeamsWebViewDataExtractor with a multi-instance WebViewManager pool that orchestrates persistent and transient WebView2 instances for automated data gathering.

**Architecture:** WebViewManager (singleton) creates a shared CoreWebView2Environment and manages two slots: one persistent instance (MessageMonitor — always on Teams, WS hooks) and one transient slot for short-lived tasks (calendar, people enrichment, status). WebViewInstance wraps each WebView2 with JS eval, network interception, and contact auto-discovery. Tasks are scheduled or on-demand. All rendering offscreen.

**Tech Stack:** Microsoft.Web.WebView2, WPF .NET 8, System.Collections.Concurrent, DispatcherTimer

**Spec:** `docs/superpowers/specs/2026-03-23-webview-manager-design.md`

**CRITICAL SAFETY RULE:** Never make speculative API calls via fetch(). All data extraction is passive interception of responses from requests Teams/Outlook itself initiates. FindPerson search uses UI automation (type into Teams search box via JS, read DOM results).

---

### Task 1: Create WebViewInstance

**Files:**
- Create: `MeetNow/WebViewInstance.cs`

The core building block. Wraps a WebView2 in an offscreen WPF Window with common utilities.

- [ ] **Step 1: Create WebViewInstance.cs**

```csharp
using Microsoft.Web.WebView2.Core;
using MeetNow.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace MeetNow
{
    public enum InstanceType { Persistent, Transient }

    public class WebViewInstance : IDisposable
    {
        private static readonly string UserDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MeetNow", "WebView2Profile");

        private Window? _hostWindow;
        private Microsoft.Web.WebView2.Wpf.WebView2? _webView;
        private string? _capturedBearerToken;

        public string Name { get; }
        public InstanceType InstanceType { get; }
        public CoreWebView2? CoreWebView2 => _webView?.CoreWebView2;
        public bool IsReady { get; private set; }
        public string? CurrentUrl => _webView?.CoreWebView2?.Source;
        public string? CapturedBearerToken => _capturedBearerToken;
        public Window? HostWindow => _hostWindow;

        public event Action<string, string?, IDictionary<string, string>>? ResponseReceived;
        public event Action<string>? ContactDiscovered;

        public WebViewInstance(string name, InstanceType type)
        {
            Name = name;
            InstanceType = type;
        }

        public async Task InitializeAsync(CoreWebView2Environment environment)
        {
            // Create offscreen host window
            _hostWindow = new Window
            {
                Title = $"MeetNow WebView [{Name}]",
                Width = 1200, Height = 800,
                Left = -10000, Top = -10000,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow
            };

            _webView = new Microsoft.Web.WebView2.Wpf.WebView2();
            _hostWindow.Content = _webView;
            _hostWindow.Show();

            await _webView.EnsureCoreWebView2Async(environment);

            // Spoof Edge user-agent
            var edgeVersion = environment.BrowserVersionString;
            _webView.CoreWebView2.Settings.UserAgent =
                $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{edgeVersion} Safari/537.36 Edg/{edgeVersion}";

            // Subscribe to network events
            _webView.CoreWebView2.WebResourceResponseReceived += OnResponseReceived;
            _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

            Log.Information("WebViewInstance [{Name}] initialized", Name);
        }

        public async Task NavigateAndWaitAsync(string url, int timeoutMs = 15000)
        {
            if (_webView?.CoreWebView2 == null) return;

            IsReady = false;
            var tcs = new TaskCompletionSource<bool>();
            void handler(object? s, CoreWebView2NavigationCompletedEventArgs e)
            {
                _webView.CoreWebView2.NavigationCompleted -= handler;
                IsReady = e.IsSuccess;
                tcs.TrySetResult(e.IsSuccess);
            }
            _webView.CoreWebView2.NavigationCompleted += handler;
            _webView.CoreWebView2.Navigate(url);

            var timeout = Task.Delay(timeoutMs);
            await Task.WhenAny(tcs.Task, timeout);
            if (!tcs.Task.IsCompleted)
            {
                _webView.CoreWebView2.NavigationCompleted -= handler;
                Log.Warning("WebViewInstance [{Name}] navigation timeout: {Url}", Name, url);
            }
        }

        public async Task<string?> EvaluateJsAsync(string script)
        {
            if (_webView?.CoreWebView2 == null) return null;
            try
            {
                var result = await _webView.CoreWebView2.ExecuteScriptAsync(script);
                if (result == "null") return null;
                return JsonSerializer.Deserialize<string>(result);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "WebViewInstance [{Name}] JS eval failed", Name);
                return null;
            }
        }

        public async Task<bool> HeartbeatAsync()
        {
            try
            {
                var result = await EvaluateJsAsync("(function() { return 'alive'; })();");
                return result == "alive";
            }
            catch { return false; }
        }

        private async void OnResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            try
            {
                var uri = e.Request.Uri;

                // Capture bearer token passively (diagnostic only)
                if (uri.Contains("/api/mt/", StringComparison.OrdinalIgnoreCase)
                    || uri.Contains("/api/chatsvc/", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var authHeader = e.Request.Headers.GetHeader("Authorization");
                        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer "))
                            _capturedBearerToken = authHeader["Bearer ".Length..];
                    }
                    catch { }
                }

                // Contact auto-discovery from profile picture URLs
                TryExtractContact(uri);

                // Read body for interesting responses
                var status = e.Response.StatusCode;
                var contentType = e.Response.Headers.GetHeader("Content-Type") ?? "";
                if (status >= 200 && status < 300 && IsInterestingResponse(uri, contentType))
                {
                    string? body = null;
                    try
                    {
                        var stream = await e.Response.GetContentAsync();
                        if (stream != null)
                        {
                            using var reader = new StreamReader(stream);
                            body = await reader.ReadToEndAsync();
                        }
                    }
                    catch { }

                    // Passive enrichment from GetPersona
                    if (body != null)
                        TryEnrichFromGetPersona(uri, body);

                    // Notify subscribers
                    var headers = new Dictionary<string, string>();
                    ResponseReceived?.Invoke(uri, body, headers);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "WebViewInstance [{Name}] response processing error", Name);
            }
        }

        private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            IsReady = e.IsSuccess;
            Log.Information("WebViewInstance [{Name}] navigated: {Url} (success={Success})",
                Name, CurrentUrl, e.IsSuccess);
        }

        // Reuse proven contact extraction logic from TeamsWebViewDataExtractor
        private void TryExtractContact(string uri)
        {
            // ... same patterns as current TeamsWebViewDataExtractor.TryExtractContact:
            // Pattern 1: profilepicturev2/8:orgid:GUID?displayname=Name
            // Pattern 2: Loki Delve URL params (teamsMri + smtp)
            // Pattern 3: mergedProfilePicturev2?usersInfo=[...]
            // Copy the proven code from TeamsWebViewDataExtractor.cs
        }

        private void TryEnrichFromGetPersona(string uri, string body)
        {
            // ... same as current TeamsWebViewDataExtractor.TryEnrichFromGetPersona
            // Copy the proven code from TeamsWebViewDataExtractor.cs
        }

        private static bool IsInterestingResponse(string uri, string contentType)
        {
            if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                && !(contentType.Contains("x-javascript", StringComparison.OrdinalIgnoreCase)
                     && (uri.Contains("outlook.office.com") || uri.Contains("outlook.cloud.microsoft"))))
                return false;

            string[] patterns = { "/api/calendar/", "/me/calendarview", "/api/chatsvc/",
                "/messages", "/threads", "/presence/", "/status", "/api/mt/", "/api/csa/",
                "outlook.cloud.microsoft/", "startupdata.ashx", "service.svc",
                "loki.delve.office.com/api/" };
            foreach (var p in patterns)
                if (uri.Contains(p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public void Dispose()
        {
            if (_webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.WebResourceResponseReceived -= OnResponseReceived;
                _webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            }
            _webView?.Dispose();
            _hostWindow?.Close();
            Log.Information("WebViewInstance [{Name}] disposed", Name);
        }
    }
}
```

NOTE TO IMPLEMENTER: The `TryExtractContact` and `TryEnrichFromGetPersona` methods should be copied verbatim from `TeamsWebViewDataExtractor.cs` (the proven implementations). Do NOT write new implementations — read the existing file and copy.

- [ ] **Step 2: Build to verify**

```bash
cd MeetNow && dotnet build
```

- [ ] **Step 3: Commit**

```bash
git add MeetNow/WebViewInstance.cs
git commit -m "feat: add WebViewInstance with offscreen hosting, JS eval, and contact discovery"
```

---

### Task 2: Create WebViewManager

**Files:**
- Create: `MeetNow/WebViewManager.cs`

Singleton orchestrator managing persistent + transient slots, task scheduling, and lifecycle.

- [ ] **Step 1: Create WebViewManager.cs**

Key elements:
- Singleton with `Instance` property, private constructor
- `InitializeAsync()` creates shared `CoreWebView2Environment`
- `GetPersistentAsync(url)` creates/returns the always-on instance
- `AcquireTransientAsync(url)` creates on-demand instance (queues if busy)
- `ScheduleTask(name, interval, workFunc)` registers recurring tasks on DispatcherTimers
- `RequestTask(name, workFunc, CancellationToken)` runs one-off task (queues if busy, 60s expiry)
- `Shutdown()` disposes everything, called from MainWindow.OnClosed
- Transient idle timeout: 60s after Release(), dispose instance
- Task timeout: 120s max per transient task
- Auth redirect detection: if URL contains `login.microsoftonline.com`, back off 5 min
- Persistent heartbeat: every 60s check JS `return 'alive'`, recreate if dead

The implementer should write the full class following the spec's API surface. All DispatcherTimer-based (UI thread). Use `SemaphoreSlim(1,1)` for transient slot access.

- [ ] **Step 2: Build to verify**
- [ ] **Step 3: Commit**

```bash
git add MeetNow/WebViewManager.cs
git commit -m "feat: add WebViewManager singleton with persistent/transient slots and scheduling"
```

---

### Task 3: Create MessageMonitorTask

**Files:**
- Create: `MeetNow/Tasks/MessageMonitorTask.cs`

The persistent task — stays on Teams, injects WS hooks, probes state.

- [ ] **Step 1: Create Tasks/MessageMonitorTask.cs**

Key elements:
- Static class with `StartAsync(WebViewInstance instance)` method
- Injects WebSocket hook (same JS as current `InjectWebSocketHook`)
- Injects webpack explorer (same JS as current `InjectWebpackExplorer`)
- Starts DispatcherTimer-based probe (same as current `ProbeTeamsStateAsync` — WS harvest, title tracking)
- `Stop()` method stops the probe timer

Copy the proven JS injection code from `TeamsWebViewDataExtractor.cs` — the WS hook, webpack explorer, and probe timer logic.

- [ ] **Step 2: Build to verify**
- [ ] **Step 3: Commit**

```bash
git add MeetNow/Tasks/MessageMonitorTask.cs
git commit -m "feat: add MessageMonitorTask with WS hooks and state probing"
```

---

### Task 4: Create CalendarCollectorTask

**Files:**
- Create: `MeetNow/Tasks/CalendarCollectorTask.cs`

Transient task — navigates to Outlook calendar, captures events.

- [ ] **Step 1: Create Tasks/CalendarCollectorTask.cs**

Key elements:
- Static class with `async Task RunAsync(WebViewInstance instance)`
- Navigates to `https://outlook.cloud.microsoft/calendar/view/day`
- Waits for page load (NavigateAndWaitAsync)
- Waits additional 15s for OWA API calls to complete
- Subscribes to `instance.ResponseReceived` to capture calendar event bodies
- Parses Subject, Start, End, FreeBusyType, Teams join URLs from OWA `service.svc` and `startupdata.ashx` responses
- Stores parsed meetings in a static `LastCollectedMeetings` property for MeetingDataAggregator to consume
- Auth redirect detection: if navigation lands on `login.microsoftonline.com`, return early with warning

- [ ] **Step 2: Build to verify**
- [ ] **Step 3: Commit**

```bash
git add MeetNow/Tasks/CalendarCollectorTask.cs
git commit -m "feat: add CalendarCollectorTask with Outlook calendar event parsing"
```

---

### Task 5: Create PeopleEnricherTask

**Files:**
- Create: `MeetNow/Tasks/PeopleEnricherTask.cs`

Transient task — triggers profile card views to passively enrich contacts.

- [ ] **Step 1: Create Tasks/PeopleEnricherTask.cs**

Key elements:
- Static class with `async Task RunAsync(WebViewInstance instance)` (scheduled) and `async Task SearchAsync(WebViewInstance instance, string query)` (on-demand from FindPerson)
- **Scheduled run:** Navigate to Teams, iterate contacts with `EnrichmentStatus=Pending`, for each: use JS to click their chat in the sidebar → Teams loads their profile picture URLs and potentially contact card data → passive interception enriches them
- **On-demand search (FindPerson):** Use JS to type query into Teams search box (Ctrl+Alt+E simulation or direct DOM manipulation of the search input), wait for results to appear in DOM, parse result elements for name/email/userId
- All data extraction is passive — MeetNow only reads responses from requests Teams itself initiates
- NO fetch() calls with auth tokens

- [ ] **Step 2: Build to verify**
- [ ] **Step 3: Commit**

```bash
git add MeetNow/Tasks/PeopleEnricherTask.cs
git commit -m "feat: add PeopleEnricherTask with passive profile card enrichment"
```

---

### Task 6: Create DebugViewerWindow

**Files:**
- Create: `MeetNow/DebugViewerWindow.xaml`
- Create: `MeetNow/DebugViewerWindow.xaml.cs`

Optional development tool — shows offscreen instances by moving their host window on-screen.

- [ ] **Step 1: Create DebugViewerWindow.xaml**

Dark theme window with a ComboBox to select which instance to view. No WebView2 control of its own — it controls the visibility/position of instance host windows.

- [ ] **Step 2: Create DebugViewerWindow.xaml.cs**

Key elements:
- ComboBox populated with active instance names from WebViewManager
- Selecting an instance moves its host window from offscreen to visible position (e.g. centered on screen, 1200x800)
- Deselecting or closing moves it back offscreen (-10000,-10000)
- Hide on close (reusable from tray menu)

- [ ] **Step 3: Build to verify**
- [ ] **Step 4: Commit**

```bash
git add MeetNow/DebugViewerWindow.xaml MeetNow/DebugViewerWindow.xaml.cs
git commit -m "feat: add DebugViewerWindow for inspecting offscreen WebView2 instances"
```

---

### Task 7: Integrate WebViewManager into MainWindow

**Files:**
- Modify: `MeetNow/MainWindow.xaml` (menu items)
- Modify: `MeetNow/MainWindow.xaml.cs` (replace TeamsWebViewWindow with WebViewManager)
- Modify: `MeetNow/FindPersonWindow.xaml.cs` (use WebViewManager.RequestTask)
- Modify: `MeetNow/MeetingDataAggregator.cs` (get data from CalendarCollectorTask)

- [ ] **Step 1: Update MainWindow.xaml**

- Change `"Teams WebView"` menu item to `"Debug WebView"`
- Click handler now toggles DebugViewerWindow

- [ ] **Step 2: Update MainWindow.xaml.cs**

- Remove `_teamsWebViewWindow` field
- Add `_debugViewerWindow` field
- In constructor: call `WebViewManager.Instance.InitializeAsync()` instead of creating TeamsWebViewWindow
- In OnClosed: call `WebViewManager.Instance.Shutdown()` then `ContactDatabase.FlushAndDispose()`
- Update `MenuItem_TeamsWebViewClick` → `MenuItem_DebugWebViewClick` to toggle DebugViewerWindow

- [ ] **Step 3: Update FindPersonWindow.xaml.cs**

- Replace `Func<TeamsWebViewDataExtractor?>` with a reference to `WebViewManager.Instance`
- Remote search calls `WebViewManager.Instance.RequestTask("FindPerson", instance => PeopleEnricherTask.SearchAsync(instance, query))`
- Local search unchanged

- [ ] **Step 4: Update MeetingDataAggregator.cs**

- Replace `Func<TeamsWebViewDataExtractor?>` with reading `CalendarCollectorTask.LastCollectedMeetings`
- The CalendarCollector runs on a schedule and stores results; aggregator reads the cache

- [ ] **Step 5: Build and verify**

```bash
cd MeetNow && dotnet build
```

- [ ] **Step 6: Commit**

```bash
git add MeetNow/MainWindow.xaml MeetNow/MainWindow.xaml.cs MeetNow/FindPersonWindow.xaml.cs MeetNow/MeetingDataAggregator.cs
git commit -m "feat: integrate WebViewManager into MainWindow, FindPerson, and MeetingDataAggregator"
```

---

### Task 8: Remove Old WebView2 Files

**Files:**
- Delete: `MeetNow/TeamsWebViewWindow.xaml`
- Delete: `MeetNow/TeamsWebViewWindow.xaml.cs`
- Delete: `MeetNow/TeamsWebViewDataExtractor.cs`
- Modify: `MeetNow/ContactEnricher.cs` (remove or mark as deprecated — its logic moved to PeopleEnricherTask)

- [ ] **Step 1: Delete old files**

Remove TeamsWebViewWindow and TeamsWebViewDataExtractor. These are fully replaced by WebViewInstance + WebViewManager + tasks.

- [ ] **Step 2: Update ContactEnricher.cs**

Remove the API-calling enrichment logic (disabled anyway). Keep the class shell if anything references it, or remove entirely if all references have been updated.

- [ ] **Step 3: Build and verify** — fix any remaining references
- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "refactor: remove TeamsWebViewWindow and TeamsWebViewDataExtractor (replaced by WebViewManager)"
```

---

### Task 9: Manual Validation

**Files:** None (testing only)

- [ ] **Step 1: Run the app**

Verify:
1. No visible browser windows on startup (offscreen)
2. Tray menu has "Debug WebView" — opens debug viewer
3. Debug viewer dropdown shows "MessageMonitor" instance
4. Selecting it shows the Teams page
5. Contacts auto-discovered in contacts.json
6. Calendar data collected every 15 min (check log)
7. "Find Person" still works (local search + UI-automated remote search)
8. No 401 errors in logs
9. No unexpected browser windows visible to IT
