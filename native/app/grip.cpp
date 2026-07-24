#include "grip.h"

namespace mn {

namespace {

constexpr wchar_t kGripClass[] = L"MeetNowGrip";

// Strip geometry in DIPs: 14 wide, dots on a 5-px grid with a 4-px
// margin — two knurled columns, the classic BeOS handle look.
constexpr int kGripW = 14;
constexpr int kDotStep = 5;
constexpr int kDotSize = 2;
constexpr int kDotMargin = 4;

// Premultiplied BGRA. Background matches the overlay chrome but slightly
// more opaque, so the handle reads as solid; dots are light gray.
constexpr BYTE kBgA = 200, kBgC = (BYTE)(30 * 200 / 255);
constexpr BYTE kDotA = 255, kDotC = 170;

} // namespace

bool DragGrip::init(HINSTANCE inst, HWND notifyWnd, UINT notifyMsg, WPARAM id) {
    notifyWnd_ = notifyWnd;
    notifyMsg_ = notifyMsg;
    id_ = id;

    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = gripProc;
    wc.hInstance = inst;
    wc.lpszClassName = kGripClass;
    wc.hCursor = LoadCursorW(nullptr, IDC_SIZEALL);
    if (!RegisterClassExW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
        return false;  // two grips share one class registration

    hwnd_ = CreateWindowExW(
        WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
        kGripClass, L"", WS_POPUP, 0, 0, 1, 1, nullptr, nullptr, inst, nullptr);
    if (!hwnd_) return false;
    SetWindowLongPtrW(hwnd_, GWLP_USERDATA, (LONG_PTR)this);
    return true;
}

void DragGrip::showFor(HWND host, int extraLeftGapPx) {
    if (!hwnd_ || !host) return;
    RECT hr{};
    if (!GetWindowRect(host, &hr)) return;
    if (host == host_ && visible() && EqualRect(&hr, &lastHostRect_)) return;
    host_ = host;
    lastHostRect_ = hr;

    const float scale = (float)GetDpiForWindow(host) / 96.f;
    const int width = (int)(kGripW * scale + 0.5f);
    const int height = hr.bottom - hr.top;
    // Flush with the host's left edge (or its other companion strip);
    // overlap the host when that would leave the screen on the far left.
    int x = hr.left - width - extraLeftGapPx;
    if (x < GetSystemMetrics(SM_XVIRTUALSCREEN)) x = hr.left;

    render(width, height);
    SetWindowPos(hwnd_, HWND_TOPMOST, x, hr.top, width, height,
                 SWP_NOACTIVATE | SWP_SHOWWINDOW);
}

void DragGrip::render(int width, int height) {
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

    const float scale = (float)width / kGripW;
    const int margin = (int)(kDotMargin * scale + 0.5f);
    const int step = (int)(kDotStep * scale + 0.5f);
    const int dot = (int)(kDotSize * scale + 0.5f);

    BYTE* px = (BYTE*)bits;
    for (int y = 0; y < height; ++y) {
        for (int x = 0; x < width; ++x) {
            // Dot when both coordinates land on the knurl grid.
            const bool inX = x >= margin && x < width - margin && (x - margin) % step < dot;
            const bool inY = y >= margin && y < height - margin && (y - margin) % step < dot;
            BYTE* p = px + 4 * ((size_t)y * width + x);
            const BYTE c = (inX && inY) ? kDotC : kBgC;
            const BYTE a = (inX && inY) ? kDotA : kBgA;
            p[0] = c; p[1] = c; p[2] = c; p[3] = a;
        }
    }

    POINT src{ 0, 0 };
    SIZE size{ width, height };
    BLENDFUNCTION blend{ AC_SRC_OVER, 0, 255, AC_SRC_ALPHA };
    // Position is applied by the SetWindowPos in showFor.
    UpdateLayeredWindow(hwnd_, nullptr, nullptr, &size, memDc, &src, 0, &blend, ULW_ALPHA);

    SelectObject(memDc, old);
    DeleteObject(dib);
    DeleteDC(memDc);
}

void DragGrip::hide() {
    if (hwnd_ && !dragging_) ShowWindow(hwnd_, SW_HIDE);
}

bool DragGrip::visible() const {
    return hwnd_ && IsWindowVisible(hwnd_);
}

bool DragGrip::getRect(RECT* r) const {
    return hwnd_ && IsWindowVisible(hwnd_) && GetWindowRect(hwnd_, r);
}

LRESULT DragGrip::onMessage(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    switch (msg) {
    case WM_LBUTTONDOWN: {
        if (!host_) break;
        dragging_ = true;
        SetCapture(hwnd);
        GetCursorPos(&dragStartCursor_);
        RECT hr{}, gr{};
        GetWindowRect(host_, &hr);
        GetWindowRect(hwnd, &gr);
        hostStart_ = { hr.left, hr.top };
        selfStart_ = { gr.left, gr.top };
        return 0;
    }
    case WM_MOUSEMOVE:
        if (dragging_) {
            POINT cur{};
            GetCursorPos(&cur);
            const int dx = cur.x - dragStartCursor_.x;
            const int dy = cur.y - dragStartCursor_.y;
            SetWindowPos(host_, nullptr, hostStart_.x + dx, hostStart_.y + dy, 0, 0,
                         SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            SetWindowPos(hwnd, nullptr, selfStart_.x + dx, selfStart_.y + dy, 0, 0,
                         SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
        return 0;
    case WM_LBUTTONUP:
        if (dragging_) {
            dragging_ = false;
            ReleaseCapture();
            GetWindowRect(host_, &lastHostRect_);  // stay aligned, no flicker
            PostMessageW(notifyWnd_, notifyMsg_, id_, 0);
        }
        return 0;
    case WM_CAPTURECHANGED:
        dragging_ = false;
        return 0;
    case WM_MOUSEACTIVATE:
        return MA_NOACTIVATE;
    }
    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

LRESULT CALLBACK DragGrip::gripProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    DragGrip* self = (DragGrip*)GetWindowLongPtrW(hwnd, GWLP_USERDATA);
    if (self) return self->onMessage(hwnd, msg, wParam, lParam);
    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

void DragGrip::destroy() {
    if (hwnd_) {
        DestroyWindow(hwnd_);
        hwnd_ = nullptr;
    }
}

} // namespace mn
