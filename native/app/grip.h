#pragma once
#include <windows.h>

namespace mn {

// BeOS-style drag handle: a narrow knurled strip that appears flush with
// the left edge of a host window (overlay or popup) while the cursor
// hovers over it, and drags the host to a new position. The host stays
// exactly as interactive as it was — the grip is a separate window, so
// the click-through overlay remains click-through everywhere else.
//
// The owner polls the cursor (app_window's hover timer) and calls
// showFor/hide; when a drag finishes the grip posts notifyMsg with
// wParam = id to the main window, which re-anchors the host and saves
// layout.json.
class DragGrip {
public:
    // id distinguishes the two grips in the shared notifyMsg handler.
    bool init(HINSTANCE inst, HWND notifyWnd, UINT notifyMsg, WPARAM id);

    // Positions the strip against host's current rect and shows it.
    // No-op when already visible for the same rect.
    void showFor(HWND host);
    void hide();
    void destroy();

    bool visible() const;
    bool dragging() const { return dragging_; }
    bool getRect(RECT* r) const;

private:
    static LRESULT CALLBACK gripProc(HWND, UINT, WPARAM, LPARAM);
    LRESULT onMessage(HWND, UINT, WPARAM, LPARAM);
    void render(int width, int height);

    HWND hwnd_ = nullptr;
    HWND host_ = nullptr;
    HWND notifyWnd_ = nullptr;
    UINT notifyMsg_ = 0;
    WPARAM id_ = 0;
    RECT lastHostRect_{};
    bool dragging_ = false;
    POINT dragStartCursor_{};
    POINT hostStart_{};
    POINT selfStart_{};
};

} // namespace mn
