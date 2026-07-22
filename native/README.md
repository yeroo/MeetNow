# MeetNow native

A single dependency-free Win32 rewrite of MeetNow (same approach as the
BrowserSelect native rewrite), keeping exactly four features of the C# app:

- **Upcoming-meetings overlay** — click-through, always-on-top badge in the
  top-right corner of the primary screen listing meetings that start within
  the next 2 hours with live countdowns (red ≤ 5 min, yellow ≤ 15 min).
  Ports `MeetingCountdownOverlay.cs`, rendered with Direct2D/DirectWrite
  into a layered window.
- **Tray icon** with a right-click menu of **today's remaining meetings**
  (`HH:mm: Subject`); clicking one opens its Teams join link. Plus Exit.
- **Keep the screen awake** — `SetThreadExecutionState` re-asserted every
  minute plus a net-zero mouse jiggle (ports `ScreenLockPrevention.cs`).

Everything else from the C# app (MCP server, autopilot, recorder,
transcription, WebViews, popups) is intentionally gone.

## Calendar source

Unlike the C# app (Outlook COM / olk.exe cache scraping), the calendar is
read the way the **lookxy** client does it: `GET /me/calendarView` on
Microsoft Graph over a rolling window, with `Prefer: outlook.timezone="UTC"`
so recurring series are expanded server-side and all times arrive in UTC.
Sign-in is OAuth2 authorization-code + PKCE in the system browser with a
`http://localhost` loopback redirect, using the Microsoft Graph CLI public
client id (no app registration needed), requesting only
`Calendars.Read offline_access`. Tokens are cached DPAPI-encrypted in
`%LOCALAPPDATA%\MeetNow\token.bin`; the fetched window is cached in
`calendar.json` there, so startup is offline-first.

> Note: this deliberately deviates from the repo's old "no Graph API"
> constraint — the local-scraping sources stayed in the C# app; this
> rewrite adopts lookxy's mechanism instead.

First run: the app opens the Microsoft sign-in page once automatically;
if that is dismissed, the tray menu offers "Sign in…". Refresh runs every
15 minutes and immediately after resume from sleep. On failure the last
good data keeps being shown.

Optional `settings.json` overrides (read-only, same folder):
`GraphAuthority`, `GraphClientId`, `GraphScope`.

## Build

```powershell
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild `
    -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
& $msbuild native\MeetNow.sln /p:Configuration=Release /p:Platform=x64 /m
```

Output: `native\x64\Release\MeetNow.exe` — one statically linked exe
(~300 KB), C++20, Unicode, Windows-SDK libraries only, no exceptions, no
third-party code. The single-instance mutex is shared with the C# app so
the two versions never run at the same time.
