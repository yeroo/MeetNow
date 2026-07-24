#pragma once
#include "calendar.h"
#include <windows.h>
#include <vector>

namespace mn {

// One rendered badge row, exposed so the hover dismiss strip
// (dismiss.cpp) can align an ✕ per meeting and map clicks back.
struct OverlayRow {
    float top = 0, height = 0;        // client-relative pixels
    unsigned long long startUtc = 0;  // together with subject: the meeting key
    std::wstring subject;
};

// The "upcoming meetings badge": a click-through, always-on-top layered
// window in the top-right corner of the primary work area, one row per
// meeting starting within the next 2 hours, with a live countdown. Ports
// MeetingCountdownOverlay.cs; rendering is Direct2D/DirectWrite into a
// premultiplied DIB pushed via UpdateLayeredWindow instead of WPF.
class Overlay {
public:
    bool init(HINSTANCE inst);

    // Filters `meetings` to the 2-hour window and repaints; hides the
    // window when nothing qualifies (the C# code closed it — same visual
    // result). Call every ~30 s and after every calendar refresh.
    void update(const std::vector<Meeting>& meetings);

    // Drag support (grip.cpp): anchor is the TOP-RIGHT corner the badge
    // hangs from, so content growing/shrinking never moves that corner.
    // Without a custom anchor the work-area default applies.
    void setAnchor(POINT topRight) { customPos_ = true; anchor_ = topRight; }
    HWND handle() const { return hwnd_; }
    bool getRect(RECT* r) const {
        return hwnd_ && IsWindowVisible(hwnd_) && GetWindowRect(hwnd_, r);
    }

    // Valid while the badge is visible; row order matches the display.
    const std::vector<OverlayRow>& rows() const { return rows_; }

    void destroy();

private:
    void render(const std::vector<const Meeting*>& upcoming);

    HWND hwnd_ = nullptr;
    HINSTANCE inst_ = nullptr;
    bool customPos_ = false;
    POINT anchor_{};
    std::vector<OverlayRow> rows_;
    struct D2d;      // COM objects live in the .cpp so this header stays SDK-light
    D2d* d2d_ = nullptr;
};

} // namespace mn
