#pragma once
#include "calendar.h"
#include "grip.h"
#include "overlay.h"
#include "popup.h"
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
    Popup popup;
    DragGrip overlayGrip;   // hover handles that drag the two windows
    DragGrip popupGrip;
    Layout layout;          // dragged anchors, persisted in layout.json
    // Start times (UTC ticks) already popped up, so a popup that was
    // dismissed or auto-closed never reappears for the same slot.
    std::vector<unsigned long long> popupShownStarts;
    bool signInNeeded = false;        // tray menu offers "Sign in…"
    bool refreshInFlight = false;
    bool authInFlight = false;
    bool autoSignInAttempted = false; // at most one unprompted browser sign-in per session
    UINT taskbarCreatedMsg = 0;       // Explorer-restart broadcast, to re-add the tray icon
};

App* createAppWindow(HINSTANCE inst);
int runMessageLoop();

} // namespace mn
