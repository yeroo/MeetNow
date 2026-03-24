using MeetNow.Models;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace MeetNow
{
    public partial class TeamsMessagePopupWindow : Window
    {
        private static TeamsMessagePopupWindow? _instance;
        private static readonly ObservableCollection<UrgentMessageItem> _messages = new();

        private static readonly string UrgentSound = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Critical Stop.wav");
        private static readonly string NormalSound = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Balloon.wav");

        private readonly DispatcherTimer _soundTimer;
        private DateTime _firstAlertTime;

        public TeamsMessagePopupWindow()
        {
            InitializeComponent();
            MessageStack.ItemsSource = _messages;

            _soundTimer = new DispatcherTimer();
            _soundTimer.Tick += OnSoundTimerTick;
            SizeChanged += OnSizeChanged;
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            RepositionToBottomRight();
        }

        private void RepositionToBottomRight()
        {
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - Width;
            // Leave room for the Autopilot OFF button when autopilot is active
            var bottomOffset = AutopilotOverlay.IsActive ? 50 : 0;
            Top = workArea.Bottom - ActualHeight - bottomOffset;
        }

        /// <summary>
        /// Show urgent message in persistent popup with escalating sound alerts.
        /// Stacks into existing window if already open.
        /// </summary>
        public static void ShowUrgentMessage(TeamsMessage message)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var item = new UrgentMessageItem(message);
                _messages.Insert(0, item); // newest on top

                if (_instance == null || !_instance.IsLoaded)
                {
                    _instance = new TeamsMessagePopupWindow();
                    _instance._firstAlertTime = DateTime.Now;

                    _instance.Show();
                    _instance.RepositionToBottomRight();
                    _instance.StartSoundTimer();
                }

                _instance.UpdateCount();
                _instance.Activate();

                // Play sound immediately for new message
                PlayUrgentSound();
            });
        }

        /// <summary>
        /// Temporarily hide the popup window during Teams automation.
        /// </summary>
        public static void HideTemporarily()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (_instance != null && _instance.IsLoaded)
                {
                    _instance.Hide();
                    _instance._soundTimer.Stop();
                }
            });
        }

        /// <summary>
        /// Restore the popup window after Teams automation completes.
        /// </summary>
        public static void RestoreIfHidden()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (_instance != null && _instance.IsLoaded && !_instance.IsVisible)
                {
                    _instance.Show();
                    _instance.StartSoundTimer();
                }
            });
        }

        /// <summary>
        /// Play normal message sound once on all devices. No popup.
        /// </summary>
        public static void PlayNormalSound()
        {
            try
            {
                SfxHelper.PlayFileOnAllDevices(NormalSound);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error playing normal message sound");
            }
        }

        private static void PlayUrgentSound()
        {
            try
            {
                SfxHelper.PlayFileOnAllDevices(UrgentSound);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error playing urgent message sound");
            }
        }

        private void StartSoundTimer()
        {
            _soundTimer.Interval = TimeSpan.FromSeconds(30);
            _soundTimer.Start();
        }

        private void OnSoundTimerTick(object? sender, EventArgs e)
        {
            var elapsed = DateTime.Now - _firstAlertTime;

            // Play the sound
            PlayUrgentSound();

            // Adjust interval based on elapsed time:
            // 0-15 min: every 30 seconds
            // 15-60 min: every 1 minute
            // 60-180 min: every 5 minutes
            // >180 min: stop
            if (elapsed.TotalMinutes >= 180)
            {
                _soundTimer.Stop();
                Log.Information("Urgent sound alerts stopped after 3 hours");
            }
            else if (elapsed.TotalMinutes >= 60)
            {
                _soundTimer.Interval = TimeSpan.FromMinutes(5);
            }
            else if (elapsed.TotalMinutes >= 15)
            {
                _soundTimer.Interval = TimeSpan.FromMinutes(1);
            }
        }

        private void UpdateCount()
        {
            MessageCountText.Text = _messages.Count > 1 ? $"{_messages.Count} messages" : "";
        }

        private void DismissClick(object sender, RoutedEventArgs e)
        {
            DismissAll();
        }

        private void DismissAll()
        {
            _soundTimer.Stop();
            _messages.Clear();
            TeamsOperationQueue.ClearQueue();
            _instance = null;
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _soundTimer.Stop();
            _instance = null;
            base.OnClosed(e);
        }
    }

    public class UrgentMessageItem
    {
        public string Sender { get; }
        public string Content { get; }
        public string TimeStr { get; }
        public string Initials { get; }

        public UrgentMessageItem(TeamsMessage msg)
        {
            Sender = msg.Sender;
            Content = msg.Content;
            TimeStr = msg.Timestamp.ToString("HH:mm");
            Initials = GetInitials(msg.Sender);
        }

        private static string GetInitials(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            var parts = name.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                return $"{parts[0][0]}{parts[1][0]}".ToUpperInvariant();
            return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
        }
    }
}
