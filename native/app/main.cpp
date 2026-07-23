#include "app_window.h"
#include <windows.h>
#include <winsock2.h>
#include <objbase.h>

using namespace mn;

// Startup (mirrors App.xaml.cs OnStartup, minus the dropped features):
//  1. single-instance mutex — same name as the C# app, so the two versions
//     also exclude each other
//  2. create the hidden main window: tray icon, overlay, keep-awake, timers
//  3. calendar refreshes arrive from a background thread via PostMessage
int WINAPI wWinMain(HINSTANCE inst, HINSTANCE, PWSTR, int) {
    HANDLE mutex = CreateMutexW(nullptr, TRUE, L"MeetNow_SingleInstance_B7A3F2");
    if (!mutex || GetLastError() == ERROR_ALREADY_EXISTS) {
        // Exit silently: a modal "already running" box leaves a second
        // MeetNow.exe lingering in Task Manager until someone clicks OK,
        // which reads as a two-instances bug during upgrades/relaunches.
        return 0;
    }

    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
    WSADATA wsa{};
    WSAStartup(MAKEWORD(2, 2), &wsa);  // the sign-in loopback listener needs Winsock

    int rc = 1;
    if (createAppWindow(inst)) rc = runMessageLoop();

    ReleaseMutex(mutex);
    CloseHandle(mutex);
    return rc;
}
