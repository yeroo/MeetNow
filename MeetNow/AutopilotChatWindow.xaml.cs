using System;
using System.Windows;
using System.Windows.Input;

namespace MeetNow
{
    public partial class AutopilotChatWindow : Window
    {
        private static AutopilotChatWindow? _instance;

        public AutopilotChatWindow()
        {
            InitializeComponent();
            AutopilotAgent.LogUpdated += RefreshLog;
            Closed += (_, _) =>
            {
                AutopilotAgent.LogUpdated -= RefreshLog;
                _instance = null;
            };
            RefreshLog();
        }

        public static void ShowOrActivate()
        {
            if (_instance != null)
            {
                _instance.Activate();
                return;
            }
            _instance = new AutopilotChatWindow();
            _instance.Show();
        }

        private void RefreshLog()
        {
            Dispatcher.InvokeAsync(() =>
            {
                var entries = AutopilotAgent.GetLog();
                LogList.Items.Clear();
                foreach (var entry in entries)
                    LogList.Items.Add(entry);

                if (LogList.Items.Count > 0)
                    LogList.ScrollIntoView(LogList.Items[^1]);

                var mode = AutopilotAgent.IsActive ? "Active" : "Passive";
                var conn = AutopilotAgent.IsConnected ? "Connected" : "Not connected";
                StatusText.Text = $"{mode} — {conn}";
            });
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            var text = InputBox.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            InputBox.Text = "";
            InputBox.IsEnabled = false;

            try
            {
                await AutopilotAgent.SendChatMessageAsync(text);
            }
            finally
            {
                InputBox.IsEnabled = true;
                InputBox.Focus();
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            AutopilotAgent.ClearInstructions();
        }

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                SendButton_Click(sender, e);
                e.Handled = true;
            }
        }
    }
}
