#include "dismiss.h"
#include <windowsx.h>
#include <cmath>

namespace mn {

namespace {

constexpr wchar_t kStripClass[] = L"MeetNowDismiss";

// DIPs: strip width 18, ✕ glyph 8 wide/tall, ~1.5 stroke.
constexpr int kStripW = 18;
constexpr float kGlyph = 8.f;
constexpr float kStroke = 1.5f;

// Premultiplied BGRA, matching the grip chrome; the ✕ is mid gray and
// meant to read as secondary UI, not an alarm.
constexpr BYTE kBgA = 200, kBgC = (BYTE)(30 * 200 / 255);
constexpr BYTE kXA = 255, kXC = 190;

} // namespace

bool DismissStrip::init(HINSTANCE inst, HWND notifyWnd, UINT notifyMsg) {
    notifyWnd_ = notifyWnd;
    notifyMsg_ = notifyMsg;

    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = stripProc;
    wc.hInstance = inst;
    wc.lpszClassName = kStripClass;
    wc.hCursor = LoadCursorW(nullptr, IDC_HAND);
    if (!RegisterClassExW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
        return false;

    hwnd_ = CreateWindowExW(
        WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
        kStripClass, L"", WS_POPUP, 0, 0, 1, 1, nullptr, nullptr, inst, nullptr);
    if (!hwnd_) return false;
    SetWindowLongPtrW(hwnd_, GWLP_USERDATA, (LONG_PTR)this);
    return true;
}

void DismissStrip::showFor(HWND host, const std::vector<OverlayRow>& rows) {
    if (!hwnd_ || !host || rows.empty()) return;
    RECT hr{};
    if (!GetWindowRect(host, &hr)) return;
    // Same rect + same row COUNT is not enough: a right-anchored badge can
    // swap meeting sets without changing size, which would leave stale keys
    // behind the ✕s — compare the keys too.
    bool same = visible() && EqualRect(&hr, &lastHostRect_) && rows.size() == rows_.size();
    for (size_t i = 0; same && i < rows.size(); ++i)
        same = rows[i].startUtc == rows_[i].startUtc && rows[i].subject == rows_[i].subject;
    if (same) return;
    lastHostRect_ = hr;
    rows_ = rows;
    pressedRow_ = -1;

    const float scale = (float)GetDpiForWindow(host) / 96.f;
    const int width = (int)(kStripW * scale + 0.5f);
    const int height = hr.bottom - hr.top;
    lastWidth_ = width;

    render(width, height, scale);
    // Overlap the host when flush-left would land off the virtual screen
    // (badge dragged to the far left edge), same clamp as the grip.
    int x = hr.left - width;
    if (x < GetSystemMetrics(SM_XVIRTUALSCREEN)) x = hr.left;
    SetWindowPos(hwnd_, HWND_TOPMOST, x, hr.top, width, height,
                 SWP_NOACTIVATE | SWP_SHOWWINDOW);
}

void DismissStrip::render(int width, int height, float scale) {
    BITMAPINFO bmi{};
    bmi.bmiHeader.biSize = sizeof(bmi.bmiHeader);
    bmi.bmiHeader.biWidth = width;
    bmi.bmiHeader.biHeight = -height;
    bmi.bmiHeader.biPlanes = 1;
    bmi.bmiHeader.biBitCount = 32;
    bmi.bmiHeader.biCompression = BI_RGB;
    HDC memDc = CreateCompatibleDC(nullptr);
    if (!memDc) return;
    void* bits = nullptr;
    HBITMAP dib = CreateDIBSection(memDc, &bmi, DIB_RGB_COLORS, &bits, nullptr, 0);
    if (!dib) {
        DeleteDC(memDc);
        return;
    }
    HGDIOBJ old = SelectObject(memDc, dib);

    BYTE* px = (BYTE*)bits;
    for (int y = 0; y < height; ++y)
        for (int x = 0; x < width; ++x) {
            BYTE* p = px + 4 * ((size_t)y * width + x);
            p[0] = kBgC; p[1] = kBgC; p[2] = kBgC; p[3] = kBgA;
        }

    // One ✕ centered in each row band: pixels near either diagonal of a
    // glyph-sized box get the foreground color.
    const float g = kGlyph * scale;
    const float stroke = kStroke * scale;
    for (const auto& row : rows_) {
        const float cx = width / 2.f;
        const float cy = row.top + row.height / 2.f;
        const int x0 = (int)(cx - g / 2), x1 = (int)(cx + g / 2);
        const int y0 = (int)(cy - g / 2), y1 = (int)(cy + g / 2);
        for (int y = y0; y <= y1 && y < height; ++y) {
            if (y < 0) continue;
            for (int x = x0; x <= x1 && x < width; ++x) {
                if (x < 0) continue;
                // Distance from the two diagonals of the glyph box.
                const float fx = x - cx, fy = y - cy;
                const bool onMain = std::fabs(fx - fy) <= stroke / 2;
                const bool onAnti = std::fabs(fx + fy) <= stroke / 2;
                if (onMain || onAnti) {
                    BYTE* p = px + 4 * ((size_t)y * width + x);
                    p[0] = kXC; p[1] = kXC; p[2] = kXC; p[3] = kXA;
                }
            }
        }
    }

    POINT src{ 0, 0 };
    SIZE size{ width, height };
    BLENDFUNCTION blend{ AC_SRC_OVER, 0, 255, AC_SRC_ALPHA };
    UpdateLayeredWindow(hwnd_, nullptr, nullptr, &size, memDc, &src, 0, &blend, ULW_ALPHA);

    SelectObject(memDc, old);
    DeleteObject(dib);
    DeleteDC(memDc);
}

void DismissStrip::hide() {
    if (hwnd_) ShowWindow(hwnd_, SW_HIDE);
    rows_.clear();
    pressedRow_ = -1;
}

bool DismissStrip::visible() const {
    return hwnd_ && IsWindowVisible(hwnd_);
}

bool DismissStrip::getRect(RECT* r) const {
    return hwnd_ && IsWindowVisible(hwnd_) && GetWindowRect(hwnd_, r);
}

bool DismissStrip::rowKey(size_t index, unsigned long long* startUtc,
                          std::wstring* subject) const {
    if (index >= rows_.size()) return false;
    *startUtc = rows_[index].startUtc;
    *subject = rows_[index].subject;
    return true;
}

int DismissStrip::rowAt(LPARAM lParam) const {
    const float y = (float)GET_Y_LPARAM(lParam);
    for (size_t i = 0; i < rows_.size(); ++i)
        if (y >= rows_[i].top && y < rows_[i].top + rows_[i].height) return (int)i;
    return -1;
}

LRESULT DismissStrip::onMessage(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    switch (msg) {
    case WM_LBUTTONDOWN:
        pressedRow_ = rowAt(lParam);
        return 0;
    case WM_LBUTTONUP: {
        // Click contract: down and up on the same row, else ignore.
        const int row = rowAt(lParam);
        if (row >= 0 && row == pressedRow_)
            PostMessageW(notifyWnd_, notifyMsg_, (WPARAM)row, 0);
        pressedRow_ = -1;
        return 0;
    }
    case WM_MOUSEACTIVATE:
        return MA_NOACTIVATE;
    }
    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

LRESULT CALLBACK DismissStrip::stripProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    DismissStrip* self = (DismissStrip*)GetWindowLongPtrW(hwnd, GWLP_USERDATA);
    if (self) return self->onMessage(hwnd, msg, wParam, lParam);
    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

void DismissStrip::destroy() {
    if (hwnd_) {
        DestroyWindow(hwnd_);
        hwnd_ = nullptr;
    }
}

} // namespace mn
