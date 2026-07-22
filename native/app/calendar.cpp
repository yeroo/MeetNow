#include "calendar.h"
#include "graph.h"
#include "json.h"
#include "str.h"
#include <algorithm>
#include <cstdio>

namespace mn {

namespace {

std::wstring calendarCachePath() {
    return settingsDir() + L"\\calendar.json";
}

unsigned long long ticksOf(const SYSTEMTIME& stUtc) {
    FILETIME ft{};
    if (!SystemTimeToFileTime(&stUtc, &ft)) return 0;
    return ((unsigned long long)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
}

} // namespace

unsigned long long nowUtc() {
    FILETIME ft{};
    GetSystemTimeAsFileTime(&ft);
    return ((unsigned long long)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
}

SYSTEMTIME toLocal(unsigned long long utcTicks) {
    FILETIME ft{ (DWORD)(utcTicks & 0xFFFFFFFF), (DWORD)(utcTicks >> 32) };
    SYSTEMTIME utc{}, local{};
    FileTimeToSystemTime(&ft, &utc);
    // nullptr = active time zone, DST decided per the date being converted.
    SystemTimeToTzSpecificLocalTime(nullptr, &utc, &local);
    return local;
}

unsigned long long todayLocalMidnightUtc() {
    SYSTEMTIME local{};
    GetLocalTime(&local);
    local.wHour = local.wMinute = local.wSecond = local.wMilliseconds = 0;
    SYSTEMTIME utc{};
    if (!TzSpecificLocalTimeToSystemTime(nullptr, &local, &utc)) return nowUtc();
    return ticksOf(utc);
}

std::wstring formatIsoUtc(unsigned long long utcTicks) {
    FILETIME ft{ (DWORD)(utcTicks & 0xFFFFFFFF), (DWORD)(utcTicks >> 32) };
    SYSTEMTIME st{};
    FileTimeToSystemTime(&ft, &st);
    wchar_t buf[32];
    swprintf_s(buf, L"%04u-%02u-%02uT%02u:%02u:%02uZ",
               st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    return buf;
}

unsigned long long parseIsoUtc(const std::wstring& iso) {
    SYSTEMTIME st{};
    int y = 0, mo = 0, d = 0, h = 0, mi = 0, s = 0;
    if (swscanf_s(iso.c_str(), L"%d-%d-%dT%d:%d:%d", &y, &mo, &d, &h, &mi, &s) < 6) return 0;
    st.wYear = (WORD)y; st.wMonth = (WORD)mo; st.wDay = (WORD)d;
    st.wHour = (WORD)h; st.wMinute = (WORD)mi; st.wSecond = (WORD)s;
    return ticksOf(st);
}

RefreshResult refreshCalendar(const Settings& s) {
    RefreshResult result;

    TokenSet token = acquireTokenSilent(s);
    if (token.empty()) {
        result.status = RefreshStatus::AuthRequired;
        return result;
    }

    const unsigned long long from = todayLocalMidnightUtc();
    const unsigned long long to = from + 2 * kTicksPerDay;
    CalendarViewResult view =
        fetchCalendarView(token.accessToken, formatIsoUtc(from), formatIsoUtc(to));

    // A 401 with a token the silent path considered fresh: refresh once and
    // retry, like lookxy's with_auth. A second 401 means sign-in is needed.
    if (view.status == 401) {
        TokenSet expired = token;
        expired.expiresAtUnix = 0;  // force the refresh-token exchange
        token = ensureFresh(s, expired);
        if (token.empty()) {
            result.status = RefreshStatus::AuthRequired;
            return result;
        }
        view = fetchCalendarView(token.accessToken, formatIsoUtc(from), formatIsoUtc(to));
        if (view.status == 401) {
            result.status = RefreshStatus::AuthRequired;
            return result;
        }
    }
    if (view.status < 200 || view.status >= 300) return result;  // Error

    std::sort(view.meetings.begin(), view.meetings.end(),
              [](const Meeting& a, const Meeting& b) { return a.startUtc < b.startUtc; });
    result.status = RefreshStatus::Ok;
    result.meetings = std::move(view.meetings);

    // Replace the whole cached window on every successful fetch, mirroring
    // lookxy's replace-not-upsert (cancelled/moved meetings disappear).
    auto root = json::Value::makeObject();
    auto arr = json::Value::makeArray();
    for (const auto& m : result.meetings) {
        auto e = json::Value::makeObject();
        e->set(L"subject", json::Value::makeString(m.subject));
        e->set(L"startUtc", json::Value::makeString(formatIsoUtc(m.startUtc)));
        e->set(L"endUtc", json::Value::makeString(formatIsoUtc(m.endUtc)));
        e->set(L"joinUrl", json::Value::makeString(m.joinUrl));
        arr->array.push_back(e);
    }
    root->set(L"meetings", arr);
    const std::string out = json::serializeIndented(root);
    writeFileAtomic(calendarCachePath(), out.data(), out.size());

    return result;
}

std::vector<Meeting> loadCalendarCache() {
    std::vector<Meeting> meetings;
    const auto root = json::parse(readFileBytes(calendarCachePath()));
    if (!root) return meetings;
    const auto arr = root->get(L"meetings");
    if (!arr || arr->type != json::Type::Array) return meetings;
    for (const auto& e : arr->array) {
        if (!e || e->type != json::Type::Object) continue;
        Meeting m;
        m.subject = e->getString(L"subject");
        m.startUtc = parseIsoUtc(e->getString(L"startUtc"));
        m.endUtc = parseIsoUtc(e->getString(L"endUtc"));
        m.joinUrl = e->getString(L"joinUrl");
        if (m.startUtc != 0) meetings.push_back(std::move(m));
    }
    std::sort(meetings.begin(), meetings.end(),
              [](const Meeting& a, const Meeting& b) { return a.startUtc < b.startUtc; });
    return meetings;
}

} // namespace mn
