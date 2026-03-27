using Serilog;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MeetNow
{
    /// <summary>
    /// Prevents the screen from locking by periodically moving the mouse and
    /// calling SetThreadExecutionState.  Runs independently of Autopilot mode.
    /// </summary>
    public static class ScreenLockPrevention
    {
        private static Timer? _keepAliveTimer;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public MOUSEINPUT mi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        private const uint INPUT_MOUSE = 0;
        private const uint MOUSEEVENTF_MOVE = 0x0001;

        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint esFlags);

        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;

        public static bool IsActive => _keepAliveTimer != null;

        public static void Start()
        {
            if (IsActive) return;

            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);

            _keepAliveTimer = new Timer(_ =>
            {
                try
                {
                    SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);

                    var inputs = new INPUT[2];
                    inputs[0].type = INPUT_MOUSE;
                    inputs[0].mi.dx = 1;
                    inputs[0].mi.dy = 0;
                    inputs[0].mi.dwFlags = MOUSEEVENTF_MOVE;

                    inputs[1].type = INPUT_MOUSE;
                    inputs[1].mi.dx = -1;
                    inputs[1].mi.dy = 0;
                    inputs[1].mi.dwFlags = MOUSEEVENTF_MOVE;

                    SendInput(2, inputs, Marshal.SizeOf<INPUT>());
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "ScreenLockPrevention: mouse move failed");
                }
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));

            Log.Information("ScreenLockPrevention: started");
        }

        public static void Stop()
        {
            _keepAliveTimer?.Dispose();
            _keepAliveTimer = null;
            SetThreadExecutionState(ES_CONTINUOUS);
            Log.Information("ScreenLockPrevention: stopped");
        }
    }
}
