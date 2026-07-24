#pragma once
#include "calendar.h"
#include <windows.h>
#include <vector>

namespace mn {

// Bottom-right join popup — the native port of PopupEventsWindow.cs /
// MeetingPanelWindow: appears at a meeting's start time just above the
// system tray with a Join button per meeting and a Dismiss button, and
// auto-closes 5 minutes after the start it was shown for. Unlike the
// countdown overlay this window takes mouse clicks (but never focus).
class Popup {
public:
    bool init(HINSTANCE inst);

    // Replaces any visible popup with one listing `meetings` (rows in the
    // given order). autoCloseUtc: tick() hides the popup at that time.
    void show(const std::vector<Meeting>& meetings, unsigned long long autoCloseUtc);

    void tick(unsigned long long now);  // auto-close check, call ~1/s
    bool visible() const;
    void close();
    void destroy();

    // Drag support (grip.cpp): anchored by its BOTTOM-RIGHT corner (the
    // corner nearest its default spot above the tray).
    void setAnchor(POINT bottomRight) { customPos_ = true; anchor_ = bottomRight; }
    HWND handle() const { return hwnd_; }
    bool getRect(RECT* r) const {
        return hwnd_ && IsWindowVisible(hwnd_) && GetWindowRect(hwnd_, r);
    }

private:
    struct D2d;
    struct Hit;  // clickable region: a Join row's URL or the Dismiss button

    void render();
    static LRESULT CALLBACK popupProc(HWND, UINT, WPARAM, LPARAM);
    LRESULT onMessage(HWND, UINT, WPARAM, LPARAM);
    const Hit* hitAt(LPARAM lParam) const;

    HINSTANCE inst_ = nullptr;
    HWND hwnd_ = nullptr;
    bool customPos_ = false;
    POINT anchor_{};
    D2d* d2d_ = nullptr;
    std::vector<Meeting> meetings_;
    std::vector<Hit>* hits_ = nullptr;
    unsigned long long autoCloseUtc_ = 0;
};

} // namespace mn
