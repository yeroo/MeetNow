#include "popup.h"
#include <d2d1.h>
#include <dwrite.h>
#include <shellapi.h>
#include <windowsx.h>
#include <wrl/client.h>
#include <cstdio>

using Microsoft::WRL::ComPtr;

namespace mn {

namespace {

constexpr wchar_t kPopupClass[] = L"MeetNowPopup";

// Metrics in DIPs, from MeetingPanelWindow's WPF element properties:
// header/row Padding="16,10", time column 50, subject MaxWidth=300,
// Join Padding="12,4" Margin="10,0,0,0", Dismiss Padding="10,4"
// Margin="8,0,0,0", corner radius 8, 20 off the work-area corner.
namespace metrics {
constexpr float kPadX = 16, kPadY = 10;
constexpr float kCorner = 8;
constexpr float kEdgeMargin = 20;
constexpr float kTimeColW = 50;
constexpr float kSubjMaxW = 300;
constexpr float kBtnPadXJoin = 12, kBtnPadXDismiss = 10, kBtnPadY = 4;
constexpr float kBtnMarginJoin = 10, kBtnMarginDismiss = 8;
constexpr float kFontTitle = 16, kFontRow = 14, kFontBtn = 12;
} // namespace metrics

D2D1_COLOR_F argb(int a, int r, int g, int b) {
    return D2D1::ColorF(r / 255.f, g / 255.f, b / 255.f, a / 255.f);
}

struct Cell {
    ComPtr<IDWriteTextLayout> layout;
    float width = 0, height = 0;
};

} // namespace

// Clickable region in window pixels. Empty url = the Dismiss button.
struct Popup::Hit {
    D2D1_RECT_F rect{};
    std::wstring url;
};

struct Popup::D2d {
    ComPtr<ID2D1Factory> factory;
    ComPtr<ID2D1DCRenderTarget> rt;
    ComPtr<IDWriteFactory> dwrite;

    float scale = 0;
    ComPtr<IDWriteTextFormat> title, time, subj, btn, btnBold;

    bool create() {
        if (FAILED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, factory.GetAddressOf())))
            return false;
        if (FAILED(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory),
                                       (IUnknown**)dwrite.GetAddressOf())))
            return false;
        const D2D1_RENDER_TARGET_PROPERTIES props = D2D1::RenderTargetProperties(
            D2D1_RENDER_TARGET_TYPE_DEFAULT,
            D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED),
            96, 96, D2D1_RENDER_TARGET_USAGE_GDI_COMPATIBLE);
        if (FAILED(factory->CreateDCRenderTarget(&props, &rt))) return false;
        rt->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
        return true;
    }

    bool formatsFor(float newScale) {
        if (scale == newScale && title) return true;
        scale = newScale;
        const auto make = [&](float size, DWRITE_FONT_WEIGHT weight,
                              ComPtr<IDWriteTextFormat>* out) {
            out->Reset();
            return SUCCEEDED(dwrite->CreateTextFormat(
                L"Segoe UI", nullptr, weight, DWRITE_FONT_STYLE_NORMAL,
                DWRITE_FONT_STRETCH_NORMAL, size * scale, L"", out->GetAddressOf()));
        };
        return make(metrics::kFontTitle, DWRITE_FONT_WEIGHT_SEMI_BOLD, &title) &&
               make(metrics::kFontRow, DWRITE_FONT_WEIGHT_BOLD, &time) &&
               make(metrics::kFontRow, DWRITE_FONT_WEIGHT_NORMAL, &subj) &&
               make(metrics::kFontBtn, DWRITE_FONT_WEIGHT_NORMAL, &btn) &&
               make(metrics::kFontBtn, DWRITE_FONT_WEIGHT_BOLD, &btnBold);
    }

    bool layoutCell(const std::wstring& text, const ComPtr<IDWriteTextFormat>& format,
                    float maxWidth, bool trim, Cell* cell) {
        cell->layout.Reset();
        if (FAILED(dwrite->CreateTextLayout(text.c_str(), (UINT32)text.size(), format.Get(),
                                            maxWidth, 512, cell->layout.GetAddressOf())))
            return false;
        cell->layout->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
        if (trim) {
            ComPtr<IDWriteInlineObject> sign;
            if (SUCCEEDED(dwrite->CreateEllipsisTrimmingSign(cell->layout.Get(), &sign))) {
                DWRITE_TRIMMING trimming{ DWRITE_TRIMMING_GRANULARITY_CHARACTER, 0, 0 };
                cell->layout->SetTrimming(&trimming, sign.Get());
            }
        }
        DWRITE_TEXT_METRICS tm{};
        if (FAILED(cell->layout->GetMetrics(&tm))) return false;
        cell->width = tm.widthIncludingTrailingWhitespace;
        cell->height = tm.height;
        return true;
    }
};

bool Popup::init(HINSTANCE inst) {
    inst_ = inst;
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = popupProc;
    wc.hInstance = inst;
    wc.lpszClassName = kPopupClass;
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    if (!RegisterClassExW(&wc)) return false;

    // Like the overlay but WITHOUT WS_EX_TRANSPARENT: this window takes
    // clicks (Join/Dismiss). WS_EX_NOACTIVATE keeps focus where it was.
    hwnd_ = CreateWindowExW(
        WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
        kPopupClass, L"", WS_POPUP, 0, 0, 1, 1, nullptr, nullptr, inst, this);
    if (!hwnd_) return false;
    SetWindowLongPtrW(hwnd_, GWLP_USERDATA, (LONG_PTR)this);

    d2d_ = new D2d();
    hits_ = new std::vector<Hit>();
    if (!d2d_->create()) {
        destroy();
        return false;
    }
    return true;
}

void Popup::show(const std::vector<Meeting>& meetings, unsigned long long autoCloseUtc) {
    if (!hwnd_ || !d2d_ || meetings.empty()) return;
    meetings_ = meetings;
    autoCloseUtc_ = autoCloseUtc;
    render();
}

void Popup::tick(unsigned long long now) {
    if (visible() && autoCloseUtc_ != 0 && now >= autoCloseUtc_) close();
}

bool Popup::visible() const {
    return hwnd_ && IsWindowVisible(hwnd_);
}

void Popup::close() {
    if (hwnd_) ShowWindow(hwnd_, SW_HIDE);
    meetings_.clear();
    if (hits_) hits_->clear();
    autoCloseUtc_ = 0;
}

void Popup::render() {
    const float scale = (float)GetDpiForWindow(hwnd_) / 96.f;
    if (!d2d_->formatsFor(scale)) return;
    hits_->clear();

    // Measure everything first (SizeToContent=WidthAndHeight equivalent).
    Cell title, dismiss;
    if (!d2d_->layoutCell(L"Upcoming Meetings", d2d_->title, 4096, false, &title)) return;
    if (!d2d_->layoutCell(L"Dismiss", d2d_->btn, 4096, false, &dismiss)) return;

    struct Row {
        Cell time, subj, join;
        bool hasUrl = false;
        float height = 0;
    };
    std::vector<Row> rows;
    for (const auto& m : meetings_) {
        Row row;
        const SYSTEMTIME local = toLocal(m.startUtc);
        wchar_t timeStr[8];
        swprintf_s(timeStr, L"%02u:%02u", local.wHour, local.wMinute);
        if (!d2d_->layoutCell(timeStr, d2d_->time, metrics::kTimeColW * scale, false, &row.time))
            return;
        const std::wstring subject = m.subject.empty() ? L"(no subject)" : m.subject;
        if (!d2d_->layoutCell(subject, d2d_->subj, metrics::kSubjMaxW * scale, true, &row.subj))
            return;
        row.hasUrl = m.joinUrl.rfind(L"https://", 0) == 0;
        if (row.hasUrl && !d2d_->layoutCell(L"Join", d2d_->btnBold, 4096, false, &row.join))
            return;
        rows.push_back(std::move(row));
    }

    const float btnH = dismiss.height + 2 * metrics::kBtnPadY * scale;
    const float headerH = 2 * metrics::kPadY * scale +
                          (title.height > btnH ? title.height : btnH);
    const float dismissW = dismiss.width + 2 * metrics::kBtnPadXDismiss * scale;

    float contentW = title.width + metrics::kBtnMarginDismiss * scale + dismissW;
    float rowsH = 0;
    for (auto& row : rows) {
        const float joinW = row.hasUrl
            ? metrics::kBtnMarginJoin * scale + row.join.width + 2 * metrics::kBtnPadXJoin * scale
            : 0;
        const float w = metrics::kTimeColW * scale + row.subj.width + joinW;
        if (w > contentW) contentW = w;
        float rh = row.time.height;
        if (row.subj.height > rh) rh = row.subj.height;
        if (row.hasUrl) {
            const float jh = row.join.height + 2 * metrics::kBtnPadY * scale;
            if (jh > rh) rh = jh;
        }
        row.height = rh + 2 * metrics::kPadY * scale;
        rowsH += row.height;
    }
    const int width = (int)(contentW + 2 * metrics::kPadX * scale + 0.5f);
    const int height = (int)(headerH + rowsH + 0.5f);

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

    const RECT rc{ 0, 0, width, height };
    bool drawn = false;
    if (SUCCEEDED(d2d_->rt->BindDC(memDc, &rc))) {
        d2d_->rt->BeginDraw();
        d2d_->rt->Clear(D2D1::ColorF(0, 0, 0, 0));

        ComPtr<ID2D1SolidColorBrush> brush;
        if (SUCCEEDED(d2d_->rt->CreateSolidColorBrush(D2D1::ColorF(0, 0), &brush))) {
            const float r = metrics::kCorner * scale;

            // Panel body (row background), rounded on every corner, then the
            // header drawn over the top with its own rounded top corners
            // (its bottom half is overpainted by the first row's rect).
            brush->SetColor(argb(230, 40, 40, 40));
            d2d_->rt->FillRoundedRectangle(
                D2D1::RoundedRect(D2D1::RectF(0, 0, (float)width, (float)height), r, r),
                brush.Get());
            brush->SetColor(argb(230, 30, 30, 30));
            d2d_->rt->FillRoundedRectangle(
                D2D1::RoundedRect(D2D1::RectF(0, 0, (float)width, headerH + r), r, r),
                brush.Get());
            brush->SetColor(argb(230, 40, 40, 40));
            d2d_->rt->FillRectangle(D2D1::RectF(0, headerH, (float)width, headerH + r),
                                    brush.Get());

            // Header: title left, Dismiss button right.
            brush->SetColor(D2D1::ColorF(D2D1::ColorF::White));
            d2d_->rt->DrawTextLayout(
                { metrics::kPadX * scale, (headerH - title.height) / 2 },
                title.layout.Get(), brush.Get());

            const D2D1_RECT_F dismissRect = D2D1::RectF(
                (float)width - metrics::kPadX * scale - dismissW,
                (headerH - btnH) / 2,
                (float)width - metrics::kPadX * scale,
                (headerH + btnH) / 2);
            brush->SetColor(argb(255, 0x4D, 0x20, 0x20));
            d2d_->rt->FillRoundedRectangle(D2D1::RoundedRect(dismissRect, 3 * scale, 3 * scale),
                                           brush.Get());
            brush->SetColor(argb(255, 0xFF, 0x88, 0x88));
            d2d_->rt->DrawTextLayout(
                { dismissRect.left + metrics::kBtnPadXDismiss * scale,
                  dismissRect.top + metrics::kBtnPadY * scale },
                dismiss.layout.Get(), brush.Get());
            hits_->push_back({ dismissRect, L"" });

            // Meeting rows.
            float y = headerH;
            for (size_t i = 0; i < rows.size(); ++i) {
                const Row& row = rows[i];
                const float innerH = row.height - 2 * metrics::kPadY * scale;
                const float yPad = y + metrics::kPadY * scale;
                const float xTime = metrics::kPadX * scale;

                brush->SetColor(argb(255, 0x88, 0xBB, 0xFF));
                d2d_->rt->DrawTextLayout(
                    { xTime, yPad + (innerH - row.time.height) / 2 },
                    row.time.layout.Get(), brush.Get());

                brush->SetColor(D2D1::ColorF(D2D1::ColorF::White));
                d2d_->rt->DrawTextLayout(
                    { xTime + metrics::kTimeColW * scale, yPad + (innerH - row.subj.height) / 2 },
                    row.subj.layout.Get(), brush.Get());

                if (row.hasUrl) {
                    const float joinW = row.join.width + 2 * metrics::kBtnPadXJoin * scale;
                    const float joinH = row.join.height + 2 * metrics::kBtnPadY * scale;
                    const D2D1_RECT_F joinRect = D2D1::RectF(
                        (float)width - metrics::kPadX * scale - joinW,
                        yPad + (innerH - joinH) / 2,
                        (float)width - metrics::kPadX * scale,
                        yPad + (innerH + joinH) / 2);
                    brush->SetColor(argb(255, 0x40, 0x80, 0x40));
                    d2d_->rt->FillRoundedRectangle(D2D1::RoundedRect(joinRect, 3 * scale, 3 * scale),
                                                   brush.Get());
                    brush->SetColor(D2D1::ColorF(D2D1::ColorF::White));
                    d2d_->rt->DrawTextLayout(
                        { joinRect.left + metrics::kBtnPadXJoin * scale,
                          joinRect.top + metrics::kBtnPadY * scale },
                        row.join.layout.Get(), brush.Get());
                    hits_->push_back({ joinRect, meetings_[i].joinUrl });
                }

                y += row.height;
                // 1-px bottom border between rows, like the WPF BorderBrush.
                if (i + 1 < rows.size()) {
                    brush->SetColor(argb(255, 60, 60, 60));
                    d2d_->rt->FillRectangle(D2D1::RectF(0, y - 1, (float)width, y), brush.Get());
                }
            }
        }
        drawn = SUCCEEDED(d2d_->rt->EndDraw());
    }

    if (drawn) {
        // Bottom-right of the primary work area, 20 DIPs off both edges —
        // just above the system tray (WorkArea excludes the taskbar).
        MONITORINFO mi{ sizeof(mi) };
        POINT origin{ 0, 0 };
        GetMonitorInfoW(MonitorFromPoint(origin, MONITOR_DEFAULTTOPRIMARY), &mi);
        POINT dst{ mi.rcWork.right - width - (int)(metrics::kEdgeMargin * scale),
                   mi.rcWork.bottom - height - (int)(metrics::kEdgeMargin * scale) };
        POINT src{ 0, 0 };
        SIZE size{ width, height };
        BLENDFUNCTION blend{ AC_SRC_OVER, 0, 255, AC_SRC_ALPHA };
        UpdateLayeredWindow(hwnd_, nullptr, &dst, &size, memDc, &src, 0, &blend, ULW_ALPHA);
        ShowWindow(hwnd_, SW_SHOWNOACTIVATE);
    }

    SelectObject(memDc, old);
    DeleteObject(dib);
    DeleteDC(memDc);
}

const Popup::Hit* Popup::hitAt(LPARAM lParam) const {
    if (!hits_) return nullptr;
    const float x = (float)GET_X_LPARAM(lParam);
    const float y = (float)GET_Y_LPARAM(lParam);
    for (const auto& h : *hits_)
        if (x >= h.rect.left && x < h.rect.right && y >= h.rect.top && y < h.rect.bottom)
            return &h;
    return nullptr;
}

LRESULT Popup::onMessage(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    switch (msg) {
    case WM_LBUTTONUP:
        if (const Hit* h = hitAt(lParam)) {
            // Join opens the meeting (https enforced at render), Dismiss
            // doesn't; both close the popup, like the WPF button handlers.
            if (!h->url.empty())
                ShellExecuteW(nullptr, L"open", h->url.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
            close();
        }
        return 0;
    case WM_SETCURSOR: {
        POINT pt;
        GetCursorPos(&pt);
        ScreenToClient(hwnd, &pt);
        SetCursor(LoadCursorW(nullptr,
                              hitAt(MAKELPARAM(pt.x, pt.y)) ? IDC_HAND : IDC_ARROW));
        return TRUE;
    }
    case WM_MOUSEACTIVATE:
        return MA_NOACTIVATE;  // clicks must not pull focus from the meeting
    }
    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

LRESULT CALLBACK Popup::popupProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    Popup* self = (Popup*)GetWindowLongPtrW(hwnd, GWLP_USERDATA);
    if (self) return self->onMessage(hwnd, msg, wParam, lParam);
    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

void Popup::destroy() {
    if (hwnd_) {
        DestroyWindow(hwnd_);
        hwnd_ = nullptr;
    }
    delete d2d_;
    d2d_ = nullptr;
    delete hits_;
    hits_ = nullptr;
}

} // namespace mn
