#pragma once

namespace mn {

// Mirrors ScreenLockPrevention.cs: active for the app's whole lifetime.
// start() asserts the execution state once; tick() (call every ~60 s)
// re-asserts it and sends the net-zero mouse jiggle that defeats
// idle-based lock policies which ignore execution state.
void keepAwakeStart();
void keepAwakeTick();
void keepAwakeStop();

} // namespace mn
