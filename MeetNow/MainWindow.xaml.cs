using FluentScheduler;
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
        private Timer _timer;

        internal MainWindowModel Model
        {
            get => DataContext as MainWindowModel;
            private set => DataContext = value;
        }

        readonly List<Control> _scheduledPopupMenuItems = new();
        public MainWindow()
        {
            Model = new MainWindowModel();
            JobManager.Initialize();
            InitializeComponent();
            SetupTimer();
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


            // MenuItem: separator
            var separator = new Separator();

            contextMenu.Items.Insert(0, test1MenuItem);
            contextMenu.Items.Insert(1, test2MenuItem);
            contextMenu.Items.Insert(2, test3MenuItem);
            contextMenu.Items.Insert(3, test4MenuItem);
            contextMenu.Items.Insert(4, separator);
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
        private void SetupTimer()
        {
            _timer = new Timer(TimerElapsed, null, 0, OUTLOOK_TIMER_INTERVAL_MINUTES * 60 * 1000); // 5 minutes in milliseconds
        }

        private void TimerElapsed(object? state)
        {
            Log.Information("Timer tick");
            RefreshOutlookWithRetry();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _timer.Dispose();
            JobManager.StopAndBlock();
            Application.Current.Shutdown();
            Log.Information("Application closed");
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
        bool _IsOutlookRunningMessageBoxShown = false;
        private bool RefreshOutlook(bool debug = false)
        {
            try
            {
                Log.Information("Refreshing Outlook");
                bool isOutlookRunning = false;
                // Detect if Outlook is running
                try
                {
                    Process[] proc = Process.GetProcessesByName("OUTLOOK");
                    if (proc.Length > 0)
                    {
                        isOutlookRunning = true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "There is an exception!");
                }
                if (isOutlookRunning)
                {
                    // If outlook is running - continue
                    var now = DateTime.Now;
                    (var meetings, var username) = OutlookHelper.GetTeamsMeetings(now, debug);
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
                    return true;
                }
                else
                {
                    Log.Information("Outlook is not running");
                    if (!_IsOutlookRunningMessageBoxShown)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            MessageBox.Show("Please start Outlook to use this application", "Outlook is not running", MessageBoxButton.OK, MessageBoxImage.Information);
                        });
                        _IsOutlookRunningMessageBoxShown = true;
                    }
                    return false;
                }

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
                OutlookHelper.StartTeamsMeeting(meeting.TeamsUrl);
                Log.Information($"Starting meeting: {meeting.Start.ToString("HH:mm")} - {meeting.Subject}");
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
