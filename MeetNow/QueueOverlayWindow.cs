using Serilog;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace MeetNow
{
    public static class QueueOverlay
    {
        private static QueueOverlayWindow? _window;
        private static bool _popupsHidden;

        public static void Initialize()
        {
            TeamsOperationQueue.QueueChanged += OnQueueChanged;
        }

        private static void OnQueueChanged()
        {
            Application.Current?.Dispatcher.BeginInvoke(UpdateOverlay);
        }

        private static void UpdateOverlay()
        {
            try
            {
                // Hide/show urgent popups during Teams automation
                if (TeamsOperationQueue.IsExecuting && !_popupsHidden)
                {
                    TeamsMessagePopupWindow.HideTemporarily();
                    _popupsHidden = true;
                }
                else if (!TeamsOperationQueue.IsExecuting && _popupsHidden)
                {
                    TeamsMessagePopupWindow.RestoreIfHidden();
                    _popupsHidden = false;
                }
                var current = TeamsOperationQueue.Current;
                var pending = TeamsOperationQueue.PendingSnapshot;
                var autoReplies = AutopilotOverlay.IsActive
                    ? AutopilotOverlay.GetPendingAutoReplies()
                    : new Dictionary<string, DateTime>();

                bool hasContent = current != null || pending.Count > 0 || autoReplies.Count > 0;

                if (!hasContent)
                {
                    _window?.Close();
                    _window = null;
                    return;
                }

                if (_window == null)
                {
                    _window = new QueueOverlayWindow();
                    _window.Closed += (_, _) => _window = null;
                    _window.Show();
                }

                _window.Update(current, pending, autoReplies);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "QueueOverlay: error updating overlay");
            }
        }

        private class QueueOverlayWindow : Window
        {
            [DllImport("user32.dll")]
            private static extern int GetWindowLong(IntPtr hwnd, int index);

            [DllImport("user32.dll")]
            private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

            private const int GWL_EXSTYLE = -20;
            private const int WS_EX_TRANSPARENT = 0x20;
            private const int WS_EX_TOOLWINDOW = 0x80;
            private const int WS_EX_NOACTIVATE = 0x08000000;

            private readonly StackPanel _panel;
            private readonly DispatcherTimer _timer;

            private TeamsOperationQueue.QueueEntry? _current;
            private List<TeamsOperationQueue.QueueEntry> _pending = new();
            private Dictionary<string, DateTime> _autoReplies = new();

            public QueueOverlayWindow()
            {
                WindowStyle = WindowStyle.None;
                AllowsTransparency = true;
                Background = Brushes.Transparent;
                Topmost = true;
                ShowInTaskbar = false;
                ShowActivated = false;
                Focusable = false;
                ResizeMode = ResizeMode.NoResize;
                SizeToContent = SizeToContent.WidthAndHeight;
                MaxWidth = 350;

                var border = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(180, 30, 30, 30)),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0)
                };

                _panel = new StackPanel();
                border.Child = _panel;
                Content = border;

                Loaded += (_, _) => PositionWindow();

                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _timer.Tick += (_, _) => Render();
                _timer.Start();
            }

            protected override void OnSourceInitialized(EventArgs e)
            {
                base.OnSourceInitialized(e);
                var hwnd = new WindowInteropHelper(this).Handle;
                var style = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            }

            protected override void OnClosed(EventArgs e)
            {
                _timer.Stop();
                base.OnClosed(e);
            }

            private void PositionWindow()
            {
                Left = SystemParameters.WorkArea.Right - ActualWidth - 12;
                Top = SystemParameters.WorkArea.Top + 12;
            }

            public void Update(
                TeamsOperationQueue.QueueEntry? current,
                List<TeamsOperationQueue.QueueEntry> pending,
                Dictionary<string, DateTime> autoReplies)
            {
                _current = current;
                _pending = pending;
                _autoReplies = autoReplies;
                Render();
            }

            private void Render()
            {
                _panel.Children.Clear();

                bool isExecuting = TeamsOperationQueue.IsExecuting;

                // Big warning banner while Teams automation is running
                if (isExecuting)
                {
                    _panel.Children.Add(MakeText("TEAMS AUTOMATION",
                        16, FontWeights.Bold,
                        new SolidColorBrush(Color.FromRgb(255, 200, 60))));
                    _panel.Children.Add(MakeText("Wait till complete...",
                        14, FontWeights.SemiBold,
                        new SolidColorBrush(Color.FromRgb(255, 200, 60))));
                }
                else
                {
                    // Header
                    _panel.Children.Add(MakeText("QUEUE", 11, FontWeights.SemiBold,
                        new SolidColorBrush(Color.FromArgb(160, 255, 255, 255))));
                }

                // Current operation
                if (_current != null)
                {
                    var elapsed = DateTime.Now - _current.EnqueuedAt;
                    _panel.Children.Add(MakeText(
                        $"> {_current.Description}  ({elapsed.TotalSeconds:F0}s)",
                        12, FontWeights.Normal,
                        new SolidColorBrush(Color.FromRgb(78, 201, 176))));

                    var step = TeamsOperationQueue.CurrentStep;
                    if (!string.IsNullOrEmpty(step))
                    {
                        _panel.Children.Add(MakeText(
                            $"  [{step}]",
                            11, FontWeights.Normal,
                            new SolidColorBrush(Color.FromRgb(86, 156, 214))));
                    }
                }

                // Pending items with ETA
                var delayPerOp = MeetNowSettings.Instance.TeamsOperationDelaySeconds + 15; // avg 15s per op + delay
                for (int i = 0; i < _pending.Count; i++)
                {
                    var entry = _pending[i];
                    var etaSeconds = (i + 1) * delayPerOp;
                    _panel.Children.Add(MakeText(
                        $"  {entry.Description}  (~{etaSeconds}s)",
                        12, FontWeights.Normal,
                        new SolidColorBrush(Color.FromArgb(200, 200, 200, 200))));
                }

                // Auto-replies with countdown
                foreach (var kvp in _autoReplies)
                {
                    var remaining = kvp.Value - DateTime.Now;
                    if (remaining.TotalSeconds < 0) remaining = TimeSpan.Zero;
                    var mins = (int)remaining.TotalMinutes;
                    var secs = remaining.Seconds;
                    _panel.Children.Add(MakeText(
                        $"  Reply to {kvp.Key}  {mins}:{secs:D2}",
                        12, FontWeights.Normal,
                        new SolidColorBrush(Color.FromRgb(255, 185, 80))));
                }

                // Reposition after content change
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, PositionWindow);
            }

            private static TextBlock MakeText(string text, double size, FontWeight weight, Brush foreground)
            {
                return new TextBlock
                {
                    Text = text,
                    FontSize = size,
                    FontWeight = weight,
                    Foreground = foreground,
                    FontFamily = new FontFamily("Segoe UI"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 1, 0, 1)
                };
            }
        }
    }
}
