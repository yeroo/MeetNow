# Native rewrite: harden, test against EPAM tenant, swap — design

Date: 2026-07-22
Branch: `feature/native-rewrite`
Status: approved (Boris, 2026-07-22)

## Goal

Make `native\x64\Release\MeetNow.exe` the running MeetNow on this machine,
showing the real EPAM calendar via Graph `/me/calendarView` with PKCE
sign-in. No new features. The C# app stays in-tree, launchable manually.

## Scope decisions (confirmed)

- **Harden what exists** — the four committed features (overlay, tray menu,
  keep-awake, Graph calendar) are the whole feature set.
- **Drop the rest** — MCP server, autopilot, meeting popups, recorder
  control stop running when the C# app is stopped. Accepted.
- **Out of scope** — Startup-folder autostart, any C# app changes.

## Build

The machine now gets VS 2026 Build Tools (18.8) with the v145 toolset,
matching the branch's original `Directory.Build.props` pin. Restore the
`v145` pin; remove the interim v143 fallback edits made when only VS 2022
was present. README build snippet must select an instance that has the
desktop C++ MSBuild platform files (`-products *` + filter), since the
Community 2022 install here is partial.

## Work items

1. **Correctness pass** over `auth.cpp`, `http.cpp`, `graph.cpp`,
   `calendar.cpp` — fix real bugs only, no restyling.
2. **Minimal diagnostics** — append-only log at
   `%LOCALAPPDATA%\MeetNow\native.log`: auth outcomes, HTTP status codes,
   refresh results, parse failures. Never tokens, never event bodies.
   Rationale: with no logging, a tenant-side failure (conditional access,
   consent policy, proxy) is indistinguishable from an empty calendar.
3. **Swap** — stop the running C# MeetNow (releases the shared
   `MeetNow_SingleInstance_B7A3F2` mutex), launch the native exe.
4. **Live test against EPAM** — first run opens Microsoft sign-in; Boris
   completes it with the EPAM account. Verify in order: token cached
   (`token.bin`), window cached (`calendar.json`), overlay shows meetings
   starting ≤2 h out with correct thresholds, tray menu lists today's
   remaining meetings, clicking one opens the Teams join URL.
5. **Fix what the tenant surfaces.** Likely candidates: consent prompt or
   admin-consent block on `Calendars.Read`, conditional access rejecting
   the Graph CLI public client (`14d82eec-204b-4c2f-b7e8-296a70dab67e`),
   corporate proxy breaking WinHTTP (may need
   `WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY`).

## Error handling stance

Already implemented, keep: failed refresh keeps last good `calendar.json`
data; signed-out state offers tray "Sign in…" instead of nagging.
Logging must not change behavior — log-and-continue only.

## Success criteria

- Native exe builds clean (W4-as-error) with v145.
- EPAM sign-in completes; overlay and tray show real meetings that match
  Outlook.
- C# app no longer running; native exe survives a 15-min refresh cycle
  and a sleep/resume without losing data.
