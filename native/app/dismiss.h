#pragma once
#include "overlay.h"
#include <windows.h>
#include <vector>

namespace mn {

// Hover companion for the countdown badge: a narrow strip flush with the
// badge's LEFT edge showing one small ✕ per meeting row, so individual
// upcoming events can be discarded from the badge without making the
// badge itself clickable. Clicking an ✕ posts notifyMsg with wParam =
// row index; the main window maps it back through rowKey().
class DismissStrip {
public:
    bool init(HINSTANCE inst, HWND notifyWnd, UINT notifyMsg);

    // Aligns one ✕ per row against the host badge and shows the strip.
    // No-op when already visible for the same host rect and row set.
    void showFor(HWND host, const std::vector<OverlayRow>& rows);
    void hide();
    void destroy();

    bool visible() const;
    bool getRect(RECT* r) const;
    int widthPx() const { return lastWidth_; }

    // The meeting key behind a clicked ✕ (index from the posted message);
    // false when the index is out of range. Staleness is prevented by the
    // callers: posted clicks drain before the timers that re-render, and
    // hide() clears the rows.
    bool rowKey(size_t index, unsigned long long* startUtc, std::wstring* subject) const;

private:
    static LRESULT CALLBACK stripProc(HWND, UINT, WPARAM, LPARAM);
    LRESULT onMessage(HWND, UINT, WPARAM, LPARAM);
    void render(int width, int height, float scale);
    int rowAt(LPARAM lParam) const;

    HWND hwnd_ = nullptr;
    HWND notifyWnd_ = nullptr;
    UINT notifyMsg_ = 0;
    RECT lastHostRect_{};
    int lastWidth_ = 0;
    int pressedRow_ = -1;  // row under the last WM_LBUTTONDOWN, else -1
    std::vector<OverlayRow> rows_;
};

} // namespace mn
