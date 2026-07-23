#include "tray.h"
#include "resource.h"
#include <shellapi.h>
#include <cstdio>

namespace mn {

namespace {

constexpr UINT kTrayId = 1;
constexpr UINT kCmdExit = 1;
constexpr UINT kCmdSignIn = 2;
constexpr UINT kCmdMeetingFirst = 100;

NOTIFYICONDATAW trayData(HWND owner, UINT callbackMsg) {
    NOTIFYICONDATAW nid{};
    nid.cbSize = sizeof(nid);
    nid.hWnd = owner;
    nid.uID = kTrayId;
    nid.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
    nid.uCallbackMessage = callbackMsg;
    nid.hIcon = LoadIconW(GetModuleHandleW(nullptr), MAKEINTRESOURCEW(IDI_APP));
    wcscpy_s(nid.szTip, L"MeetNow");
    return nid;
}

} // namespace

bool trayAdd(HWND owner, UINT callbackMsg) {
    NOTIFYICONDATAW nid = trayData(owner, callbackMsg);
    if (!Shell_NotifyIconW(NIM_ADD, &nid)) return false;
    nid.uVersion = NOTIFYICON_VERSION_4;
    Shell_NotifyIconW(NIM_SETVERSION, &nid);
    return true;
}

void trayRemove(HWND owner) {
    NOTIFYICONDATAW nid{};
    nid.cbSize = sizeof(nid);
    nid.hWnd = owner;
    nid.uID = kTrayId;
    Shell_NotifyIconW(NIM_DELETE, &nid);
}

void trayReAdd(HWND owner, UINT callbackMsg) {
    trayRemove(owner);
    trayAdd(owner, callbackMsg);
}

TrayMenuResult showTrayMenu(HWND owner, const std::vector<Meeting>& meetings, bool offerSignIn) {
    TrayMenuResult result;

    // Today's meetings that haven't ENDED yet: in-progress ones stay listed
    // until endUtc as a late-join/reconnect point (the C# app dropped them
    // at start, which left no way back in).
    SYSTEMTIME today = toLocal(nowUtc());
    const unsigned long long now = nowUtc();
    std::vector<const Meeting*> upcoming;
    for (const auto& m : meetings) {
        if (m.endUtc <= now) continue;
        const SYSTEMTIME local = toLocal(m.startUtc);
        if (local.wYear == today.wYear && local.wMonth == today.wMonth && local.wDay == today.wDay)
            upcoming.push_back(&m);
    }

    HMENU menu = CreatePopupMenu();
    if (!menu) return result;

    if (upcoming.empty()) {
        AppendMenuW(menu, MF_STRING | MF_GRAYED, 0, L"No more meetings today");
    } else {
        for (size_t i = 0; i < upcoming.size(); ++i) {
            const SYSTEMTIME local = toLocal(upcoming[i]->startUtc);
            // _TRUNCATE: a >500-char subject must clip, not trip the CRT
            // invalid-parameter handler.
            wchar_t label[512];
            _snwprintf_s(label, _TRUNCATE, L"%02u:%02u: %s%s", local.wHour, local.wMinute,
                         upcoming[i]->subject.empty() ? L"(no subject)"
                                                      : upcoming[i]->subject.c_str(),
                         upcoming[i]->startUtc <= now ? L"  (now)" : L"");
            // Meetings without a join URL are informational only.
            const UINT flags = MF_STRING | (upcoming[i]->joinUrl.empty() ? MF_GRAYED : 0);
            AppendMenuW(menu, flags, kCmdMeetingFirst + i, label);
        }
    }
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    if (offerSignIn) AppendMenuW(menu, MF_STRING, kCmdSignIn, L"Sign in…");
    AppendMenuW(menu, MF_STRING, kCmdExit, L"Exit");

    // The SetForegroundWindow dance is required so the menu closes when the
    // user clicks elsewhere (Raymond Chen's classic tray-menu rule).
    POINT pt{};
    GetCursorPos(&pt);
    SetForegroundWindow(owner);
    const UINT cmd = (UINT)TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY,
                                          pt.x, pt.y, 0, owner, nullptr);
    PostMessageW(owner, WM_NULL, 0, 0);
    DestroyMenu(menu);

    if (cmd == kCmdExit) {
        result.action = TrayAction::Exit;
    } else if (cmd == kCmdSignIn) {
        result.action = TrayAction::SignIn;
    } else if (cmd >= kCmdMeetingFirst && cmd < kCmdMeetingFirst + upcoming.size()) {
        result.action = TrayAction::Join;
        result.joinUrl = upcoming[cmd - kCmdMeetingFirst]->joinUrl;
    }
    return result;
}

} // namespace mn
