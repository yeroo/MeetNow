using Serilog;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MeetNow
{
    /// <summary>
    /// Changes Teams presence/status using Teams slash commands.
    /// Teams supports typing /available, /busy, /away, /dnd, /brb
    /// in the search bar (Ctrl+E) to change status.
    /// No CDP, no registry, no admin, no API required.
    /// </summary>
    public static class TeamsStatusManager
    {
        [DllImport("user32.dll")]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private const uint MOUSEEVENTF_LEFTDOWN = 0x02;
        private const uint MOUSEEVENTF_LEFTUP = 0x04;

        private const int SW_RESTORE = 9;
        private const int SW_MINIMIZE = 6;
        private const byte VK_CONTROL = 0x11;
        private const byte VK_RETURN = 0x0D;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        public enum TeamsStatus
        {
            Available,
            Busy,
            Away,
            DoNotDisturb,
            BeRightBack
        }

        /// <summary>
        /// Set Teams status using slash commands in the search bar.
        /// </summary>
        public static async Task<bool> SetStatusAsync(TeamsStatus status)
        {
            var command = status switch
            {
                TeamsStatus.Available => "/available",
                TeamsStatus.Busy => "/busy",
                TeamsStatus.Away => "/away",
                TeamsStatus.DoNotDisturb => "/dnd",
                TeamsStatus.BeRightBack => "/brb",
                _ => "/available"
            };

            Log.Information("Setting Teams status: {Status} (command: {Command})", status, command);

            // Find Teams window
            var teamsHwnd = FindTeamsWindow();
            if (teamsHwnd == IntPtr.Zero)
            {
                Log.Warning("Teams window not found");
                return false;
            }

            // Remember current foreground window to restore later
            var previousForeground = GetForegroundWindow();

            try
            {
                // Bring Teams to foreground
                TeamsOperationQueue.CurrentStep = "BringToForeground";
                if (!BringToForeground(teamsHwnd))
                {
                    Log.Warning("Could not bring Teams to foreground");
                    return false;
                }

                await Task.Delay(300);

                // Click on Teams window to ensure real keyboard focus
                TeamsOperationQueue.CurrentStep = "Click to focus";
                if (GetWindowRect(teamsHwnd, out var rect))
                {
                    int cx = (rect.Left + rect.Right) / 2;
                    int cy = rect.Top + 50;
                    SetCursorPos(cx, cy);
                    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                }
                await Task.Delay(500);

                // Navigate to Chat view first (Ctrl+3) to ensure clean state
                TeamsOperationQueue.CurrentStep = "Ctrl+3 (Chat view)";
                keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                keybd_event(0x33 /* 3 */, 0, 0, UIntPtr.Zero);
                keybd_event(0x33, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                await Task.Delay(800);

                // Dismiss any open dialogs first (press Esc 3 times)
                TeamsOperationQueue.CurrentStep = "Esc x3 (dismiss dialogs)";
                for (int i = 0; i < 3; i++)
                {
                    keybd_event(0x1B /* ESC */, 0, 0, UIntPtr.Zero);
                    keybd_event(0x1B, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    await Task.Delay(200);
                }

                await Task.Delay(300);

                // Press Ctrl+E to focus search bar
                TeamsOperationQueue.CurrentStep = "Ctrl+E (search bar)";
                keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                keybd_event(0x45 /* E */, 0, 0, UIntPtr.Zero);
                keybd_event(0x45, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

                await Task.Delay(800);

                // Type the slash command
                TeamsOperationQueue.CurrentStep = $"Typing {command}";
                await TypeTextCharByChar(command);

                // Wait for Teams autocomplete dropdown to appear
                TeamsOperationQueue.CurrentStep = "Waiting for autocomplete";
                await Task.Delay(1500);

                // Press Enter to execute the slash command
                TeamsOperationQueue.CurrentStep = "Enter (execute)";
                keybd_event(VK_RETURN, 0, 0, UIntPtr.Zero);
                keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

                // Wait for Teams to process the status change
                await Task.Delay(1000);

                // Press Escape 3 times to close any remaining UI
                TeamsOperationQueue.CurrentStep = "Esc x3 (cleanup)";
                for (int i = 0; i < 3; i++)
                {
                    keybd_event(0x1B /* ESC */, 0, 0, UIntPtr.Zero);
                    keybd_event(0x1B, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    await Task.Delay(200);
                }

                TeamsOperationQueue.CurrentStep = "Done";
                Log.Information("Teams status command sent: {Command}", command);

                await Task.Delay(300);

                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error setting Teams status");
                return false;
            }
        }

        private const byte VK_DELETE = 0x2E;
        private const byte VK_TAB = 0x09;
        private const byte VK_DOWN = 0x28;

        /// <summary>
        /// Opens a 1:1 chat via Ctrl+E search, then clicks compose box.
        /// Returns true if successful.
        /// </summary>
        private static async Task<bool> OpenChatAndFocusCompose(IntPtr teamsHwnd, string searchName, string logPrefix)
        {
            // Bring Teams to foreground
            BringToForeground(teamsHwnd);
            await Task.Delay(300);

            // Click on the Teams window body to ensure keyboard focus
            if (GetWindowRect(teamsHwnd, out var rect))
            {
                int cx = (rect.Left + rect.Right) / 2;
                int cy = rect.Top + 50; // click near the top (title/toolbar area)
                Log.Information("{Prefix}: clicking Teams body at ({X},{Y}) to ensure focus", logPrefix, cx, cy);
                SetCursorPos(cx, cy);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            }
            await Task.Delay(500);
            Log.Information("{Prefix}: Teams in foreground", logPrefix);

            // Dismiss any open dialogs
            await PressEscMultiple(3);
            await Task.Delay(300);

            // Ctrl+N to open new chat
            Log.Information("{Prefix}: opening new chat (Ctrl+N)", logPrefix);
            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            keybd_event(0x4E /* N */, 0, 0, UIntPtr.Zero);
            keybd_event(0x4E, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            await Task.Delay(800);

            // Type the person's name
            Log.Information("{Prefix}: typing name '{Search}'", logPrefix, searchName);
            await TypeTextCharByChar(searchName);
            await Task.Delay(2000);

            // Press Down to highlight first result, then Enter to open chat
            Log.Information("{Prefix}: selecting search result", logPrefix);
            keybd_event(VK_DOWN, 0, 0, UIntPtr.Zero);
            keybd_event(VK_DOWN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            await Task.Delay(300);
            keybd_event(VK_RETURN, 0, 0, UIntPtr.Zero);
            keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            await Task.Delay(2000);

            // Click compose box area (bottom of main pane)
            Log.Information("{Prefix}: clicking compose box", logPrefix);
            ClickComposeBox(teamsHwnd);
            await Task.Delay(500);

            return true;
        }

        /// <summary>
        /// Opens the 1:1 chat with the sender, types "Hi", waits (typing indicator shows),
        /// then clears the text. Does not send anything.
        /// </summary>
        public static async Task<bool> SimulateTypingAsync(string senderName)
        {
            var searchName = ExtractSearchName(senderName);
            Log.Information("SimulateTyping: start for '{Name}' (search: '{Search}')", senderName, searchName);

            var teamsHwnd = FindTeamsWindow();
            if (teamsHwnd == IntPtr.Zero)
            {
                Log.Warning("SimulateTyping: Teams window not found");
                return false;
            }

            try
            {
                await OpenChatAndFocusCompose(teamsHwnd, searchName, "SimulateTyping");

                // Type "Hi" — sender sees "is typing..."
                var typingDuration = MeetNowSettings.Instance.SimulateTypingDurationSeconds * 1000;
                Log.Information("SimulateTyping: typing 'Hi', holding for {Duration}s", typingDuration / 1000);
                await TypeTextCharByChar("Hi");
                await Task.Delay(typingDuration);

                // Clear: Ctrl+A then Delete
                Log.Information("SimulateTyping: clearing text");
                keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                keybd_event(0x41 /* A */, 0, 0, UIntPtr.Zero);
                keybd_event(0x41, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                await Task.Delay(100);
                keybd_event(VK_DELETE, 0, 0, UIntPtr.Zero);
                keybd_event(VK_DELETE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                await Task.Delay(300);

                await PressEscMultiple(3);

                Log.Information("SimulateTyping: complete for '{Name}'", senderName);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "SimulateTyping: error for '{Name}'", senderName);
                return false;
            }
        }

        /// <summary>
        /// Opens the 1:1 chat and actually sends a message.
        /// </summary>
        public static async Task<bool> SendMessageAsync(string senderName, string message)
        {
            var searchName = ExtractSearchName(senderName);
            Log.Information("SendMessage: start for '{Name}' (search: '{Search}'), message: '{Message}'",
                senderName, searchName, message);

            var teamsHwnd = FindTeamsWindow();
            if (teamsHwnd == IntPtr.Zero)
            {
                Log.Warning("SendMessage: Teams window not found");
                return false;
            }

            try
            {
                await OpenChatAndFocusCompose(teamsHwnd, searchName, "SendMessage");

                // Type the message char by char
                await TypeTextCharByChar(message);
                await Task.Delay(500);

                // Press Enter to SEND
                keybd_event(VK_RETURN, 0, 0, UIntPtr.Zero);
                keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                await Task.Delay(1000);

                await PressEscMultiple(3);

                Log.Information("SendMessage: sent '{Message}' to '{Name}'", message, senderName);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "SendMessage: error for '{Name}'", senderName);
                return false;
            }
        }

        /// <summary>
        /// Extracts a searchable name from sender format like "Doe, John TESTORG/IT"
        /// </summary>
        private static string ExtractSearchName(string sender)
        {
            // Remove org/dept suffix (after /)
            var slashIdx = sender.IndexOf('/');
            var name = slashIdx >= 0 ? sender[..slashIdx].Trim() : sender.Trim();

            // Handle "Last, First ORG" format
            var commaIdx = name.IndexOf(',');
            if (commaIdx >= 0)
            {
                var last = name[..commaIdx].Trim();
                var rest = name[(commaIdx + 1)..].Trim();
                // First word after comma is the first name, rest might be org
                var firstParts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (firstParts.Length > 0)
                    return $"{firstParts[0]} {last}";
            }

            return name;
        }

        /// <summary>
        /// Types text char by char with proper SendKeys escaping.
        /// Special chars like +^%~(){}[] are escaped so they're typed literally.
        /// </summary>
        private static async Task TypeTextCharByChar(string text, int delayPerCharMs = 30)
        {
            foreach (var c in text)
            {
                var escaped = c switch
                {
                    '+' or '^' or '%' or '~' or '(' or ')' or '{' or '}' or '[' or ']' => $"{{{c}}}",
                    _ => c.ToString()
                };
                System.Windows.Forms.SendKeys.SendWait(escaped);
                await Task.Delay(delayPerCharMs);
            }
        }

        /// <summary>
        /// Clicks in the Teams compose box area (bottom center of the window).
        /// </summary>
        private static void ClickComposeBox(IntPtr teamsHwnd)
        {
            if (GetWindowRect(teamsHwnd, out var rect))
            {
                int width = rect.Right - rect.Left;
                int x = rect.Left + (int)(width * 0.65); // past the left sidebar
                int y = rect.Bottom - 50; // compose box is near the very bottom
                Log.Information("ClickComposeBox: window=({L},{T},{R},{B}), clicking at ({X}, {Y})",
                    rect.Left, rect.Top, rect.Right, rect.Bottom, x, y);
                SetCursorPos(x, y);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            }
        }

        private static async Task PressEscMultiple(int count)
        {
            for (int i = 0; i < count; i++)
            {
                keybd_event(0x1B, 0, 0, UIntPtr.Zero);
                keybd_event(0x1B, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                await Task.Delay(200);
            }
        }

        private static IntPtr FindTeamsWindow()
        {
            var hwnd = FindTeamsWindowCore();
            if (hwnd != IntPtr.Zero)
                return hwnd;

            // Teams window not found — try launching Teams and wait for its window
            Log.Information("FindTeamsWindow: no window found, launching ms-teams:");
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-teams:",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "FindTeamsWindow: failed to launch ms-teams:");
                return IntPtr.Zero;
            }

            // Poll for the window to appear (up to 15 seconds)
            for (int i = 0; i < 30; i++)
            {
                Thread.Sleep(500);
                hwnd = FindTeamsWindowCore();
                if (hwnd != IntPtr.Zero)
                {
                    Log.Information("FindTeamsWindow: Teams window appeared after {Ms}ms", (i + 1) * 500);
                    return hwnd;
                }
            }

            Log.Warning("FindTeamsWindow: Teams window did not appear after launch");
            return IntPtr.Zero;
        }

        private static IntPtr FindTeamsWindowCore()
        {
            // Find by process name and window enumeration
            var processes = Process.GetProcessesByName("ms-teams");
            foreach (var proc in processes)
            {
                if (proc.MainWindowHandle != IntPtr.Zero)
                {
                    Log.Information("Found Teams window: PID={Pid}, Title={Title}",
                        proc.Id, proc.MainWindowTitle);
                    return proc.MainWindowHandle;
                }
            }

            // Fallback: try "Teams" process name
            processes = Process.GetProcessesByName("Teams");
            foreach (var proc in processes)
            {
                if (proc.MainWindowHandle != IntPtr.Zero)
                    return proc.MainWindowHandle;
            }

            return IntPtr.Zero;
        }

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        private static bool BringToForeground(IntPtr hWnd)
        {
            // Get the target process ID for AllowSetForegroundWindow
            GetWindowThreadProcessId(hWnd, out var targetPid);
            AllowSetForegroundWindow((int)targetPid);

            // Restore if minimized
            ShowWindow(hWnd, SW_RESTORE);

            // Attach to both foreground and target threads
            var foregroundHwnd = GetForegroundWindow();
            var foregroundThread = GetWindowThreadProcessId(foregroundHwnd, out _);
            var targetThread = GetWindowThreadProcessId(hWnd, out _);
            var currentThread = GetCurrentThreadId();

            bool attached1 = false, attached2 = false;
            try
            {
                if (foregroundThread != currentThread)
                {
                    attached1 = AttachThreadInput(currentThread, foregroundThread, true);
                }
                if (targetThread != currentThread && targetThread != foregroundThread)
                {
                    attached2 = AttachThreadInput(currentThread, targetThread, true);
                }

                SetForegroundWindow(hWnd);
                SetFocus(hWnd);
            }
            finally
            {
                if (attached1) AttachThreadInput(currentThread, foregroundThread, false);
                if (attached2) AttachThreadInput(currentThread, targetThread, false);
            }

            var success = GetForegroundWindow() == hWnd;
            Log.Information("BringToForeground: {Result}, hWnd={HWnd}", success ? "OK" : "FAILED", hWnd);
            return success;
        }
    }
}
