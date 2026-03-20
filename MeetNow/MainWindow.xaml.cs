using FluentScheduler;
using MeetNow.Models;
using Microsoft.Win32;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;


namespace MeetNow
{

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        const int OUTLOOK_TIMER_INTERVAL_MINUTES = 15;
        private Timer _timer = null!;
        private TeamsMessageMonitor? _teamsMonitor;
        private NotificationListenerMonitor? _notificationMonitor;

        internal MainWindowModel Model
        {
            get => (DataContext as MainWindowModel)!;
            private set => DataContext = value;
        }

        readonly List<Control> _scheduledPopupMenuItems = new();
        public MainWindow()
        {
            Model = new MainWindowModel();
            JobManager.Initialize();
            InitializeComponent();
            CheckOutlookRunning();
            SetupTimer();
            StartTeamsMonitor();
            QueueOverlay.Initialize();
            SystemEvents.PowerModeChanged += OnPowerChange;
#if DEBUG
            var contextMenu = tb.ContextMenu;

            // MenuItem: schedule test events in 5 sec
            var test1MenuItem = new MenuItem();
            test1MenuItem.Header = "Schedule test events in 5 sec";
            test1MenuItem.Click += Test1MenuItem_Click;

            // MenuItem: popup with test events
            var test2MenuItem = new MenuItem();
            test2MenuItem.Header = "Popup with test events";
            test2MenuItem.Click += Test2MenuItem_Click;

            // MenuItem: drop current schedule into log file
            var test3MenuItem = new MenuItem();
            test3MenuItem.Header = "Drop curremt schedule into log file";
            test3MenuItem.Click += Test3MenuItem_Click;

            // MenuItem: show current log file
            var test4MenuItem = new MenuItem();
            test4MenuItem.Header = "Show current log file";
            test4MenuItem.Click += Test4MenuItem_Click;


            // MenuItem: test Teams message popup
            var test5MenuItem = new MenuItem();
            test5MenuItem.Header = "Test Teams message popup";
            test5MenuItem.Click += Test5MenuItem_Click;

            // MenuItem: test Teams status change
            var test6MenuItem = new MenuItem();
            test6MenuItem.Header = "Set Teams: Available";
            test6MenuItem.Click += (_, _) => TeamsOperationQueue.Enqueue("Set Available",
                () => TeamsStatusManager.SetStatusAsync(TeamsStatusManager.TeamsStatus.Available));

            var test7MenuItem = new MenuItem();
            test7MenuItem.Header = "Set Teams: Busy";
            test7MenuItem.Click += (_, _) => TeamsOperationQueue.Enqueue("Set Busy",
                () => TeamsStatusManager.SetStatusAsync(TeamsStatusManager.TeamsStatus.Busy));

            // MenuItem: separator
            var separator = new Separator();

            contextMenu.Items.Insert(0, test1MenuItem);
            contextMenu.Items.Insert(1, test2MenuItem);
            contextMenu.Items.Insert(2, test3MenuItem);
            contextMenu.Items.Insert(3, test4MenuItem);
            contextMenu.Items.Insert(4, test5MenuItem);
            // MenuItem: test typing simulation
            var test8MenuItem = new MenuItem();
            test8MenuItem.Header = "Test: Simulate Typing";
            test8MenuItem.Click += (_, _) => TeamsOperationQueue.Enqueue("Simulate typing to Boris Kudriashov",
                () => TeamsStatusManager.SimulateTypingAsync("Boris Kudriashov"));

            contextMenu.Items.Insert(5, test6MenuItem);
            contextMenu.Items.Insert(6, test7MenuItem);
            contextMenu.Items.Insert(7, test8MenuItem);
            contextMenu.Items.Insert(8, separator);
#endif
        }
        private void OnPowerChange(object sender, PowerModeChangedEventArgs e)
        {
            switch (e.Mode)
            {
                case PowerModes.Suspend:
                    Log.Information("PowerModes.Suspend");
                    // Computer is going to sleep
                    break;
                case PowerModes.Resume:
                    // Computer has been woken up
                    Log.Information("PowerModes.Resume");
                    _timer.Change(0, OUTLOOK_TIMER_INTERVAL_MINUTES * 60 * 1000);
                    break;
            }
        }
        private void CheckOutlookRunning()
        {
            var source = MeetNowSettings.Instance.OutlookSource;

            if (source == "Classic")
            {
                bool isRunning = Process.GetProcessesByName("OUTLOOK").Length > 0;
                if (!isRunning)
                {
                    var result = MessageBox.Show(
                        "Classic Outlook is not running.\n\n" +
                        "Start it? MeetNow will restart after Outlook launches.\n\n" +
                        "Click No to open Settings and change the Outlook source.",
                        "MeetNow", MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo { FileName = "outlook.exe", UseShellExecute = true });
                            Log.Information("Launched Classic Outlook, restarting in 5s...");
                            System.Threading.Tasks.Task.Delay(5000).ContinueWith(_ =>
                                Dispatcher.Invoke(() => MeetNowHelper.RestartApplication()));
                            return;
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Failed to start Classic Outlook");
                        }
                    }
                    SettingsWindow.ShowWindow();
                }
            }
            else
            {
                bool isRunning = Process.GetProcessesByName("olk").Length > 0;
                if (!isRunning)
                {
                    var result = MessageBox.Show(
                        "New Outlook is not running.\n\n" +
                        "Start it?\n\n" +
                        "Click No to open Settings and change the Outlook source.",
                        "MeetNow", MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo { FileName = "ms-outlook:", UseShellExecute = true });
                            Log.Information("Launched New Outlook via ms-outlook: protocol");
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Failed to start New Outlook");
                        }
                    }
                    else
                    {
                        SettingsWindow.ShowWindow();
                    }
                }
            }
        }

        private void SetupTimer()
        {
            _timer = new Timer(TimerElapsed, null, 0, OUTLOOK_TIMER_INTERVAL_MINUTES * 60 * 1000); // 5 minutes in milliseconds
        }

        private void TimerElapsed(object? state)
        {
            Log.Information("Timer tick");
            RefreshOutlookWithRetry();
        }

        private void StartTeamsMonitor()
        {
            try
            {
                var username = Model.Username ?? Environment.UserName;

                _notificationMonitor = new NotificationListenerMonitor(username);
                _notificationMonitor.NewMessageDetected += OnTeamsMessageDetected;
                if (_notificationMonitor.Start())
                {
                    Log.Information("Teams monitor: using Win32 accessibility hooks for toast detection");
                }

                _teamsMonitor = new TeamsMessageMonitor(username);
                _teamsMonitor.NewMessageDetected += OnTeamsMessageDetected;
                if (_teamsMonitor.Start())
                {
                    Log.Information("Teams monitor: LevelDB polling active as supplement");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to start Teams message monitor");
            }
        }

        private void OnTeamsMessageDetected(TeamsMessage message)
        {
            try
            {
                // Check for per-contact priority override first
                if (ContactPriorityProvider.TryGetUrgencyOverride(message.Sender, out var overrideUrgency, out var overrideReason))
                {
                    message.Urgency = overrideUrgency;
                    message.UrgencyReason = overrideReason;
                }
                else
                {
                    var (urgency, reason) = LocalUrgencyClassifier.Classify(message);
                    message.Urgency = urgency;
                    message.UrgencyReason = reason;
                }

                Log.Information("Message urgency: {Urgency} ({Reason})", message.Urgency, message.UrgencyReason);
                MessageSummaryWindow.AddMessage(message);

                if (AutopilotOverlay.IsActive)
                {
                    switch (message.Urgency)
                    {
                        case MessageUrgency.Urgent:
                            TeamsMessagePopupWindow.ShowUrgentMessage(message);
                            if (message.IsDirectChat)
                            {
                                var settings = MeetNowSettings.Instance;
                                if (settings.SimulateTypingInAutopilot
                                    && TeamsOperationQueue.TryClaimSimulateTyping(message.Sender))
                                {
                                    TeamsOperationQueue.Enqueue($"Simulate typing to {message.Sender}",
                                        () => TeamsStatusManager.SimulateTypingAsync(message.Sender));
                                }
                                if (settings.AutoReplyHiInAutopilot)
                                    AutopilotOverlay.TrackUrgentMessage(message.Sender);
                                ForwardUrgentIfEnabled(message);
                            }
                            break;
                        case MessageUrgency.Normal:
                            TeamsMessagePopupWindow.PlayNormalSound();
                            break;
                        // Low: no sound, no popup
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error handling Teams message");
            }
        }

        private void ForwardUrgentIfEnabled(TeamsMessage message)
        {
            var settings = MeetNowSettings.Instance;
            if (!settings.ForwardUrgentInAutopilot || string.IsNullOrWhiteSpace(settings.ForwardToEmail))
                return;

            var forwardText = $"{message.Sender} (Urgent): {message.Content}";
            Log.Information("Forwarding urgent message to {Email}: {Preview}",
                settings.ForwardToEmail, forwardText.Length > 80 ? forwardText[..80] : forwardText);

            TeamsOperationQueue.Enqueue($"Forward to {settings.ForwardToEmail}",
                () => TeamsStatusManager.SendMessageAsync(settings.ForwardToEmail, forwardText));
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _timer.Dispose();
            _notificationMonitor?.Dispose();
            _teamsMonitor?.Dispose();
            JobManager.StopAndBlock();
            Application.Current.Shutdown();
            Log.Information("Application closed");
        }

        private void MenuItem_MessagesClick(object sender, RoutedEventArgs e)
        {
            MessageSummaryWindow.ShowWindow();
        }

        private void MenuItem_SettingsClick(object sender, RoutedEventArgs e)
        {
            SettingsWindow.ShowWindow();
        }

        private void MenuItem_AutopilotClick(object sender, RoutedEventArgs e)
        {
            AutopilotOverlay.Toggle();
        }

        private void MenuItem_ClearQueueClick(object sender, RoutedEventArgs e)
        {
            TeamsOperationQueue.ClearQueue();
        }

        private void MenuItem_ExitAppClick(object sender, RoutedEventArgs e)
        {
            Close();
        }
        private bool RefreshOutlookWithRetry(bool debug = false, int retryCount = 3)
        {
            int retries = 0;
            while (!RefreshOutlook(debug) && retries < retryCount)
            {
                var waitTime = TimeSpan.FromSeconds(30);
                Thread.Sleep(waitTime);
                retries++;
            }
            return retries < retryCount;
        }
        private bool RefreshOutlook(bool debug = false)
        {
            try
            {
                Log.Information("Refreshing Outlook");
                var now = DateTime.Now;
                string username = Dispatcher.Invoke(() => Model.Username) ?? Environment.UserName;

                var source = MeetNowSettings.Instance.OutlookSource;
                TeamsMeeting[] meetings;

                if (source == "Classic")
                {
                    bool isRunning = Process.GetProcessesByName("OUTLOOK").Length > 0;
                    if (!isRunning)
                    {
                        Log.Warning("Classic Outlook selected but not running");
                        return false;
                    }
                    try
                    {
                        (meetings, username) = OutlookHelper.GetTeamsMeetings(now, debug);
                        Log.Information("Classic Outlook COM: {Count} meetings", meetings.Length);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Outlook COM not available");
                        return false;
                    }
                }
                else
                {
                    bool isRunning = Process.GetProcessesByName("olk").Length > 0;
                    try
                    {
                        meetings = OutlookCacheReader.GetTodaysMeetings(now);
                        Log.Information("New Outlook cache: {Count} meetings (olk running={Running})", meetings.Length, isRunning);
                        if (!isRunning && meetings.Length > 0)
                            Log.Warning("New Outlook not running — cache may be stale");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error reading Outlook cache");
                        return false;
                    }
                }

                if (meetings.Length == 0)
                {
                    Log.Information("No meetings found");
                    return true; // not an error — just no meetings today
                }

                if (meetings.Length > 0)
                {
                    meetings = meetings.OrderBy(m => m.Start).ToArray();
                    Dispatcher.Invoke(() =>
                    {
                        Model.Username = username;
                        Model.TeamsMeetings = meetings.OrderBy(m => m.Start).ToArray();
                    });

                    Dictionary<DateTime, List<TeamsMeeting>> aggregatedEvents = new();

                    foreach (var meeting in meetings)
                    {
                        if (!string.IsNullOrEmpty(meeting.TeamsUrl))
                        {
                            if (now < meeting.Start)
                            {
                                var timespan = TimeSpan.FromMinutes(5);
                                if (debug)
                                {
                                    timespan =TimeSpan.FromSeconds(1);
                                }
                                DateTime roundedTime = OutlookHelper.RoundDownToNearestInterval(meeting.Start, timespan);

                                if (!aggregatedEvents.ContainsKey(roundedTime))
                                {
                                    aggregatedEvents[roundedTime] = new();
                                }

                                aggregatedEvents[roundedTime].Add(meeting);
                            }
                        }
                    }

                    JobManager.RemoveAllJobs();
                    Dispatcher.Invoke(() =>
                    {
                        foreach (var item in _scheduledPopupMenuItems)
                        {
                            if (item is MenuItem menuItem)
                            {
                                menuItem.Click -= MeetingItemClick;

                            }
                            tb.ContextMenu.Items.Remove(item);
                        }
                    });
                    _scheduledPopupMenuItems.Clear();
                    var contextMenuNum = 0;
                    foreach (var entry in aggregatedEvents)
                    {
                        foreach (var meeting in entry.Value)
                        {
                            Log.Information($"Scheduling popup for {entry.Key.ToString("HH:mm")}: {meeting.Subject}");
                            Dispatcher.Invoke(() =>
                            {
                                var menuItem = new MenuItem
                                {
                                    Header = $"{meeting.Start.ToString("HH:mm")}: {meeting.Subject}",
                                    Tag = meeting
                                };
                                menuItem.Click += MeetingItemClick;
                                tb.ContextMenu.Items.Insert(contextMenuNum, menuItem);
                                _scheduledPopupMenuItems.Add(menuItem);
                                contextMenuNum++;
                            });

                        }
                        SchedulePopup(entry.Key, entry.Value);
                        SchedulePopupClose(entry.Key.AddMinutes(5));
                    }
                    if (_scheduledPopupMenuItems.Count > 0)
                        Dispatcher.Invoke(() =>
                        {
                            var separator = new Separator();
                            tb.ContextMenu.Items.Insert(contextMenuNum, separator);
                            _scheduledPopupMenuItems.Add(separator);
                        });
                }

                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error refreshing Outlook");
                return false;
            }
        }

        void MeetingItemClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is TeamsMeeting meeting)
            {
                Log.Information("Starting meeting: {Time} - {Subject} URL=[{Url}]", meeting.Start.ToString("HH:mm"), meeting.Subject, meeting.TeamsUrl);
                OutlookHelper.StartTeamsMeeting(meeting.TeamsUrl);
            }
        }
        public static void SchedulePopup(DateTime startTime, List<TeamsMeeting> subjects)
        {
            JobManager.AddJob(new EventPopupJob(subjects.ToArray()), s => s.ToRunOnceAt(startTime));
        }
        public static void SchedulePopupClose(DateTime startTime)
        {
            JobManager.AddJob(new EventClosePopupJob(), s => s.ToRunOnceAt(startTime));
        }

        private void Test1MenuItem_Click(object sender, RoutedEventArgs e)
        {
            RefreshOutlookWithRetry(true, 1);
        }
        private void Test2MenuItem_Click(object sender, RoutedEventArgs e)
        {
            //RefreshOutlook(true);


            var (events, b) = OutlookHelper.GetTeamsMeetings(DateTime.Now, true);
            PopupEventsWindow.Show(events);
        }
        private void Test3MenuItem_Click(object sender, RoutedEventArgs e)
        {
            Log.Information("Current schedules:");
            foreach (var schedules in JobManager.AllSchedules)
            {
                Log.Information($"    | {schedules.Name}: {schedules.NextRun}");
            }

        }
        private void Test4MenuItem_Click(object sender, RoutedEventArgs e)
        {
            var directoryInfo = new DirectoryInfo(System.IO.Path.GetTempPath());
            var latestLogFile = directoryInfo.GetFiles()
                                             .Where(f => f.Name.StartsWith("MeetNow"))
                                             .OrderByDescending(f => f.LastWriteTime)
                                             .FirstOrDefault();

            if (latestLogFile != null)
            {
                var currentLogFilePath = latestLogFile.FullName;

                RunPowershellCommand($"Get-Content '{currentLogFilePath}' -Wait -Tail 50");
            }
        }
        private void Test5MenuItem_Click(object sender, RoutedEventArgs e)
        {
            var testMessage = new TeamsMessage
            {
                Id = "test_" + DateTime.Now.Ticks,
                Sender = "Doe, John TESTORG/IT",
                Content = "Hey, are you available? We have a production issue with the deployment pipeline and need your help ASAP.",
                Timestamp = DateTime.Now,
                ThreadType = "chat",
                IsMention = true,
                MentionedNames = new[] { "Kudriashov" },
                Urgency = MessageUrgency.Urgent,
                UrgencyReason = "Direct message requesting immediate help with production issue"
            };

            TeamsMessagePopupWindow.ShowUrgentMessage(testMessage);
        }
        private void RunPowershellCommand(string command)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
                UseShellExecute = false
            };

            Process.Start(startInfo);
        }

    }
}
