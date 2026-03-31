using Serilog;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace MeetNow
{
    public static class MeetingCountdownOverlay
    {
        private static MeetingCountdownWindow? _window;
        private static DispatcherTimer? _timer;
        private static MeetingDataAggregator? _aggregator;

        public static void Initialize(MeetingDataAggregator aggregator)
        {
            _aggregator = aggregator;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _timer.Tick += (_, _) => Update();
            _timer.Start();

            // Initial check
            Update();
        }

        public static void Update()
        {
            try
            {
                if (_aggregator == null) return;

                var now = DateTime.Now;
                var meetings = _aggregator.GetMeetings(
                    now, MeetNowSettings.Instance.OutlookSource);

                // Filter: starts within 2 hours, hasn't ended
                var upcoming = meetings
                    .Where(m => m.Start > now && m.Start <= now.AddHours(2))
                    .OrderBy(m => m.Start)
                    .ToArray();

                if (upcoming.Length == 0)
                {
                    _window?.Close();
                    _window = null;
                    return;
                }

                if (_window == null)
                {
                    _window = new MeetingCountdownWindow();
                    _window.Closed += (_, _) => _window = null;
                    _window.Show();
                }

                _window.UpdateMeetings(upcoming);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "MeetingCountdownOverlay: error updating");
            }
        }

        public static bool IsVisible => _window is { IsVisible: true };

        private class MeetingCountdownWindow : Window
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
            private readonly DispatcherTimer _countdownTimer;
            private TeamsMeeting[] _meetings = [];

            public MeetingCountdownWindow()
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
                };

                _panel = new StackPanel();
                border.Child = _panel;
                Content = border;

                Loaded += (_, _) => PositionWindow();

                // Update countdown every 30 seconds
                _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                _countdownTimer.Tick += (_, _) => Render();
                _countdownTimer.Start();
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
                _countdownTimer.Stop();
                base.OnClosed(e);
            }

            private void PositionWindow()
            {
                // Position top-right, below QueueOverlay if visible
                double topOffset = SystemParameters.WorkArea.Top + 12;

                // Stack below QueueOverlay if it's showing
                if (QueueOverlay.WindowHeight > 0)
                    topOffset += QueueOverlay.WindowHeight + 8;

                Left = SystemParameters.WorkArea.Right - ActualWidth - 12;
                Top = topOffset;
            }

            public void UpdateMeetings(TeamsMeeting[] meetings)
            {
                _meetings = meetings;
                Render();
            }

            private void Render()
            {
                _panel.Children.Clear();
                var now = DateTime.Now;

                for (int i = 0; i < _meetings.Length; i++)
                {
                    var meeting = _meetings[i];
                    var remaining = meeting.Start - now;
                    if (remaining.TotalSeconds < 0) continue;

                    bool isNext = i == 0;

                    // Row: "14:00  Sprint Planning          in 1:43"
                    var row = new Grid();
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    // Time
                    var timeText = new TextBlock
                    {
                        Text = meeting.Start.ToString("HH:mm"),
                        FontSize = 12,
                        FontWeight = isNext ? FontWeights.Bold : FontWeights.Normal,
                        Foreground = new SolidColorBrush(
                            isNext ? Color.FromRgb(86, 156, 214) : Color.FromRgb(140, 140, 140)),
                        FontFamily = new FontFamily("Segoe UI"),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    Grid.SetColumn(timeText, 0);
                    row.Children.Add(timeText);

                    // Subject
                    var subjectText = new TextBlock
                    {
                        Text = meeting.Subject ?? "(no subject)",
                        FontSize = 12,
                        FontWeight = isNext ? FontWeights.SemiBold : FontWeights.Normal,
                        Foreground = new SolidColorBrush(
                            isNext ? Color.FromRgb(255, 255, 255) : Color.FromRgb(180, 180, 180)),
                        FontFamily = new FontFamily("Segoe UI"),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(4, 0, 8, 0),
                    };
                    Grid.SetColumn(subjectText, 1);
                    row.Children.Add(subjectText);

                    // Countdown
                    var countdownStr = remaining.TotalMinutes >= 60
                        ? $"in {(int)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}"
                        : $"in {(int)remaining.TotalMinutes}:{remaining.Seconds:D2}";

                    var countdownColor = remaining.TotalMinutes <= 5
                        ? Color.FromRgb(255, 100, 100)   // red when < 5 min
                        : remaining.TotalMinutes <= 15
                            ? Color.FromRgb(255, 200, 60) // yellow when < 15 min
                            : Color.FromRgb(120, 120, 120); // gray otherwise

                    var countdownText = new TextBlock
                    {
                        Text = countdownStr,
                        FontSize = 11,
                        FontWeight = isNext ? FontWeights.SemiBold : FontWeights.Normal,
                        Foreground = new SolidColorBrush(countdownColor),
                        FontFamily = new FontFamily("Segoe UI"),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    Grid.SetColumn(countdownText, 2);
                    row.Children.Add(countdownText);

                    _panel.Children.Add(row);
                }

                // Reposition after content change
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, PositionWindow);
            }
        }
    }

}
