# Contact Database with Auto-Discovery and Manual Lookup

## Problem

MeetNow only knows chat contacts by display name (extracted from Teams LevelDB). When someone chats with you, there's no email address, Teams user ID, or other contact information available. This makes it impossible to reliably:
- Forward urgent messages to the right email
- Look up who sent a message
- Target specific users for chat actions

## Solution

Build a persistent contact database that:
1. **Auto-discovers** contacts from WebView2 chat traffic (profile picture URLs, Trouter WebSocket messages)
2. **Enriches** stub contacts with full profile data (email, title, department) via Teams/Outlook/Graph APIs
3. **Provides manual lookup** via a "Find Person" tray menu item with search dialog

## Constraints

- Same constraints as the WebView2 POC: no admin, no registry, no Graph API registration, IT-invisible
- Graph API access only through tokens acquired from within the WebView2 page context (no direct app registration)
- Storage follows existing pattern: JSON file in `%LOCALAPPDATA%\MeetNow\`

## Detailed Design

### 1. Contact Model & Storage

**`ContactDatabase.cs`**

Contact model fields:
- `TeamsUserId` (string, primary key — format: `8:orgid:xxxx-xxxx-xxxx`)
- `DisplayName` (string)
- `Email` (string, nullable)
- `JobTitle` (string, nullable)
- `Department` (string, nullable)
- `Phone` (string, nullable)
- `ProfilePictureUrl` (string, nullable)
- `LastSeenTimestamp` (DateTime)
- `IsPinned` (bool — manually added via Find Person)
- `Source` (enum: Chat, Search, Manual)
- `LastUpdated` (DateTime)
- `EnrichmentStatus` (enum: Pending, Enriched, Failed — shown in Find Person UI so user knows which contacts have complete data)

**Storage:** `%LOCALAPPDATA%\MeetNow\contacts.json`
- JSON array, deserialized into `ConcurrentDictionary<string, Contact>` keyed by TeamsUserId (thread-safe: auto-discovery writes from UI thread, enrichment writes from background)
- Loaded into memory on startup
- Saved on changes, debounced (max once per 30 seconds via `System.Threading.Timer`)
- On app exit, force immediate flush of pending changes

**Public API (static class: `ContactDatabase`, matching `ContactPriorityProvider` pattern):**
- `GetByName(string displayName)` → List&lt;Contact&gt; (matches: case-insensitive, minimum 3 chars, matches on word boundaries — "Agar" matches "Agarwal, Sneha" but "Ag" returns empty)
- `GetByEmail(string email)` → Contact? (exact match, case-insensitive)
- `GetById(string teamsUserId)` → Contact? (exact match)
- `Upsert(Contact)` — insert or update, triggers debounced save (thread-safe)
- `GetPinned()` → List of pinned contacts
- `SetPinned(string teamsUserId, bool pinned)`
- `GetAll()` → All contacts
- `Prune()` — remove non-pinned contacts not seen in 90 days (called on startup)

### 2. Auto-Discovery from Chat Traffic

**Two data sources already flowing through the WebView2 interceptor:**

**Source 1: Profile picture URLs**
The network interceptor captures requests matching:
```
/api/mt/.../beta/users/.../profilepicturev2/8:orgid:GUID?displayname=Name
/api/mt/.../beta/users/.../mergedProfilePicturev2?usersInfo=[{userId, displayName}]
```
These provide TeamsUserId + DisplayName pairs.

**Source 2: Trouter WebSocket messages**
When someone sends a message, Trouter delivers sender identity in the frame payload. The exact payload format is not yet fully mapped — the POC captured Trouter frames with `name` fields like `trouter.connected`, `trouter.message_loss`, and control frames. Actual chat message notifications (which would contain sender MRI/display name) will appear when a real message arrives while the WebView2 is running. Implementation should log and parse any Trouter frame containing a sender identifier (`8:orgid:` prefix) and extract it.

**Auto-discovery flow:**
1. `TeamsWebViewDataExtractor.OnResponseReceived` parses profile picture URLs for userId + displayName
2. Trouter message harvesting extracts sender IDs from chat notification frames (discovery during implementation — log all frames with `8:orgid:` to map the payload structure)
3. For each new TeamsUserId, call `ContactDatabase.Upsert()` with the stub contact
4. Queue the stub for background enrichment

### 3. Contact Enrichment

**`ContactEnricher.cs`**

Resolves TeamsUserId → full profile (email, title, department, phone).

**Takes** a `TeamsWebViewDataExtractor` reference (uses its existing `EvaluateJsAsync()` method — consistent with the extractor pattern, avoids raw CoreWebView2 dependency, and safely handles WebView not being ready).

**`EnrichAsync(Contact contact)`** tries APIs in order:
1. **Teams People API:** `fetch('/api/mt/{region}/beta/users/{userId}/profile')` via `EvaluateJsAsync` — most likely to work, same-origin. The `{region}` path segment (e.g. `part/emea-02`) must be discovered from captured traffic at startup (already visible in the traffic log).
2. **Graph API:** During the Outlook calendar navigation phase (which already happens for calendar discovery), use OWA's `GetAccessTokenforResource` to acquire a Graph token, then call `graph.microsoft.com/v1.0/users/{id}` — provides the richest data (email, jobTitle, department, officeLocation). This path is only available during the brief Outlook navigation window.

Note: The OWA People API path (originally proposed) is dropped — it's only accessible when the WebView is on an Outlook page, which is a brief navigation for calendar discovery, not a persistent state. The Teams People API (option 1) is the primary enrichment path.

**Execution model:**
- Background queue using `ConcurrentQueue<string>` (TeamsUserIds to enrich)
- Processes on a `DispatcherTimer` (UI thread, since `EvaluateJsAsync` requires it)
- Rate-limited: max 1 enrichment call per 2 seconds
- Skips contacts already enriched in the last 24 hours (`LastUpdated` + `Email != null`)
- If WebView is not ready (`EvaluateJsAsync` returns null), skips and retries on next timer tick
- Queue is in-memory only — not persisted. Stubs will be re-queued on next app start when auto-discovery re-encounters the contact.
- Results stored via `ContactDatabase.Upsert()`

### 4. Find Person UI

**New tray menu item:** "Find Person" in MainWindow's context menu.

**`FindPersonWindow.xaml` / `.xaml.cs`**

Dark theme dialog consistent with SettingsWindow (`Background="#1F1F1F"`, `Foreground="#DDD"`):
- Search text box at the top
- Debounced search (300ms after typing stops)
- Results list below: each row shows Name, Email (if known), Title (if known)
- "Pin" button on each row — sets `IsPinned = true` in database
- Already-pinned contacts show a filled indicator

**Search mechanism:**
1. First searches local `ContactDatabase` (instant, offline) — fuzzy match on DisplayName and Email
2. If fewer than 5 local results, fires remote search via WebView2 JS eval (Teams people search API — exact URL path includes a region segment like `part/emea-02` discovered from captured traffic)
3. Remote results merged with local, deduplicated by TeamsUserId
4. Remote results auto-added to database and queued for enrichment

**Window behavior:**
- Stays on top but doesn't block the app
- Close = hide (re-shown from tray menu)
- Reusable across sessions

### 5. New Files

| File | Purpose |
|------|---------|
| `ContactDatabase.cs` | Contact model, JSON storage, singleton, lookup API |
| `ContactEnricher.cs` | Background enrichment pipeline via WebView2 JS eval |
| `FindPersonWindow.xaml` / `.xaml.cs` | Search dialog with pin support |

### 6. Modified Files

| File | Change |
|------|--------|
| `MainWindow.xaml` | Add "Find Person" tray menu item |
| `MainWindow.xaml.cs` | Click handler for Find Person, initialize ContactDatabase on startup |
| `TeamsWebViewDataExtractor.cs` | Parse profile picture URLs and Trouter messages for auto-discovery |

### 7. Unchanged

- `ContactPriorityProvider` — continues working with display names (integration deferred to chat migration sub-project)
- `FavoriteContactsProvider` — continues reading from LevelDB
- All popup/audio/scheduling code
- `TeamsStatusManager` — keyboard simulation kept as fallback

## What This Spec Does NOT Cover

- Migrating chat actions (typing, reply, forward) to WebView2 — separate sub-project
- Migrating status automation to WebView2 — separate sub-project
- Migrating calendar data extraction to WebView2 — separate sub-project (POC proven)
- Replacing ContactPriorityProvider with ContactDatabase — deferred to chat migration
- Profile picture caching/display
