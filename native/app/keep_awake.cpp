#include "keep_awake.h"
#include <windows.h>

namespace mn {

void keepAwakeStart() {
    SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
}

void keepAwakeTick() {
    // Re-assert every tick, exactly like ScreenLockPrevention.PreventLock().
    SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);

    // Two MOUSEEVENTF_MOVE inputs, +1 then -1 px: net zero movement but
    // enough to reset the idle timer group policy watches.
    INPUT inputs[2]{};
    inputs[0].type = INPUT_MOUSE;
    inputs[0].mi.dx = 1;
    inputs[0].mi.dwFlags = MOUSEEVENTF_MOVE;
    inputs[1].type = INPUT_MOUSE;
    inputs[1].mi.dx = -1;
    inputs[1].mi.dwFlags = MOUSEEVENTF_MOVE;
    SendInput(2, inputs, sizeof(INPUT));
}

void keepAwakeStop() {
    SetThreadExecutionState(ES_CONTINUOUS);
}

} // namespace mn
