# WebView2 POC: Teams Data Extraction & Status Automation

## Problem

MeetNow currently extracts data from Teams and Outlook via fragile approaches:
- **Meetings:** Parsing LevelDB/IndexedDB files from New Outlook's Chromium cache (Snappy-compressed SSTable files with undocumented schema)
- **Messages:** Parsing Teams LevelDB with V8 binary serialization format (brittle byte-level parsing)
- **Status automation:** Win32 keyboard/mouse simulation of slash commands (`/available`, `/busy`, etc.) — unreliable, breaks on UI changes

These approaches work but are fragile, hard to maintain, and limited in what data they can access.

## Solution

Host a **separate WebView2 control** inside MeetNow that navigates to `teams.microsoft.com`. Since MeetNow owns this WebView2 instance, it has full programmatic access to:
- **Network interception** — capture Teams' own API responses as structured JSON
- **JS evaluation** — read React state, replay API calls for status changes
- **No Teams restart required** — the real Teams desktop app runs untouched

Data from WebView2 is merged with existing LevelDB sources and deduplicated before consumption.

## Constraints

- No admin privileges
- No registry changes
- No Microsoft Graph API
- No external API dependencies
- No certificates or code signing
- Must be IT-invisible (Shell corporate environment)
- WebView2 Runtime pre-installed on Windows 11 (no additional install)

## Architecture

### Component Overview

```
+------------------+     +---------------------------+     +----------------------+
| OutlookCacheReader|     | TeamsWebViewDataExtractor |     | TeamsMessageMonitor  |
| (LevelDB)        |     | (Network + JS eval)       |     | (LevelDB)            |
+--------+---------+     +------------+--------------+     +----------+-----------+
         |                             |                               |
         +-----------------------------+-------------------------------+
                                       |
                              +--------v---------+
                              | MeetingDataAggregator |
                              | (dedup & merge)       |
                              +--------+---------+
                                       |
                              +--------v---------+
                              | MainWindow /       |
                              | Popup scheduling   |
                              +--------------------+
```

### New Files

| File | Purpose |
|------|---------|
| `TeamsWebViewWindow.xaml` / `.xaml.cs` | WPF window hosting WebView2 control |
| `TeamsWebViewDataExtractor.cs` | Network interception, JS evaluation, data extraction |
| `MeetingDataAggregator.cs` | Multi-source dedup and merging for meetings and messages |

### Modified Files

| File | Change |
|------|--------|
| `MainWindow.xaml.cs` | Launch WebView2 window, add tray menu toggle, refactor `RefreshOutlook` to use aggregator |
| `SettingsWindow.xaml` / `.xaml.cs` | Add "Show Teams WebView" checkbox |
| `MeetNow.csproj` | Add `Microsoft.Web.WebView2` NuGet reference |

### Unchanged

- Existing LevelDB readers (continue working as fallback/parallel source)
- Urgency classification pipeline (`LocalUrgencyClassifier`, `ContactPriorityProvider`)
- Popup windows and audio system
- `TeamsStatusManager.cs` (kept as fallback)

## Detailed Design

### 1. WebView2 Window & Lifecycle

**`TeamsWebViewWindow.xaml`** — A WPF Window hosting a `WebView2` control filling the entire client area.

- Title: "MeetNow — Teams WebView"
- Default size: ~1200x800, resizable
- Visible by default (`Settings.ShowTeamsWebView = true`), toggled via tray context menu item "Teams WebView"
- Window close = hide (not dispose), re-shown via tray menu
- Disposed on app exit

**Initialization sequence:**
1. Created on app startup (after MainWindow init)
2. `CoreWebView2Environment.CreateAsync()` with user data folder `%LOCALAPPDATA%\MeetNow\WebView2Profile`
3. `EnsureCoreWebView2Async(environment)`
4. Navigate to `https://teams.microsoft.com`
5. First run: user sees Teams login page, logs in manually — cookies persist in the profile folder
6. Subsequent runs: auto-authenticated via persisted cookies

### 2. Network Interception

**Part of `TeamsWebViewDataExtractor.cs`**

Subscribes to `CoreWebView2.WebResourceResponseReceived` to capture Teams API responses.

**Filtered URL patterns:**
- Calendar/meetings: URLs containing `/api/calendar/`, `/me/calendarview`, or similar Teams middleware endpoints
- Messages/chat: URLs containing `/api/chatsvc/`, `/messages`, `/threads`
- Presence/status: URLs containing `/presence/`, `/status`

**For matching requests:**
1. Read response body via `GetContentAsync()`
2. Parse JSON
3. Map to existing models (`TeamsMeeting`, `TeamsMessage`)
4. Push to in-memory buffer / raise event

**Discovery mode (`LogAllTraffic = true`, default during POC):**
- Logs every request URL + response status + content-type to `%TEMP%\MeetNow_WebView_Traffic.log`
- Enables mapping out the Teams API surface before narrowing filters
- Disabled once URL patterns are finalized

### 3. JS Evaluation

**Part of `TeamsWebViewDataExtractor.cs`**

Uses `CoreWebView2.ExecuteScriptAsync()` for two purposes:

**Data extraction from React state:**
- Probe for internal state stores (`window.__REDUX_STORE__`, React fiber tree, global state objects)
- Extract cached meetings, chat threads, presence status not visible in network traffic (already-loaded data)
- Runs on a timer (every 30 seconds) after page is fully loaded
- Results parsed from JSON string returned by `ExecuteScriptAsync`

**Status automation (discover & replay):**
- **Phase 1 — Discover:** Intercept network traffic while user manually changes status in the WebView2 window. Log the exact API endpoint, method, headers, and body to the traffic log.
- **Phase 2 — Replay:** Replay those same API calls via `ExecuteScriptAsync` running `fetch()` from within the page context. This inherits cookies and auth tokens automatically. Example: `await fetch('/api/presence/...', { method: 'PUT', body: '{"availability":"Available"}', headers: {...} })`
- **Fallback:** If replay doesn't work (CSRF tokens, etc.), fall back to DOM manipulation — find the status picker elements and click through them via JS.

**Safety:**
- All JS snippets stored as const strings in the class (not loaded from external files)
- All JS wrapped in try/catch to prevent WebView2 crashes

### 4. Deduplication & Data Merging

**`MeetingDataAggregator.cs`**

Aggregates data from all sources and deduplicates.

**Meetings — three potential sources:**
1. `OutlookCacheReader` (LevelDB from New Outlook)
2. `TeamsWebViewDataExtractor` (network interception + JS evaluation)
3. `OutlookHelper` (Classic Outlook COM, optional)

**Meeting dedup key:** Normalized subject (trim + lowercase) + start time (rounded to nearest minute) — same approach `OutlookCacheReader` already uses.

**Merge strategy:** When duplicates found, prefer the source with richest data (e.g. Teams URL present > absent, has organizer > no organizer). Merge fields across sources where one has data the other lacks.

**Messages:**
- Key on `clientmessageid` (already used in `TeamsMessageMonitor`)
- WebView2 messages deduplicated against LevelDB messages
- Same urgency classification pipeline runs regardless of source

**Presence/status:**
- Single source (WebView2 only), no dedup needed
- Write-only from MeetNow's perspective (setting status)

**Integration:**
- `MainWindow.RefreshOutlook()` refactored to call `MeetingDataAggregator.GetMeetings()` which fans out to all sources and returns deduplicated results
- Same pattern for messages: aggregator wraps both `TeamsMessageMonitor` and `TeamsWebViewDataExtractor`

### 5. New Dependencies

**NuGet:**
- `Microsoft.Web.WebView2` (latest stable) — WPF WebView2 control + CoreWebView2 APIs

**Runtime requirement:**
- WebView2 Runtime — pre-installed on Windows 11, no action needed

### 6. Settings

| Setting | Type | Default | Purpose |
|---------|------|---------|---------|
| `ShowTeamsWebView` | bool | `true` | Show/hide the WebView2 window |
| `LogAllWebViewTraffic` | bool | `true` | POC discovery mode — log all network traffic |

## Known Risks & Mitigations

### Teams web may block embedded access (HIGH — potential showstopper)
Teams web (`teams.microsoft.com`) may detect the embedded WebView2 and:
- Show a "Use the desktop app" interstitial blocking access
- Redirect to a download page based on user-agent detection
- Corporate Azure AD Conditional Access Policies might block sign-in from a non-standard browser context

**Mitigation:** Set `CoreWebView2Settings.UserAgent` to match Edge's user-agent string. If Azure AD blocks login, this is a showstopper that the POC will surface early — validating this is the POC's first priority.

### WebView2 profile visibility to IT
The user data folder at `%LOCALAPPDATA%\MeetNow\WebView2Profile` will accumulate Chromium cache, cookies, and browser artifacts. In Shell's monitored environment this could attract attention.

**Mitigation:** Set `CoreWebView2EnvironmentOptions` to limit disk usage where possible. For the POC, this is acceptable — optimize later if needed.

### Response body availability in `WebResourceResponseReceived`
Per WebView2 docs, `GetContentAsync()` may return `null` for responses where the body has already been consumed or for streaming responses. Not all intercepted responses will have readable bodies.

**Mitigation:** Use `AddWebResourceRequestedFilter()` for critical URL patterns where body access is essential. Log cases where body is null to understand the scope of the limitation during POC discovery.

### Memory impact of dual Teams instances
Teams web is a heavyweight React app. Running it alongside the Teams desktop app means two full instances in memory.

**Mitigation:** POC accepts this trade-off. Future: lazy initialization, ability to stop/restart the WebView2 session on demand.

### JS evaluation targets are speculative
Teams web does not expose a standard `window.__REDUX_STORE__`. React state access patterns are version-dependent and undocumented. The POC's JS evaluation section is exploratory — the primary goal is validating what global state (if any) is accessible.

### Status replay auth may be incomplete
`fetch()` from page context inherits cookies but Teams may use bearer tokens in memory, rotating anti-forgery tokens, or WebSocket-based communication. Discovering the exact auth mechanism is part of Phase 1; Phase 2 replay may need to replicate token injection patterns observed during discovery.

## What This POC Does NOT Cover

- Finalizing which Teams API endpoints to intercept (POC discovers these)
- Polished UI for the WebView2 window (POC is developer-facing)
- Replacing LevelDB readers (they remain as parallel sources)
- Performance optimization of deduplication
- Error recovery if Teams web session expires (manual re-login for now)
- WebView2 profile disk usage management
- Push/pull timing model between real-time WebView2 events and 15-minute LevelDB polling (POC uses simple merge-on-demand)
