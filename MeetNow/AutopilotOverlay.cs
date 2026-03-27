using Serilog;
using System;
using System.Runtime.InteropServices;
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

            Log.Information("Autopilot mode enabled (UI overlay shown)");
        }

        public static void Disable()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                _borderWindow?.Close();
                _borderWindow = null;
                _buttonWindow?.Close();
                _buttonWindow = null;
            });

            Log.Information("Autopilot mode disabled (UI overlay hidden)");
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
