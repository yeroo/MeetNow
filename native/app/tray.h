#pragma once
#include "calendar.h"
#include <windows.h>
#include <vector>

namespace mn {

// Shell_NotifyIcon tray icon + the right-click menu, replacing the
// Hardcodet TaskbarIcon from MainWindow.xaml. The menu is rebuilt on every
// open from the current meeting list (the C# app rebuilt it on every
// 15-minute refresh; building at click time is strictly fresher).

bool trayAdd(HWND owner, UINT callbackMsg);
void trayRemove(HWND owner);
// After Explorer restarts (TaskbarCreated broadcast) the icon must be re-added.
void trayReAdd(HWND owner, UINT callbackMsg);

enum class TrayAction { None, Exit, SignIn, Join };

struct TrayMenuResult {
    TrayAction action = TrayAction::None;
    std::wstring joinUrl;
};

// Shows the menu at the cursor: today's remaining meetings as
// "HH:mm: Subject" (mirrors MainWindow.RefreshOutlook), then Sign in…
// when sign-in is required, then Exit. Blocks in TrackPopupMenu.
TrayMenuResult showTrayMenu(HWND owner, const std::vector<Meeting>& meetings, bool offerSignIn);

} // namespace mn
