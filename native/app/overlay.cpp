#include "overlay.h"
#include <d2d1.h>
#include <dwrite.h>
#include <wrl/client.h>
#include <cstdio>

using Microsoft::WRL::ComPtr;

namespace mn {

namespace {

constexpr wchar_t kOverlayClass[] = L"MeetNowOverlay";

// Metrics in DIPs, straight from MeetingCountdownOverlay.cs XAML-in-code:
// Border Padding="12,8,12,8", CornerRadius=8, window MaxWidth=350,
// margin 12 from the work-area corner, time column 42, subject Margin="4,0,8,0".
namespace metrics {
constexpr float kPadL = 12, kPadT = 8, kPadR = 12, kPadB = 8;
constexpr float kCorner = 8;
constexpr float kMaxWidth = 350;
constexpr float kEdgeMargin = 12;
constexpr float kTimeColW = 42;
constexpr float kSubjMarginL = 4, kSubjMarginR = 8;
constexpr float kFontMain = 12, kFontCountdown = 11;
} // namespace metrics

LRESULT CALLBACK overlayProc(HWND hwnd, UINT msg, WPARAM w, LPARAM l) {
    return DefWindowProcW(hwnd, msg, w, l);
}

struct Cell {
    std::wstring text;
    ComPtr<IDWriteTextLayout> layout;
    float width = 0, height = 0;
    D2D1_COLOR_F color{};
};

D2D1_COLOR_F rgb(int r, int g, int b) {
    return D2D1::ColorF(r / 255.f, g / 255.f, b / 255.f, 1.f);
}

} // namespace

struct Overlay::D2d {
    ComPtr<ID2D1Factory> factory;
    ComPtr<ID2D1DCRenderTarget> rt;
    ComPtr<IDWriteFactory> dwrite;

    // Text formats, recreated when the DPI scale changes (fonts are created
    // pre-scaled so the render target can stay at its default 96 DPI).
    float scale = 0;
    ComPtr<IDWriteTextFormat> time, timeBold, subj, subjSemi, countdown, countdownSemi;

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
        // ClearType over a transparent layered surface leaves color fringes;
        // grayscale AA is the standard fix.
        rt->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
        return true;
    }

    bool formatsFor(float newScale) {
        if (scale == newScale && time) return true;
        scale = newScale;
        const auto make = [&](float size, DWRITE_FONT_WEIGHT weight,
                              ComPtr<IDWriteTextFormat>* out) {
            out->Reset();
            return SUCCEEDED(dwrite->CreateTextFormat(
                L"Segoe UI", nullptr, weight, DWRITE_FONT_STYLE_NORMAL,
                DWRITE_FONT_STRETCH_NORMAL, size * scale, L"", out->GetAddressOf()));
        };
        return make(metrics::kFontMain, DWRITE_FONT_WEIGHT_NORMAL, &time) &&
               make(metrics::kFontMain, DWRITE_FONT_WEIGHT_BOLD, &timeBold) &&
               make(metrics::kFontMain, DWRITE_FONT_WEIGHT_NORMAL, &subj) &&
               make(metrics::kFontMain, DWRITE_FONT_WEIGHT_SEMI_BOLD, &subjSemi) &&
               make(metrics::kFontCountdown, DWRITE_FONT_WEIGHT_NORMAL, &countdown) &&
               make(metrics::kFontCountdown, DWRITE_FONT_WEIGHT_SEMI_BOLD, &countdownSemi);
    }

    bool layoutCell(const std::wstring& text, const ComPtr<IDWriteTextFormat>& format,
                    float maxWidth, bool trim, Cell* cell) {
        cell->text = text;
        cell->layout.Reset();
        if (FAILED(dwrite->CreateTextLayout(text.c_str(), (UINT32)text.size(), format.Get(),
                                            maxWidth, 512, cell->layout.GetAddressOf())))
            return false;
        cell->layout->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
        if (trim) {
            // Ellipsis-trimmed like the WPF subject TextBlock.
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

bool Overlay::init(HINSTANCE inst) {
    inst_ = inst;
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = overlayProc;
    wc.hInstance = inst;
    wc.lpszClassName = kOverlayClass;
    if (!RegisterClassExW(&wc)) return false;

    // Same extended styles MeetingCountdownOverlay.cs applies after Show():
    // click-through, no taskbar/alt-tab presence, never activated, topmost.
    hwnd_ = CreateWindowExW(
        WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
        kOverlayClass, L"", WS_POPUP, 0, 0, 1, 1, nullptr, nullptr, inst, nullptr);
    if (!hwnd_) return false;

    d2d_ = new D2d();
    if (!d2d_->create()) {
        destroy();
        return false;
    }
    return true;
}

void Overlay::update(const std::vector<Meeting>& meetings) {
    if (!hwnd_ || !d2d_) return;

    // Starts within 2 hours or is in progress — unlike the C# filter
    // (m.Start > now), a running meeting stays on screen until endUtc so a
    // late join is still prompted; its row switches to an "ends in" count.
    const unsigned long long now = nowUtc();
    std::vector<const Meeting*> upcoming;
    for (const auto& m : meetings)
        if (m.endUtc > now && m.startUtc <= now + 2 * kTicksPerHour)
            upcoming.push_back(&m);

    if (upcoming.empty()) {
        ShowWindow(hwnd_, SW_HIDE);
        return;
    }
    render(upcoming);
}

void Overlay::render(const std::vector<const Meeting*>& upcoming) {
    const float scale = (float)GetDpiForWindow(hwnd_) / 96.f;
    if (!d2d_->formatsFor(scale)) return;
    const unsigned long long now = nowUtc();

    // Build and measure all cells first; the window is sized to content
    // like WPF's SizeToContent=WidthAndHeight with MaxWidth=350.
    struct Row {
        Cell time, subj, countdown;
        float height = 0;
    };
    std::vector<Row> rows;
    const float maxContentW = (metrics::kMaxWidth - metrics::kPadL - metrics::kPadR) * scale;

    for (size_t i = 0; i < upcoming.size(); ++i) {
        const Meeting& m = *upcoming[i];
        const bool next = i == 0;  // the soonest meeting gets the emphasis styling
        Row row;

        const SYSTEMTIME local = toLocal(m.startUtc);
        wchar_t timeStr[8];
        swprintf_s(timeStr, L"%02u:%02u", local.wHour, local.wMinute);

        const bool started = m.startUtc <= now;
        // Upcoming rows count down to start; in-progress rows count down to
        // the end (endUtc > now is guaranteed by the update() filter).
        const unsigned long long remaining = started ? m.endUtc - now : m.startUtc - now;
        const unsigned long long totalSeconds = remaining / kTicksPerSecond;
        const unsigned long long totalMinutes = totalSeconds / 60;
        wchar_t cdStr[32];
        const wchar_t* prefix = started ? L"ends in" : L"in";
        if (totalMinutes >= 60) {
            swprintf_s(cdStr, L"%s %llu:%02llu:%02llu", prefix, totalMinutes / 60,
                       totalMinutes % 60, totalSeconds % 60);
        } else {
            swprintf_s(cdStr, L"%s %llu:%02llu", prefix, totalMinutes, totalSeconds % 60);
        }
        // Countdown urgency colors: red <= 5 min, yellow <= 15 min, gray
        // else; in-progress rows stay red — the pressure is to join late.
        const D2D1_COLOR_F cdColor = started                   ? rgb(255, 100, 100)
                                     : totalSeconds <= 5 * 60  ? rgb(255, 100, 100)
                                     : totalSeconds <= 15 * 60 ? rgb(255, 200, 60)
                                                               : rgb(120, 120, 120);

        if (!d2d_->layoutCell(timeStr, next ? d2d_->timeBold : d2d_->time, maxContentW, false,
                              &row.time))
            return;
        row.time.color = next ? rgb(86, 156, 214) : rgb(140, 140, 140);

        if (!d2d_->layoutCell(cdStr, next ? d2d_->countdownSemi : d2d_->countdown, maxContentW,
                              false, &row.countdown))
            return;
        row.countdown.color = cdColor;

        const float subjAvail =
            maxContentW - (metrics::kTimeColW + metrics::kSubjMarginL + metrics::kSubjMarginR) * scale -
            row.countdown.width;
        const std::wstring subject = m.subject.empty() ? L"(no subject)" : m.subject;
        if (!d2d_->layoutCell(subject, next ? d2d_->subjSemi : d2d_->subj,
                              subjAvail > 8 ? subjAvail : 8, true, &row.subj))
            return;
        row.subj.color = next ? rgb(255, 255, 255) : rgb(180, 180, 180);

        row.height = row.time.height;
        if (row.subj.height > row.height) row.height = row.subj.height;
        if (row.countdown.height > row.height) row.height = row.countdown.height;
        rows.push_back(std::move(row));
    }

    float contentW = 0, contentH = 0;
    for (const auto& row : rows) {
        const float w = (metrics::kTimeColW + metrics::kSubjMarginL + metrics::kSubjMarginR) * scale +
                        row.subj.width + row.countdown.width;
        if (w > contentW) contentW = w;
        contentH += row.height;
    }
    if (contentW > maxContentW) contentW = maxContentW;
    const int width = (int)(contentW + (metrics::kPadL + metrics::kPadR) * scale + 0.5f);
    const int height = (int)(contentH + (metrics::kPadT + metrics::kPadB) * scale + 0.5f);

    // 32-bit premultiplied top-down DIB for UpdateLayeredWindow.
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
            // Border: ARGB(180, 30, 30, 30), corner radius 8.
            brush->SetColor(D2D1::ColorF(30 / 255.f, 30 / 255.f, 30 / 255.f, 180 / 255.f));
            const D2D1_ROUNDED_RECT rr = D2D1::RoundedRect(
                D2D1::RectF(0, 0, (float)width, (float)height),
                metrics::kCorner * scale, metrics::kCorner * scale);
            d2d_->rt->FillRoundedRectangle(rr, brush.Get());

            float y = metrics::kPadT * scale;
            const float xTime = metrics::kPadL * scale;
            const float xSubj = xTime + (metrics::kTimeColW + metrics::kSubjMarginL) * scale;
            for (const auto& row : rows) {
                brush->SetColor(row.time.color);
                d2d_->rt->DrawTextLayout({ xTime, y }, row.time.layout.Get(), brush.Get());
                brush->SetColor(row.subj.color);
                d2d_->rt->DrawTextLayout({ xSubj, y }, row.subj.layout.Get(), brush.Get());
                brush->SetColor(row.countdown.color);
                const float xCd = (float)width - metrics::kPadR * scale - row.countdown.width;
                d2d_->rt->DrawTextLayout({ xCd, y }, row.countdown.layout.Get(), brush.Get());
                y += row.height;
            }
        }
        drawn = SUCCEEDED(d2d_->rt->EndDraw());
    }

    if (drawn) {
        // Top-right of the primary work area, 12 DIPs off both edges
        // (WorkArea.Right - width - 12 / WorkArea.Top + 12 in the C# code).
        MONITORINFO mi{ sizeof(mi) };
        POINT origin{ 0, 0 };
        GetMonitorInfoW(MonitorFromPoint(origin, MONITOR_DEFAULTTOPRIMARY), &mi);
        POINT dst{ mi.rcWork.right - width - (int)(metrics::kEdgeMargin * scale),
                   mi.rcWork.top + (int)(metrics::kEdgeMargin * scale) };
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

void Overlay::destroy() {
    if (hwnd_) {
        DestroyWindow(hwnd_);
        hwnd_ = nullptr;
    }
    delete d2d_;
    d2d_ = nullptr;
}

} // namespace mn
