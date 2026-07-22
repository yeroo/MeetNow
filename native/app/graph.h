#pragma once
#include "calendar.h"
#include <string>
#include <vector>

namespace mn {

// GET /me/calendarView with `Prefer: outlook.timezone="UTC"` so Graph both
// expands recurring series into concrete instances server-side and returns
// every start/end already in UTC — the two design decisions lifted from
// lookxy's mailcore graph client. Follows @odata.nextLink.
struct CalendarViewResult {
    int status = 0;  // last HTTP status; 0 = transport failure
    std::vector<Meeting> meetings;
};

CalendarViewResult fetchCalendarView(const std::string& accessToken,
                                     const std::wstring& fromIsoUtc,
                                     const std::wstring& toIsoUtc);

} // namespace mn
