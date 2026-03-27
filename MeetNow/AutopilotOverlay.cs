using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace MeetNow
{
    public static class AutopilotOverlay
    {
        private static BorderOverlayWindow? _borderWindow;
        private static ButtonOverlayWindow? _buttonWindow;
        private static Timer? _autoOffTimer;
        private static Timer? _keepAliveTimer;

        // SendInput P/Invoke for hardware-level mouse movement (bypasses IT lock screen policies)
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

        // Also use SetThreadExecutionState as a secondary measure
        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint esFlags);

        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;

        // Track urgent message counts per sender and pending auto-replies
        private static readonly ConcurrentDictionary<string, int> _urgentMessageCounts = new();

        private record PendingReply(CancellationTokenSource Cts, DateTime ScheduledTime);
        private static readonly ConcurrentDictionary<string, PendingReply> _pendingReplies = new();


        public static bool IsActive => _borderWindow != null;

        public static void Toggle()
        {
            if (IsActive)
                Disable();
            else
                Enable();
        }

        public static void Enable()
        {
            if (IsActive) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                _borderWindow = new BorderOverlayWindow();
                _borderWindow.Show();

                _buttonWindow = new ButtonOverlayWindow();
                _buttonWindow.DisableClicked += Disable;
                _buttonWindow.Show();
            });

            StartAutoOffTimer();
            StartKeepAlive();
            Log.Information("Autopilot mode enabled (screen lock prevention active)");
            TeamsOperationQueue.Enqueue("Set Teams Busy",
                () => TeamsStatusManager.SetStatusAsync(TeamsStatusManager.TeamsStatus.Busy));
        }

        public static void Disable()
        {
            // Cancel all pending auto-replies
            foreach (var reply in _pendingReplies.Values)
            {
                reply.Cts.Cancel();
            }
            _pendingReplies.Clear();
            _urgentMessageCounts.Clear();

            Application.Current.Dispatcher.Invoke(() =>
            {
                _borderWindow?.Close();
                _borderWindow = null;
                _buttonWindow?.Close();
                _buttonWindow = null;
            });

            _autoOffTimer?.Dispose();
            _autoOffTimer = null;

            StopKeepAlive();

            TeamsOperationQueue.ClearQueue();
            TeamsOperationQueue.ResetCooldowns();
            Log.Information("Autopilot mode disabled, cancelled all pending auto-replies, queue cleared");
            TeamsOperationQueue.Enqueue("Set Teams Available",
                () => TeamsStatusManager.SetStatusAsync(TeamsStatusManager.TeamsStatus.Available));
        }

        private static void StartKeepAlive()
        {
            // Set execution state to prevent sleep
            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);

            // Move mouse by 1px every 60s to simulate real user activity
            _keepAliveTimer = new Timer(_ =>
            {
                try
                {
                    // Refresh execution state each tick
                    SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);

                    // SendInput mouse move: 1px right, then 1px left
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
                    Log.Debug(ex, "KeepAlive: mouse move failed");
                }
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));

            Log.Information("KeepAlive: started (SendInput mouse + SetThreadExecutionState)");
        }

        private static void StopKeepAlive()
        {
            _keepAliveTimer?.Dispose();
            _keepAliveTimer = null;

            // Clear execution state
            SetThreadExecutionState(ES_CONTINUOUS);

            Log.Information("KeepAlive: stopped");
        }

        /// <summary>
        /// Track an urgent 1:1 message. If sender exceeds threshold, schedule auto-reply.
        /// </summary>
        public static void TrackUrgentMessage(string sender)
        {
            if (!IsActive) return;

            var settings = MeetNowSettings.Instance;
            var count = _urgentMessageCounts.AddOrUpdate(sender, 1, (_, c) => c + 1);
            Log.Information("Autopilot: urgent message #{Count} from {Sender}", count, sender);

            if (count >= settings.AutoReplyMessageThreshold && !_pendingReplies.ContainsKey(sender))
            {
                var delay = TimeSpan.FromMinutes(settings.AutoReplyDelayMinutes);
                Log.Information("Autopilot: scheduling auto-reply to {Sender} in {Delay} minutes",
                    sender, delay.TotalMinutes);

                var cts = new CancellationTokenSource();
                _pendingReplies[sender] = new PendingReply(cts, DateTime.Now + delay);

                _ = ScheduleAutoReplyAsync(sender, delay, cts.Token);
            }
        }

        public static Dictionary<string, DateTime> GetPendingAutoReplies()
        {
            var result = new Dictionary<string, DateTime>();
            foreach (var kvp in _pendingReplies)
                result[kvp.Key] = kvp.Value.ScheduledTime;
            return result;
        }

        private static async Task ScheduleAutoReplyAsync(string sender, TimeSpan delay, CancellationToken ct)
        {
            try
            {
                await Task.Delay(delay, ct);

                if (ct.IsCancellationRequested || !IsActive)
                {
                    Log.Information("Autopilot: auto-reply to {Sender} cancelled (autopilot disabled)", sender);
                    return;
                }

                Log.Information("Autopilot: queuing auto-reply 'Hi' to {Sender}", sender);
                TeamsOperationQueue.Enqueue($"Auto-reply Hi to {sender}",
                    () => TeamsStatusManager.SendMessageAsync(sender, "Hi"));

                _pendingReplies.TryRemove(sender, out _);
            }
            catch (TaskCanceledException)
            {
                Log.Information("Autopilot: auto-reply to {Sender} cancelled", sender);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Autopilot: error in auto-reply to {Sender}", sender);
            }
        }

        private static void StartAutoOffTimer()
        {
            _autoOffTimer?.Dispose();
            _autoOffTimer = new Timer(_ =>
            {
                if (!IsActive) return;

                if (TimeSpan.TryParse(MeetNowSettings.Instance.AutopilotOffTime, out var offTime)
                    && DateTime.Now.TimeOfDay >= offTime)
                {
                    Log.Information("Autopilot auto-off: reached {Time}, disabling", offTime);
                    Application.Current.Dispatcher.Invoke(Disable);
                }
            }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        /// <summary>
        /// Full-screen transparent click-through window that draws a red border around the virtual screen.
        /// </summary>
        private class BorderOverlayWindow : Window
        {
            [DllImport("user32.dll")]
            private static extern int GetWindowLong(IntPtr hwnd, int index);

            [DllImport("user32.dll")]
            private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

            private const int GWL_EXSTYLE = -20;
            private const int WS_EX_TRANSPARENT = 0x20;
            private const int WS_EX_TOOLWINDOW = 0x80;

            public BorderOverlayWindow()
            {
                WindowStyle = WindowStyle.None;
                AllowsTransparency = true;
                Background = Brushes.Transparent;
                Topmost = true;
                ShowInTaskbar = false;
                ResizeMode = ResizeMode.NoResize;

                Left = SystemParameters.VirtualScreenLeft;
                Top = SystemParameters.VirtualScreenTop;
                Width = SystemParameters.VirtualScreenWidth;
                Height = SystemParameters.VirtualScreenHeight;

                Content = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromArgb(180, 255, 0, 0)),
                    BorderThickness = new Thickness(8),
                    Background = Brushes.Transparent
                };
            }

            protected override void OnSourceInitialized(EventArgs e)
            {
                base.OnSourceInitialized(e);
                var hwnd = new WindowInteropHelper(this).Handle;
                var style = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
            }
        }

        /// <summary>
        /// Small clickable button in bottom-right corner to disable autopilot.
        /// </summary>
        private class ButtonOverlayWindow : Window
        {
            public event Action? DisableClicked;

            public ButtonOverlayWindow()
            {
                WindowStyle = WindowStyle.None;
                AllowsTransparency = true;
                Background = Brushes.Transparent;
                Topmost = true;
                ShowInTaskbar = false;
                ResizeMode = ResizeMode.NoResize;
                SizeToContent = SizeToContent.WidthAndHeight;

                var button = new Button
                {
                    Content = "Autopilot OFF",
                    Padding = new Thickness(12, 6, 12, 6),
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White,
                    Background = new SolidColorBrush(Color.FromArgb(210, 180, 0, 0)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(220, 255, 60, 60)),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand
                };
                button.Click += (_, _) => DisableClicked?.Invoke();

                Content = button;

                Loaded += (_, _) =>
                {
                    Left = SystemParameters.WorkArea.Right - ActualWidth - 16;
                    Top = SystemParameters.WorkArea.Bottom - ActualHeight - 16;
                };
            }
        }
    }
}
