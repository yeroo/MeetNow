#pragma once
#include "calendar.h"
#include "overlay.h"
#include "settings.h"
#include <windows.h>
#include <vector>

namespace mn {

// The hidden main window: owns the tray icon, the overlay, the three
// timers (calendar refresh / overlay repaint / keep-awake) and the
// background refresh + sign-in threads. Replaces MainWindow.xaml.cs's
// hidden WPF window and App.xaml.cs's lifecycle.
struct App {
    HWND hwnd = nullptr;
    Settings settings;
    std::vector<Meeting> meetings;
    Overlay overlay;
    bool signInNeeded = false;        // tray menu offers "Sign in…"
    bool refreshInFlight = false;
    bool authInFlight = false;
    bool autoSignInAttempted = false; // at most one unprompted browser sign-in per session
    UINT taskbarCreatedMsg = 0;       // Explorer-restart broadcast, to re-add the tray icon
};

App* createAppWindow(HINSTANCE inst);
int runMessageLoop();

} // namespace mn
