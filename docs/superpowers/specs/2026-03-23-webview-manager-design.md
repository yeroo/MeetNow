# WebViewManager: Multi-Instance WebView2 Automation Pool

## Problem

The current WebView2 POC uses a single WebView2 instance for everything — calendar discovery, contact enrichment, message monitoring. This causes conflicts (calendar navigation interrupts chat monitoring), makes it impossible to run tasks in parallel, and leaves a visible browser window that may attract IT attention.

## Solution

Replace the single WebView2 window with a **WebViewManager** that orchestrates multiple purpose-built WebView2 instances. Each instance is dedicated to one automation task, runs offscreen, and shares the same auth cookies via a common user data profile.

## Constraints

- Same as all MeetNow constraints: no admin, no registry, no Graph API app registration, IT-invisible
- Shared `CoreWebView2Environment` to minimize overhead by sharing the browser process and user data folder. IT will see standard WebView2 process trees (3-5 msedgewebview2.exe processes) similar to any Electron app — this is normal and expected.
- All instances share `%LOCALAPPDATA%\MeetNow\WebView2Profile` for cookie/auth persistence
- No speculative API calls — only intercept responses from requests that Teams/Outlook itself initiates
- WebView2 Runtime pre-installed on Windows 11

## Architecture

### Instance Slots

Two slots managed by WebViewManager:

- **Persistent slot:** One long-lived instance (MessageMonitor). Always connected to Teams, maintains Trouter WebSocket for real-time notifications. Never disposed until app exit.
- **Transient slot:** One short-lived instance for brief tasks (calendar collection, people enrichment, status setting, chat automation). Created on demand, disposed after 60s idle. If busy when a new task arrives: scheduled tasks skip, on-demand tasks queue.

### Rendering

All instances render offscreen (invisible WPF Window at -10000,-10000). A **DebugViewerWindow** can attach to any instance on demand for development/troubleshooting. In production, no visible browser windows.

## Detailed Design

### 1. WebViewManager

**`WebViewManager.cs`** — Singleton instance (not static — WebView2 requires UI thread affinity and singleton enables constructor injection into FindPersonWindow/MeetingDataAggregator). All public API calls must be made from the UI/Dispatcher thread.

**Public API:**
- `InitializeAsync()` — creates shared `CoreWebView2Environment` with `%LOCALAPPDATA%\MeetNow\WebView2Profile`. Called once at app startup.
- `AcquireTransientAsync(string url)` → `WebViewInstance` — get a short-lived instance navigated to the given URL. If transient slot is busy, queues and waits.
- `GetPersistentAsync(string url)` → `WebViewInstance` — get or create the persistent instance. Creates on first call, returns existing on subsequent calls.
- `ScheduleTask(string name, TimeSpan interval, Func<WebViewInstance, Task> work)` — register a recurring transient task. If transient slot is busy at trigger time, skips that cycle.
- `RequestTask(string name, Func<WebViewInstance, Task> work, CancellationToken ct = default)` → `Task` — run a one-off transient task (queues if busy, cancellable). Queued items expire after 60s. Returns when task completes or is cancelled.
- `AttachDebugViewer(string instanceName)` — reparent an instance's WebView2 into the DebugViewerWindow.
- `Shutdown()` — dispose all instances and environment. Called at app exit.

**Internal state:**
- `_environment` — shared `CoreWebView2Environment`
- `_persistentInstance` — the always-on MessageMonitor instance (nullable, created lazily)
- `_transientInstance` — current transient instance (nullable, created on demand)
- `_transientQueue` — `ConcurrentQueue` of pending transient task requests
- `_scheduledTasks` — dictionary of name → (interval, work func, DispatcherTimer)
- `_transientIdleTimer` — disposes transient instance after 60s idle

### 2. WebViewInstance

**`WebViewInstance.cs`** — Wraps a single WebView2 control hosted in an offscreen WPF Window.

**Properties:**
- `CoreWebView2` — the underlying browser control
- `IsReady` — true after navigation completes
- `CurrentUrl` — current page URL
- `InstanceType` — enum: Persistent, Transient
- `Name` — identifier (e.g. "MessageMonitor", "CalendarCollector")
- `CapturedBearerToken` — passively captured from Teams API request Authorization headers. For diagnostic/logging only — never reused for speculative API calls.

**Methods:**
- `EvaluateJsAsync(string script)` → `string?` — execute JS, deserialize result
- `NavigateAndWaitAsync(string url, int timeoutMs = 15000)` — navigate and wait for completion
- `Release()` — mark as available for reuse or disposal

**Events:**
- `ResponseReceived(string uri, string? body, IDictionary<string, string> headers)` — tasks subscribe to intercept specific traffic
- `ContactDiscovered(string teamsUserId)` — fired by built-in profile URL parser (runs on all instances)

**Built-in behaviors (run on all instances):**
- Contact auto-discovery from profile picture URLs (same logic as current TryExtractContact)
- Contact enrichment from Loki Delve URL parameters (email from `smtp=` param)
- Contact enrichment from GetPersona response bodies (full profile)
- Bearer token capture from Teams API request Authorization headers
- Edge user-agent spoofing

**Lifecycle:**
- Created by WebViewManager via `CoreWebView2Environment.CreateAsync()` + offscreen WPF Window
- Transient instances stay alive for 60s after Release (reuse saves ~2s init cost)
- Disposed by WebViewManager (idle timeout or Shutdown)

### 3. Built-in Automation Tasks

Registered by WebViewManager at startup:

#### MessageMonitorTask (Persistent)
- **URL:** `https://teams.microsoft.com`
- **Schedule:** Always on
- **Work:** Inject WebSocket hooks (Trouter interception), JS state probing. Stays connected indefinitely. Contact auto-discovery runs continuously from profile picture URL traffic.

#### CalendarCollectorTask (Transient)
- **URL:** `https://outlook.cloud.microsoft/calendar/view/day`
- **Schedule:** Every 15 minutes
- **Duration:** ~30 seconds
- **Work:** Navigate to Outlook calendar, wait for page load, intercept OWA `service.svc` responses and `startupdata.ashx` for calendar event data. Parse Subject, Start, End, FreeBusyType, Teams join URLs. Store results for MeetingDataAggregator.

#### PeopleEnricherTask (Transient)
- **URL:** `https://teams.microsoft.com`
- **Schedule:** Every 30 minutes + on-demand (Find Person)
- **Duration:** ~60 seconds
- **Work:** Navigate Teams chat list. For contacts in ContactDatabase with EnrichmentStatus=Pending, trigger profile card views via JS (click on contact names to spawn Loki/GetPersona calls). Passively capture the enrichment data from intercepted responses.

### 4. Debug Viewer

**`DebugViewerWindow.xaml/.cs`** — Optional development tool.

- Single window, dark theme, shows one WebView2 instance at a time
- Dropdown to select instance (MessageMonitor, current transient task)
- Moves the instance's host window from offscreen to a visible position and resizes it (avoids WebView2 reparenting issues — WPF WebView2 does not officially support reparenting between windows)
- When closed, moves the host window back offscreen
- Toggle via tray menu: "Debug WebView"
- Only available when `MeetNowSettings.Instance.ShowTeamsWebView == true`

### 5. File Structure

**New files:**
| File | Purpose |
|------|---------|
| `WebViewManager.cs` | Pool orchestrator, scheduling, acquire/release |
| `WebViewInstance.cs` | Single instance wrapper with JS eval, interception, navigation |
| `DebugViewerWindow.xaml/.cs` | Optional debug viewer |
| `Tasks/MessageMonitorTask.cs` | Persistent: Trouter WS hooks, contact discovery |
| `Tasks/CalendarCollectorTask.cs` | Transient: Outlook calendar collection |
| `Tasks/PeopleEnricherTask.cs` | Transient: Profile card enrichment |

**Modified files:**
| File | Change |
|------|--------|
| `MainWindow.xaml` | "Teams WebView" → "Debug WebView" |
| `MainWindow.xaml.cs` | Replace TeamsWebViewWindow with WebViewManager.InitializeAsync()/Shutdown() |
| `FindPersonWindow.xaml.cs` | Remote search via UI automation: RequestTask types query into Teams search box via JS, reads results from DOM. No speculative API calls. |
| `MeetingDataAggregator.cs` | Get calendar data from CalendarCollector results |

**Removed files:**
| File | Reason |
|------|--------|
| `TeamsWebViewWindow.xaml/.cs` | Replaced by WebViewManager + DebugViewerWindow |
| `TeamsWebViewDataExtractor.cs` | Refactored into WebViewInstance |

### 6. Health Monitoring & Recovery

- **Persistent instance heartbeat:** Every 60 seconds, execute `return 'alive'` via JS eval. If it fails or times out (5s), log a warning and recreate the instance.
- **Transient task timeout:** Maximum 120 seconds per transient task. If exceeded, force-dispose the instance and log an error. Queued tasks proceed with a fresh instance.
- **Auth redirect detection:** If any instance detects navigation to `login.microsoftonline.com` (auth redirect), log a warning, skip the current task, and back off for 5 minutes. Surface a tray notification: "MeetNow: Please sign in via Debug WebView."
- **Offscreen rendering note:** If offscreen positioning (-10000,-10000) causes rendering issues (some WebView2 features require visible layout), fall back to a 1x1 pixel window with `ShowInTaskbar=false`.

### 7. Safety

- **No speculative API calls.** All data extraction is passive — intercept responses from requests that Teams/Outlook initiates via normal UI navigation. MeetNow never calls `fetch()` with auth tokens.
- **PeopleEnricher triggers enrichment by navigating the UI** (clicking contact names), causing Teams to make its own authenticated GetPersona/Loki requests. MeetNow only reads the responses.
- **FindPerson remote search uses UI automation** — types the query into Teams' own search box via JS, reads results from the DOM. Teams itself makes the authenticated search API calls. MeetNow intercepts the responses passively.
- **Bearer tokens are passively captured** from request headers for diagnostic/logging only — never reused for speculative API calls.
- **Offscreen rendering** — no visible browser windows in production. IT sees standard WebView2 process trees similar to any Electron app.

## What This Spec Does NOT Cover

- Implementing StatusSetter and ChatAutomator tasks (future sub-projects)
- Replacing LevelDB-based message monitoring (MessageMonitorTask coexists with existing TeamsMessageMonitor)
- Replacing LevelDB-based calendar reading (CalendarCollectorTask coexists with existing OutlookCacheReader)
- MeetNowSettings UI changes for per-task scheduling configuration
