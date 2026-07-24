#include "app_window.h"
#include "auth.h"
#include "keep_awake.h"
#include "tray.h"
#include <objbase.h>
#include <shellapi.h>

namespace mn {

namespace {

constexpr wchar_t kMainClass[] = L"MeetNowMain";

constexpr UINT WM_APP_TRAY = WM_APP + 1;
constexpr UINT WM_APP_REFRESHED = WM_APP + 2;  // lParam: heap RefreshResult*
constexpr UINT WM_APP_AUTH_DONE = WM_APP + 3;  // wParam: success
constexpr UINT WM_APP_GRIP_MOVED = WM_APP + 4; // wParam: kGripOverlay/kGripPopup
constexpr UINT WM_APP_OVERLAY_DISMISS = WM_APP + 5;  // wParam: strip row index

constexpr WPARAM kGripOverlay = 0;
constexpr WPARAM kGripPopup = 1;

// Intervals from the C# app: OUTLOOK_TIMER_INTERVAL_MINUTES = 15, overlay
// query/render timers 30 s, ScreenLockPrevention timer 60 s.
constexpr UINT_PTR kRefreshTimer = 1;
constexpr UINT kRefreshIntervalMs = 15 * 60 * 1000;
constexpr UINT_PTR kOverlayTimer = 2;
constexpr UINT kOverlayIntervalMs = 30 * 1000;
constexpr UINT_PTR kKeepAwakeTimer = 3;
constexpr UINT kKeepAwakeIntervalMs = 60 * 1000;
// 1 s so the join popup appears at the meeting's start time, not up to
// 30 s late like the overlay tick would allow (SchedulePopup in the C#
// app fired to the second via FluentScheduler).
constexpr UINT_PTR kPopupTimer = 4;
constexpr UINT kPopupIntervalMs = 1000;
// The popup covers [start, start+5 min): shown when start passes, auto-
// closed 5 min in — SchedulePopup / SchedulePopupClose(start.AddMinutes(5)).
constexpr unsigned long long kPopupWindowTicks = 5 * kTicksPerMinute;
// Cursor polling for the drag grips: the overlay is click-through, so
// hover can't arrive as a mouse message — it has to be polled.
constexpr UINT_PTR kHoverTimer = 5;
constexpr UINT kHoverIntervalMs = 150;

struct ThreadArgs {
    HWND hwnd;
    Settings settings;
};

DWORD WINAPI refreshThread(void* param) {
    ThreadArgs* args = (ThreadArgs*)param;
    auto* result = new RefreshResult(refreshCalendar(args->settings));
    if (!PostMessageW(args->hwnd, WM_APP_REFRESHED, 0, (LPARAM)result)) delete result;
    delete args;
    return 0;
}

DWORD WINAPI authThread(void* param) {
    ThreadArgs* args = (ThreadArgs*)param;
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
    const TokenSet t = acquireTokenInteractive(args->settings, nullptr);
    PostMessageW(args->hwnd, WM_APP_AUTH_DONE, t.empty() ? 0 : 1, 0);
    CoUninitialize();
    delete args;
    return 0;
}

void startThread(App* app, LPTHREAD_START_ROUTINE proc) {
    HANDLE h = CreateThread(nullptr, 0, proc, new ThreadArgs{ app->hwnd, app->settings }, 0, nullptr);
    if (h) CloseHandle(h);
}

void startRefresh(App* app) {
    if (app->refreshInFlight || app->authInFlight) return;
    app->refreshInFlight = true;
    startThread(app, refreshThread);
}

void startSignIn(App* app) {
    if (app->authInFlight) return;
    app->authInFlight = true;
    startThread(app, authThread);
}

// The badge's meeting list: everything except rows the user discarded
// with the ✕ strip. Dismissals for meetings no longer in the calendar
// window are pruned so the list can't grow across the day.
std::vector<Meeting> overlayMeetings(App* app) {
    std::erase_if(app->overlayDismissed, [app](const auto& d) {
        for (const auto& m : app->meetings)
            if (m.startUtc == d.first && m.subject == d.second) return false;
        return true;
    });
    std::vector<Meeting> out;
    for (const auto& m : app->meetings) {
        bool dismissed = false;
        for (const auto& d : app->overlayDismissed)
            if (m.startUtc == d.first && m.subject == d.second) dismissed = true;
        if (!dismissed) out.push_back(m);
    }
    return out;
}

void onRefreshed(App* app, RefreshResult* result) {
    app->refreshInFlight = false;
    switch (result->status) {
    case RefreshStatus::Ok:
        app->signInNeeded = false;
        app->meetings = std::move(result->meetings);
        app->overlay.update(overlayMeetings(app));
        break;
    case RefreshStatus::AuthRequired:
        app->signInNeeded = true;
        // First run (or a revoked grant): open the browser sign-in once
        // without being asked — the tray-menu "Sign in…" covers retries.
        if (!app->autoSignInAttempted) {
            app->autoSignInAttempted = true;
            startSignIn(app);
        }
        break;
    case RefreshStatus::Error:
        break;  // keep showing the last good data; next timer tick retries
    }
    delete result;
}

void popupTick(App* app) {
    const unsigned long long now = nowUtc();
    app->popup.tick(now);  // auto-close at start + 5 min

    // Meetings inside their popup window that haven't been popped yet.
    std::vector<Meeting> due;
    unsigned long long earliestStart = 0;
    for (const auto& m : app->meetings) {
        if (m.startUtc > now || now >= m.startUtc + kPopupWindowTicks || m.endUtc <= now)
            continue;
        bool shown = false;
        for (const auto s : app->popupShownStarts) shown = shown || s == m.startUtc;
        if (shown) continue;
        due.push_back(m);
        if (earliestStart == 0 || m.startUtc < earliestStart) earliestStart = m.startUtc;
    }
    if (due.empty()) return;

    for (const auto& m : due) app->popupShownStarts.push_back(m.startUtc);
    // Drop marks older than a day so the list can't grow unbounded.
    std::erase_if(app->popupShownStarts,
                  [now](unsigned long long s) { return s + kTicksPerDay < now; });
    app->popup.show(due, earliestStart + kPopupWindowTicks);
}

// One host window's share of the hover tick: show its grip while the
// cursor is over the host or the grip itself, hide it otherwise.
void hoverFor(HWND host, bool hostVisible, const RECT& hostRect, DragGrip& grip,
              const POINT& cursor) {
    if (grip.dragging()) return;
    if (!hostVisible) {
        grip.hide();
        return;
    }
    bool over = PtInRect(&hostRect, cursor) != 0;
    RECT gr{};
    if (!over && grip.getRect(&gr)) over = PtInRect(&gr, cursor) != 0;
    if (over)
        grip.showFor(host);  // no-op while already aligned to hostRect
    else
        grip.hide();
}

void hoverTick(App* app) {
    POINT cursor{};
    GetCursorPos(&cursor);

    // Overlay: hover shows the ✕-per-row dismiss strip flush left of the
    // badge, and the drag grip left of that.
    RECT r{};
    if (app->overlayGrip.dragging()) {
        app->dismissStrip.hide();  // realigned by the next tick after the drop
    } else if (!app->overlay.getRect(&r)) {
        app->dismissStrip.hide();
        app->overlayGrip.hide();
    } else {
        bool over = PtInRect(&r, cursor) != 0;
        RECT sr{};
        if (!over && app->dismissStrip.getRect(&sr)) over = PtInRect(&sr, cursor) != 0;
        if (!over && app->overlayGrip.getRect(&sr)) over = PtInRect(&sr, cursor) != 0;
        if (over) {
            app->dismissStrip.showFor(app->overlay.handle(), app->overlay.rows());
            app->overlayGrip.showFor(app->overlay.handle(), app->dismissStrip.widthPx());
        } else {
            app->dismissStrip.hide();
            app->overlayGrip.hide();
        }
    }

    bool vis = app->popup.getRect(&r);
    hoverFor(app->popup.handle(), vis, r, app->popupGrip, cursor);
}

// An ✕ was clicked: discard that meeting from the badge for this session.
void onOverlayDismiss(App* app, size_t rowIndex) {
    unsigned long long startUtc = 0;
    std::wstring subject;
    if (!app->dismissStrip.rowKey(rowIndex, &startUtc, &subject)) return;
    app->overlayDismissed.emplace_back(startUtc, subject);
    app->dismissStrip.hide();  // rows changed; hover tick realigns everything
    app->overlayGrip.hide();
    app->overlay.update(overlayMeetings(app));
}

// A grip drag ended: re-anchor the host to its dragged corner and persist.
void onGripMoved(App* app, WPARAM which) {
    RECT r{};
    if (which == kGripOverlay && app->overlay.getRect(&r)) {
        app->layout.hasOverlay = true;
        app->layout.overlayTopRight = { r.right, r.top };
        app->overlay.setAnchor(app->layout.overlayTopRight);
    } else if (which == kGripPopup && app->popup.getRect(&r)) {
        app->layout.hasPopup = true;
        app->layout.popupBottomRight = { r.right, r.bottom };
        app->popup.setAnchor(app->layout.popupBottomRight);
    } else {
        return;
    }
    saveLayout(app->layout);
}

void onTray(App* app, LPARAM lParam) {
    // NOTIFYICON_VERSION_4 packs the event in LOWORD(lParam).
    const UINT event = LOWORD(lParam);
    if (event != WM_CONTEXTMENU && event != WM_RBUTTONUP) return;

    const TrayMenuResult r = showTrayMenu(app->hwnd, app->meetings, app->signInNeeded);
    switch (r.action) {
    case TrayAction::Exit:
        DestroyWindow(app->hwnd);
        break;
    case TrayAction::SignIn:
        startSignIn(app);
        break;
    case TrayAction::Join:
        // OutlookHelper.StartTeamsMeeting: ShellExecute the URL, https only.
        if (r.joinUrl.rfind(L"https://", 0) == 0)
            ShellExecuteW(nullptr, L"open", r.joinUrl.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
        break;
    case TrayAction::None:
        break;
    }
}

LRESULT CALLBACK mainProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    App* app = (App*)GetWindowLongPtrW(hwnd, GWLP_USERDATA);
    if (app) {
        switch (msg) {
        case WM_TIMER:
            if (wParam == kRefreshTimer) startRefresh(app);
            // A repaint or auto-close would yank the window mid-drag, so
            // both stand down while their grip is captured.
            else if (wParam == kOverlayTimer && !app->overlayGrip.dragging())
                app->overlay.update(overlayMeetings(app));
            else if (wParam == kKeepAwakeTimer) keepAwakeTick();
            else if (wParam == kPopupTimer && !app->popupGrip.dragging()) popupTick(app);
            else if (wParam == kHoverTimer) hoverTick(app);
            return 0;
        case WM_APP_TRAY:
            onTray(app, lParam);
            return 0;
        case WM_APP_REFRESHED:
            onRefreshed(app, (RefreshResult*)lParam);
            return 0;
        case WM_APP_GRIP_MOVED:
            onGripMoved(app, wParam);
            return 0;
        case WM_APP_OVERLAY_DISMISS:
            onOverlayDismiss(app, (size_t)wParam);
            return 0;
        case WM_APP_AUTH_DONE:
            app->authInFlight = false;
            if (wParam) {
                app->signInNeeded = false;
                startRefresh(app);
            }
            return 0;
        case WM_POWERBROADCAST:
            // Refresh right after wake, like the PowerModeChanged.Resume
            // handler resetting the timer to fire immediately.
            if (wParam == PBT_APMRESUMEAUTOMATIC || wParam == PBT_APMRESUMESUSPEND)
                startRefresh(app);
            return TRUE;
        case WM_DESTROY:
            trayRemove(hwnd);
            keepAwakeStop();
            app->overlayGrip.destroy();
            app->popupGrip.destroy();
            app->dismissStrip.destroy();
            app->overlay.destroy();
            app->popup.destroy();
            PostQuitMessage(0);
            return 0;
        default:
            if (msg == app->taskbarCreatedMsg && app->taskbarCreatedMsg != 0) {
                trayReAdd(hwnd, WM_APP_TRAY);
                return 0;
            }
        }
    }
    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

} // namespace

App* createAppWindow(HINSTANCE inst) {
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = mainProc;
    wc.hInstance = inst;
    wc.lpszClassName = kMainClass;
    if (!RegisterClassExW(&wc)) return nullptr;

    // Message-only would be simpler, but such windows never receive the
    // TaskbarCreated broadcast or WM_POWERBROADCAST — so this is a normal
    // top-level window that just never gets shown.
    HWND hwnd = CreateWindowExW(0, kMainClass, L"MeetNow", WS_OVERLAPPED, 0, 0, 0, 0,
                                nullptr, nullptr, inst, nullptr);
    if (!hwnd) return nullptr;

    App* app = new App();
    app->hwnd = hwnd;
    app->settings = loadSettings();
    app->taskbarCreatedMsg = RegisterWindowMessageW(L"TaskbarCreated");
    SetWindowLongPtrW(hwnd, GWLP_USERDATA, (LONG_PTR)app);

    // Offline-first startup like lookxy: last fetched window from disk, so
    // the menu and overlay are populated before the first Graph roundtrip.
    app->meetings = loadCalendarCache();

    if (!app->overlay.init(inst)) {
        // Overlay loss is not fatal — tray + keep-awake still work.
    }
    if (!app->popup.init(inst)) {
        // Same stance: no popup is a degraded mode, not a startup failure.
    }
    app->overlayGrip.init(inst, hwnd, WM_APP_GRIP_MOVED, kGripOverlay);
    app->popupGrip.init(inst, hwnd, WM_APP_GRIP_MOVED, kGripPopup);
    app->dismissStrip.init(inst, hwnd, WM_APP_OVERLAY_DISMISS);

    // Dragged positions from the last session; an anchor whose corner no
    // longer lands on any monitor (undocked screen) falls back to default.
    app->layout = loadLayout();
    const auto onScreen = [](POINT p) {
        return MonitorFromPoint(p, MONITOR_DEFAULTTONULL) != nullptr;
    };
    if (app->layout.hasOverlay && onScreen(app->layout.overlayTopRight))
        app->overlay.setAnchor(app->layout.overlayTopRight);
    if (app->layout.hasPopup && onScreen(app->layout.popupBottomRight))
        app->popup.setAnchor(app->layout.popupBottomRight);

    trayAdd(hwnd, WM_APP_TRAY);
    keepAwakeStart();
    app->overlay.update(overlayMeetings(app));

    SetTimer(hwnd, kRefreshTimer, kRefreshIntervalMs, nullptr);
    SetTimer(hwnd, kOverlayTimer, kOverlayIntervalMs, nullptr);
    SetTimer(hwnd, kKeepAwakeTimer, kKeepAwakeIntervalMs, nullptr);
    SetTimer(hwnd, kPopupTimer, kPopupIntervalMs, nullptr);
    SetTimer(hwnd, kHoverTimer, kHoverIntervalMs, nullptr);
    // Starting the app inside a meeting's popup window pops immediately,
    // like FluentScheduler firing an overdue ToRunOnceAt job on schedule.
    popupTick(app);
    startRefresh(app);  // C# timer's first tick was immediate

    return app;
}

int runMessageLoop() {
    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    return (int)msg.wParam;
}

} // namespace mn
