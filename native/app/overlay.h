#pragma once
#include "calendar.h"
#include <windows.h>
#include <vector>

namespace mn {

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

    void destroy();

private:
    void render(const std::vector<const Meeting*>& upcoming);

    HWND hwnd_ = nullptr;
    HINSTANCE inst_ = nullptr;
    struct D2d;      // COM objects live in the .cpp so this header stays SDK-light
    D2d* d2d_ = nullptr;
};

} // namespace mn
