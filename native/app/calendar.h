#pragma once
#include "auth.h"
#include "settings.h"
#include <windows.h>
#include <string>
#include <vector>

namespace mn {

// The meeting model — the four kept features only ever used Start, End,
// Subject and TeamsUrl of the C# TeamsMeeting, so that is all that's left.
// Times are UTC FILETIME ticks (100 ns since 1601) so comparisons are
// plain integer math; conversion to local time happens at display time,
// per-date via SystemTimeToTzSpecificLocalTime (correct across DST, unlike
// lookxy's current-bias-only conversion).
struct Meeting {
    unsigned long long startUtc = 0;
    unsigned long long endUtc = 0;
    std::wstring subject;
    std::wstring joinUrl;  // Teams join URL; empty when the event has none
};

// -- time helpers ----------------------------------------------------------

unsigned long long nowUtc();                       // FILETIME ticks
constexpr unsigned long long kTicksPerSecond = 10'000'000ULL;
constexpr unsigned long long kTicksPerMinute = 60 * kTicksPerSecond;
constexpr unsigned long long kTicksPerHour = 60 * kTicksPerMinute;
constexpr unsigned long long kTicksPerDay = 24 * kTicksPerHour;

// UTC ticks -> local wall-clock time (DST applied for that date).
SYSTEMTIME toLocal(unsigned long long utcTicks);

// Local midnight of the current day, as UTC ticks.
unsigned long long todayLocalMidnightUtc();

// "YYYY-MM-DDTHH:MM:SSZ" for Graph query parameters.
std::wstring formatIsoUtc(unsigned long long utcTicks);

// Parses Graph's "YYYY-MM-DDTHH:MM:SS[.fffffff]" (already UTC thanks to the
// Prefer header). 0 on malformed input.
unsigned long long parseIsoUtc(const std::wstring& iso);

// -- refresh ---------------------------------------------------------------

enum class RefreshStatus {
    Ok,
    AuthRequired,  // no cached token and silent refresh failed
    Error,         // transport failure / throttled / server error
};

struct RefreshResult {
    RefreshStatus status = RefreshStatus::Error;
    std::vector<Meeting> meetings;  // valid only when status == Ok
};

// Full fetch of [today local midnight, +2 days) — enough for the tray menu
// (today) and the overlay (next 2 h). Runs silent auth internally (cached
// token / refresh); never opens a browser. On success the result replaces
// the on-disk cache, mirroring lookxy's replace-not-upsert windowed sync.
RefreshResult refreshCalendar(const Settings& s);

// Offline-first startup: last fetched meetings from calendar.json.
std::vector<Meeting> loadCalendarCache();

} // namespace mn
