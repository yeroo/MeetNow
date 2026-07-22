#include "graph.h"
#include "http.h"
#include "json.h"
#include "str.h"

namespace mn {

namespace {

// Fallback when onlineMeeting.joinUrl is absent: first Teams meetup-join
// link in the body preview — mirrors OutlookCacheReader.cs's raw scan.
std::wstring teamsUrlFromText(const std::wstring& text) {
    const std::wstring needle = L"https://teams.microsoft.com/l/meetup-join/";
    const size_t p = text.find(needle);
    if (p == std::wstring::npos) return L"";
    size_t end = p;
    while (end < text.size()) {
        const wchar_t c = text[end];
        if (c == L' ' || c == L'\t' || c == L'\n' || c == L'\r' ||
            c == L'<' || c == L'>' || c == L'"' || c == L'\'' || c == L'\\')
            break;
        ++end;
    }
    return text.substr(p, end - p);
}

// One Graph event -> Meeting; false when it should be dropped (all-day,
// cancelled, unparsable dates).
bool meetingFromEvent(const json::ValuePtr& ev, Meeting* m) {
    if (!ev || ev->type != json::Type::Object) return false;
    if (ev->getBool(L"isAllDay") || ev->getBool(L"isCancelled")) return false;

    const auto timeOf = [](const json::ValuePtr& field) -> unsigned long long {
        if (!field || field->type != json::Type::Object) return 0;
        return parseIsoUtc(field->getString(L"dateTime"));
    };
    m->startUtc = timeOf(ev->get(L"start"));
    m->endUtc = timeOf(ev->get(L"end"));
    if (m->startUtc == 0) return false;
    if (m->endUtc == 0) m->endUtc = m->startUtc + kTicksPerHour;  // default 1 h, like OutlookCacheReader

    m->subject = ev->getString(L"subject");
    if (const auto om = ev->get(L"onlineMeeting"); om && om->type == json::Type::Object)
        m->joinUrl = om->getString(L"joinUrl");
    if (m->joinUrl.empty()) m->joinUrl = ev->getString(L"onlineMeetingUrl");
    if (m->joinUrl.empty()) m->joinUrl = teamsUrlFromText(ev->getString(L"bodyPreview"));
    return true;
}

} // namespace

CalendarViewResult fetchCalendarView(const std::string& accessToken,
                                     const std::wstring& fromIsoUtc,
                                     const std::wstring& toIsoUtc) {
    CalendarViewResult result;
    std::wstring url =
        L"https://graph.microsoft.com/v1.0/me/calendarView?startDateTime=" + fromIsoUtc +
        L"&endDateTime=" + toIsoUtc +
        L"&$top=50&$select=subject,start,end,isAllDay,isCancelled,onlineMeeting,onlineMeetingUrl,bodyPreview";
    const std::vector<std::wstring> headers = {
        L"Authorization: Bearer " + widen(accessToken),
        L"Accept: application/json",
        L"Prefer: outlook.timezone=\"UTC\"",
    };

    // Follow @odata.nextLink (absolute URL), like client.rs calendar_view.
    for (int page = 0; page < 20; ++page) {
        const HttpResponse resp = httpGet(url, headers);
        result.status = resp.status;
        if (resp.status < 200 || resp.status >= 300) return result;

        const auto v = json::parse(resp.body);
        if (!v || v->type != json::Type::Object) {
            result.status = 0;
            return result;
        }
        if (const auto items = v->get(L"value"); items && items->type == json::Type::Array) {
            for (const auto& ev : items->array) {
                Meeting m;
                if (meetingFromEvent(ev, &m)) result.meetings.push_back(std::move(m));
            }
        }
        const std::wstring next = v->getString(L"@odata.nextLink");
        if (next.empty()) break;
        url = next;
    }
    return result;
}

} // namespace mn
