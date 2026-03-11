using MeetNow.Models;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace MeetNow
{
    public partial class TeamsMessagePopupWindow : Window
    {
        private readonly DispatcherTimer _autoCloseTimer;

        public TeamsMessagePopupWindow()
        {
            InitializeComponent();

            _autoCloseTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(15)
            };
            _autoCloseTimer.Tick += (_, _) => Close();
        }

        public static void ShowMessage(TeamsMessage message)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var popup = new TeamsMessagePopupWindow();
                popup.SetMessage(message);

                // Position in bottom-right corner of primary screen
                var workArea = SystemParameters.WorkArea;
                popup.Left = workArea.Right - popup.Width;
                popup.Top = workArea.Bottom - popup.Height;

                popup.Show();
                popup._autoCloseTimer.Start();
            });
        }

        private void SetMessage(TeamsMessage message)
        {
            SenderName.Text = message.Sender;
            SenderInitials.Text = GetInitials(message.Sender);
            MessageContent.Text = message.Content;
            ChatType.Text = message.IsDirectChat ? "Direct message" : "Channel message";
            TimeText.Text = message.Timestamp.ToString("HH:mm");

            switch (message.Urgency)
            {
                case MessageUrgency.Urgent:
                    UrgencyBadge.Background = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38));
                    UrgencyText.Text = "URGENT";
                    _autoCloseTimer.Interval = TimeSpan.FromSeconds(30);
                    break;
                case MessageUrgency.Normal:
                    UrgencyBadge.Background = new SolidColorBrush(Color.FromRgb(0x4F, 0x52, 0xB2));
                    UrgencyText.Text = "NORMAL";
                    _autoCloseTimer.Interval = TimeSpan.FromSeconds(15);
                    break;
                case MessageUrgency.Low:
                    UrgencyBadge.Background = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
                    UrgencyText.Text = "LOW";
                    _autoCloseTimer.Interval = TimeSpan.FromSeconds(8);
                    break;
            }

            UrgencyReason.Text = message.UrgencyReason;

            if (message.IsMention)
            {
                SenderInitials.Foreground = new SolidColorBrush(Colors.Yellow);
            }
        }

        private static string GetInitials(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";

            // Handle "LastName, FirstName ..." format
            var parts = name.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                return $"{parts[0][0]}{parts[1][0]}".ToUpperInvariant();
            if (parts.Length == 1 && parts[0].Length > 0)
                return parts[0][0].ToString().ToUpperInvariant();
            return "?";
        }

        private void DismissClick(object sender, RoutedEventArgs e)
        {
            _autoCloseTimer.Stop();
            Close();
        }
    }
}
