#pragma once
#include <windows.h>
#include <string>

namespace mn {

// %LOCALAPPDATA%\MeetNow — the same directory the C# app uses
// (MeetNowSettings.cs / installed exe location). Created on demand.
std::wstring settingsDir();

// Auth-related knobs, read from the C# app's settings.json when present
// (the file is never written by the native app — the C# version owns it).
struct Settings {
    // Graph CLI public client, preauthorized for Graph — same validated
    // client the lookxy auth spike settled on (mailcore auth.rs).
    std::wstring authority = L"https://login.microsoftonline.com/organizations";
    std::wstring clientId = L"14d82eec-204b-4c2f-b7e8-296a70dab67e";
    // lookxy requests mail scopes; this app only reads the calendar.
    std::wstring scope = L"Calendars.Read offline_access";
};

Settings loadSettings();

// User-chosen overlay/popup positions, dragged via the hover grip
// (grip.cpp). Stored as the corner each window is anchored to — top-right
// for the countdown overlay, bottom-right for the join popup — so content
// resizes keep the dragged corner put. Absent fields = default corner.
struct Layout {
    bool hasOverlay = false;
    POINT overlayTopRight{};
    bool hasPopup = false;
    POINT popupBottomRight{};
};

// layout.json in settingsDir(); unlike settings.json this file is OWNED by
// the native app (written on every drag end).
Layout loadLayout();
bool saveLayout(const Layout& l);

// Shared file helpers (BrowserSelect settings.cpp/cache.cpp patterns):
// whole-file read with a 64 MB sanity cap, and atomic write via .tmp+rename.
std::string readFileBytes(const std::wstring& path);
bool writeFileAtomic(const std::wstring& path, const void* data, size_t size);

} // namespace mn
