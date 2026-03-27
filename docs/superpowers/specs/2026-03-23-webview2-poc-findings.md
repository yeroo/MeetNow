# WebView2 POC Discovery Findings

**Date:** 2026-03-23
**Status:** POC validated — WebView2 approach works

## Authentication & Access

- Edge user-agent spoofing **worked** — no "use the desktop app" interstitial
- Azure AD Conditional Access **did not block** sign-in
- Cookies persist in `%LOCALAPPDATA%\MeetNow\WebView2Profile` — auto-login on restart
- Teams web fully functional in embedded WebView2 (chats, calendar, calls UI visible)

## Network Interception Results

### REST API Endpoints Discovered

Only ~16 unique JSON API calls observed after login. Teams uses very few REST calls:

| Endpoint | Data |
|----------|------|
| `/api/mt/.../beta/users/tenantsv2` | Tenant info (Shell, tenant ID, user ID) |
| `/api/chatsvc/emea/v1/users/ME/properties` | Chat service user properties (13KB, includes pinned apps) |
| `/api/mt/.../beta/me/settings/meetingConfiguration` | Meeting config, QoS settings, Shell legal text |
| `/api/mt/.../beta/usersettings/useEndToEndEncryption` | E2E encryption flag |
| `/api/mt/.../beta/atpsafelinks/getpolicy/` | ATP safe links policy |
| `/api/authsvc/v1.0/authz` | Auth tokens |
| `/api/mt/.../v1/mcps/initArtifactFolder` | File storage init |
| `/trap/tokens` | Token endpoint |
| `graph.microsoft.com/v1.0/sites/` | SharePoint site collections |
| `graph.microsoft.com/v1.0/domains/` | Domain list |
| `substrate.office.com/sigsapi/v1.0/Me/Signals` | Activity signals |

### Key Finding: Calendar is an Outlook iframe

Teams embeds the calendar as `outlook.office.com/hosted/calendar/` in an iframe — not via a Teams REST API. This means calendar data may need to be extracted from the iframe's context or by intercepting the iframe's own network requests.

### Response Body Availability

`GetContentAsync()` worked for all JSON responses tested — the spec's concern about null bodies did not materialize for REST responses.

## WebSocket Interception Results

### Connections Captured

| WebSocket URL | Purpose | Traffic |
|---------------|---------|---------|
| `wss://go-eu.trouter.teams.microsoft.com/v4/c` | Real-time notifications (chat, presence, channels) | Primary data channel |
| `wss://augloop.office.com` | Office real-time collaboration/editing sync | Text editing operations |

### Trouter Message Tags Observed

- `messaging` — chat message notifications
- `messagingsync` — message sync state
- `pinnedchannel` — pinned channel updates
- `tps` — unknown (possibly "teams presence service")

### Trouter Protocol Format

Messages use a numbered frame format: `5:N+::{JSON}` where N is a sequence number. The JSON payload contains `name` and `args` fields. Example:
```json
{"name":"trouter.message_loss","args":[{"droppedIndicators":[{"tag":"messaging","etag":"..."}]}]}
```

## Webpack Module Exploration Results

### Overview

- **3,330 total modules** in webpack cache
- **78 modules** with interesting exports (calendar, presence, chat, message, status, store, state, event, notification keywords)
- Successfully obtained `__webpack_require__` via chunk push technique

### Key Modules Found

| Module ID | Export Key | Type | Potential Use |
|-----------|-----------|------|---------------|
| 93952, 101262 | `AppStateContext` | React Context | Full app state via `_currentValue` |
| 93952, 101262 | `ClientStateContext` | React Context | Client-side state |
| 93952, 101262 | `NetworkStateContext` | React Context | Network/connectivity state |
| 138787 | `NovaEventingProvider` | Function | Teams internal event bus |
| 138787 | `useNovaEventing` | Function | Subscribe to internal events |
| 189648 | `useSyncExternalStore` | Function | React external store subscription |
| 241219 | `postMessageConfig` | Object | iframe communication config |

### React Context Access Pattern

All context objects have `_currentValue` property — this is React's internal field that holds the current context value. Reading `AppStateContext._currentValue` may expose the full Teams app state including:
- Current user info and presence
- Calendar events
- Chat threads and messages
- Notification state

## Next Steps

### High Priority
1. **Read React Context values** — `AppStateContext._currentValue` and `ClientStateContext._currentValue` likely contain calendar, presence, and chat data
2. **Parse Trouter WebSocket messages** — Filter for `messaging` tag messages to extract real-time chat notifications
3. **Explore NovaEventing** — Subscribe to Teams' internal event bus for calendar/presence/message events

### Medium Priority
4. **Status change discovery** — Manually change status in WebView2, capture the Trouter/REST call, then replay it
5. **Calendar iframe interception** — Access `outlook.office.com/hosted/calendar/` iframe's content or intercept its network requests
6. **Deep webpack module scan** — Enumerate all 78 interesting modules' exports to find service objects with callable methods

### Validated Assumptions
- WebView2 embedding works in Shell corporate environment
- User-agent spoofing bypasses embedded browser detection
- WebSocket interception via monkey-patching works
- Webpack module cache is accessible and contains useful state
- Dual Teams instances (desktop + WebView2) coexist without issues
