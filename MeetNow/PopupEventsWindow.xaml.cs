using Serilog;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace MeetNow
{
    public partial class PopupEventsWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_TOOLWINDOW = 0x80;

        private static readonly List<PopupEventsWindow> _windows = new();
        private static DispatcherTimer? _soundTimer;
        private TeamsMeeting[]? _meetings;

        public PopupEventsWindow()
        {
            InitializeComponent();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            // Make the window click-through (the meeting panel handles its own hit-testing)
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
        }

        public static void Show(TeamsMeeting[] events)
        {
            CloseAllWindows();

            if (events.Length == 0) return;

            // Create the transparent overlay on the primary screen
            var window = new PopupEventsWindow();
            window._meetings = events;
            _windows.Add(window);
            window.Show();

            // Create the meeting panel as a separate non-transparent window
            var panel = new MeetingPanelWindow(events);
            panel.Show();

            // Start repeating sound
            StartRepeatingSound();
            VolumeMonitor.Start();
        }

        public static void JoinFirstMeeting()
        {
            var meetings = GetCurrentMeetings();
            if (meetings != null && meetings.Length > 0 && !string.IsNullOrEmpty(meetings[0].TeamsUrl))
            {
                OutlookHelper.StartTeamsMeeting(meetings[0].TeamsUrl);
            }
            CloseAllWindows();
        }

        public static TeamsMeeting[]? GetCurrentMeetings()
        {
            return _windows.Count > 0 ? _windows[0]._meetings : null;
        }

        public static void CloseAllWindows()
        {
            VolumeMonitor.Stop();
            StopRepeatingSound();
            SfxHelper.StopAllDevices();
            MeetingPanelWindow.CloseAll();
            foreach (var w in _windows) w.Close();
            _windows.Clear();
        }

        private static void StartRepeatingSound()
        {
            StopRepeatingSound();
            // Play immediately
            SfxHelper.PlayOnAllDevices();
            // Repeat every 10 seconds
            _soundTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _soundTimer.Tick += (_, _) =>
            {
                SfxHelper.StopAllDevices();
                SfxHelper.PlayOnAllDevices();
            };
            _soundTimer.Start();
        }

        private static void StopRepeatingSound()
        {
            _soundTimer?.Stop();
            _soundTimer = null;
        }
    }

    /// <summary>
    /// Non-transparent panel showing meeting buttons. Positioned bottom-right.
    /// </summary>
    internal class MeetingPanelWindow : Window
    {
        private static readonly List<MeetingPanelWindow> _panels = new();

        public MeetingPanelWindow(TeamsMeeting[] meetings)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;

            var panel = new StackPanel { Margin = new Thickness(8) };

            // Header
            var header = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(230, 30, 30, 30)),
                CornerRadius = new CornerRadius(8, 8, 0, 0),
                Padding = new Thickness(16, 10, 16, 10),
                Child = new DockPanel
                {
                    Children =
                    {
                        CreateDismissButton(),
                        new TextBlock
                        {
                            Text = "Upcoming Meetings",
                            Foreground = Brushes.White,
                            FontSize = 16,
                            FontWeight = FontWeights.SemiBold,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    }
                }
            };
            DockPanel.SetDock((header.Child as DockPanel)!.Children[0], Dock.Right);
            panel.Children.Add(header);

            // Meeting buttons
            foreach (var meeting in meetings)
            {
                panel.Children.Add(CreateMeetingRow(meeting));
            }

            Content = panel;

            Loaded += (_, _) =>
            {
                Left = SystemParameters.WorkArea.Right - ActualWidth - 20;
                Top = SystemParameters.WorkArea.Bottom - ActualHeight - 20;
            };

            _panels.Add(this);
        }

        private UIElement CreateDismissButton()
        {
            var btn = new Button
            {
                Content = "Dismiss",
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x88, 0x88)),
                Background = new SolidColorBrush(Color.FromRgb(0x4D, 0x20, 0x20)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            btn.Click += (_, _) => PopupEventsWindow.CloseAllWindows();
            return btn;
        }

        private UIElement CreateMeetingRow(TeamsMeeting meeting)
        {
            bool hasUrl = !string.IsNullOrEmpty(meeting.TeamsUrl);

            var timeText = new TextBlock
            {
                Text = $"{meeting.Start:HH:mm}",
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0xBB, 0xFF)),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 50
            };

            var subjectText = new TextBlock
            {
                Text = meeting.Subject,
                Foreground = Brushes.White,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 300
            };

            var contentPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { timeText, subjectText }
            };

            if (hasUrl)
            {
                var joinBtn = new Button
                {
                    Content = "Join",
                    Padding = new Thickness(12, 4, 12, 4),
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    Background = new SolidColorBrush(Color.FromRgb(0x40, 0x80, 0x40)),
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = meeting.TeamsUrl
                };
                joinBtn.Click += (s, _) =>
                {
                    if (s is Button b && b.Tag is string url)
                    {
                        OutlookHelper.StartTeamsMeeting(url);
                        PopupEventsWindow.CloseAllWindows();
                    }
                };
                contentPanel.Children.Add(joinBtn);
            }

            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(230, 40, 40, 40)),
                Padding = new Thickness(16, 10, 16, 10),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = contentPanel
            };
        }

        public static void CloseAll()
        {
            foreach (var p in _panels) p.Close();
            _panels.Clear();
        }
    }
}
