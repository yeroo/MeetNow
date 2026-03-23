using Serilog;
using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MeetNow
{
    public partial class SettingsWindow : Window
    {
        private static SettingsWindow? _instance;

        public SettingsWindow()
        {
            InitializeComponent();
            LoadSettings();
            RefreshContactLists();
        }

        public static void ShowWindow()
        {
            if (_instance == null || !_instance.IsLoaded)
            {
                _instance = new SettingsWindow();
                _instance.Show();
            }
            else
            {
                _instance.Activate();
            }
        }

        private void LoadSettings()
        {
            var settings = MeetNowSettings.Instance;
            SimulateTypingCheckBox.IsChecked = settings.SimulateTypingInAutopilot;
            AutoReplyHiCheckBox.IsChecked = settings.AutoReplyHiInAutopilot;
            ForwardCheckBox.IsChecked = settings.ForwardUrgentInAutopilot;
            ForwardToEmail.Text = settings.ForwardToEmail ?? "";
            UpdateForwardToStatus();

            // Outlook source
            if (settings.OutlookSource == "Classic")
                OutlookSourceClassic.IsChecked = true;
            else
                OutlookSourceNew.IsChecked = true;
            UpdateOutlookSourceStatus();

            ShowWebViewCheckBox.IsChecked = settings.ShowTeamsWebView;
            LogTrafficCheckBox.IsChecked = settings.LogAllWebViewTraffic;

            AutopilotOffTimeBox.Text = settings.AutopilotOffTime;
            OpDelayBox.Text = settings.TeamsOperationDelaySeconds.ToString();
            TypingDurationBox.Text = settings.SimulateTypingDurationSeconds.ToString();
            TypingCooldownBox.Text = settings.SimulateTypingCooldownMinutes.ToString();
            AutoReplyDelayBox.Text = settings.AutoReplyDelayMinutes.ToString();
            AutoReplyThresholdBox.Text = settings.AutoReplyMessageThreshold.ToString();
        }

        private void UpdateForwardToStatus()
        {
            var email = MeetNowSettings.Instance.ForwardToEmail;
            ForwardToStatus.Text = string.IsNullOrWhiteSpace(email)
                ? "No forwarding contact set"
                : $"Currently forwarding to: {email}";
        }

        private void SimulateTypingCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            MeetNowSettings.Instance.SimulateTypingInAutopilot = SimulateTypingCheckBox.IsChecked == true;
            MeetNowSettings.Instance.Save();
        }

        private void AutoReplyHiCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            MeetNowSettings.Instance.AutoReplyHiInAutopilot = AutoReplyHiCheckBox.IsChecked == true;
            MeetNowSettings.Instance.Save();
        }

        private void ForwardCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            MeetNowSettings.Instance.ForwardUrgentInAutopilot = ForwardCheckBox.IsChecked == true;
            MeetNowSettings.Instance.Save();
        }

        private void ShowWebViewCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            var settings = MeetNowSettings.Instance;
            settings.ShowTeamsWebView = ShowWebViewCheckBox.IsChecked == true;
            settings.Save();
        }

        private void LogTrafficCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            var settings = MeetNowSettings.Instance;
            settings.LogAllWebViewTraffic = LogTrafficCheckBox.IsChecked == true;
            settings.Save();
        }

        private void SaveForwardTo_Click(object sender, RoutedEventArgs e)
        {
            var email = ForwardToEmail.Text.Trim();
            MeetNowSettings.Instance.ForwardToEmail = string.IsNullOrWhiteSpace(email) ? null : email;
            MeetNowSettings.Instance.Save();
            UpdateForwardToStatus();
        }

        private void OutlookSource_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return; // skip during initialization
            var source = OutlookSourceClassic.IsChecked == true ? "Classic" : "New";
            MeetNowSettings.Instance.OutlookSource = source;
            MeetNowSettings.Instance.Save();
            UpdateOutlookSourceStatus();
        }

        private void UpdateOutlookSourceStatus()
        {
            var source = MeetNowSettings.Instance.OutlookSource;
            bool isRunning;
            string processName;
            if (source == "Classic")
            {
                processName = "Classic Outlook (OUTLOOK.EXE)";
                isRunning = Process.GetProcessesByName("OUTLOOK").Length > 0;
            }
            else
            {
                processName = "New Outlook (olk.exe)";
                isRunning = Process.GetProcessesByName("olk").Length > 0;
            }

            OutlookSourceStatus.Text = isRunning
                ? $"{processName} is running"
                : $"{processName} is NOT running — start it for calendar data";
            OutlookSourceStatus.Foreground = isRunning
                ? new SolidColorBrush(Color.FromRgb(0x8F, 0xF0, 0x8F))
                : new SolidColorBrush(Color.FromRgb(0xFF, 0x88, 0x88));
        }

        private void RestartMeetNow_Click(object sender, RoutedEventArgs e)
        {
            MeetNowHelper.RestartApplication();
        }

        private void RestartOutlookAndMeetNow_Click(object sender, RoutedEventArgs e)
        {
            var source = MeetNowSettings.Instance.OutlookSource;
            var outlook = source == "Classic" ? "Classic Outlook" : "New Outlook";
            var result = MessageBox.Show(
                $"This will:\n1. Close {outlook}\n2. Start {outlook}\n3. Wait for it to initialize\n4. Start MeetNow\n\nProceed?",
                "Restart Outlook + MeetNow",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
                MeetNowHelper.RestartWithOutlook();
        }

        private void ClearForwardTo_Click(object sender, RoutedEventArgs e)
        {
            ForwardToEmail.Text = "";
            MeetNowSettings.Instance.ForwardToEmail = null;
            MeetNowSettings.Instance.Save();
            UpdateForwardToStatus();
        }

        private void RefreshContactLists()
        {
            // Urgent contacts
            var urgentContacts = ContactPriorityProvider.GetContactsByPriority(ContactPriorityProvider.ContactPriority.Urgent);
            UrgentContactsList.Items.Clear();
            NoUrgentContacts.Visibility = urgentContacts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var contact in urgentContacts)
            {
                UrgentContactsList.Items.Add(CreateContactRow(contact, "Urgent", Brushes.OrangeRed));
            }

            // All overrides
            var allPriorities = new[]
            {
                ContactPriorityProvider.ContactPriority.Urgent,
                ContactPriorityProvider.ContactPriority.Normal,
                ContactPriorityProvider.ContactPriority.Low
            };

            AllOverridesList.Items.Clear();
            bool hasAny = false;

            foreach (var priority in allPriorities)
            {
                var contacts = ContactPriorityProvider.GetContactsByPriority(priority);
                foreach (var contact in contacts)
                {
                    hasAny = true;
                    var color = priority switch
                    {
                        ContactPriorityProvider.ContactPriority.Urgent => Brushes.OrangeRed,
                        ContactPriorityProvider.ContactPriority.Normal => Brushes.DodgerBlue,
                        ContactPriorityProvider.ContactPriority.Low => Brushes.Gray,
                        _ => Brushes.Gray
                    };
                    AllOverridesList.Items.Add(CreateContactRow(contact, priority.ToString(), color));
                }
            }

            NoOverrides.Visibility = hasAny ? Visibility.Collapsed : Visibility.Visible;
        }

        private UIElement CreateContactRow(string contact, string priorityLabel, Brush badgeColor)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Contact name
            var nameBlock = new TextBlock
            {
                Text = contact,
                Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(nameBlock, 0);
            grid.Children.Add(nameBlock);

            // Priority badge
            var badge = new Border
            {
                Background = badgeColor,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = priorityLabel,
                    Foreground = Brushes.White,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold
                }
            };
            Grid.SetColumn(badge, 1);
            grid.Children.Add(badge);

            // Remove button
            var removeBtn = new Button
            {
                Content = "Remove",
                Tag = contact,
                Margin = new Thickness(8, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x4D, 0x20, 0x20)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x88, 0x88)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 3, 8, 3),
                FontSize = 11,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            removeBtn.Click += RemoveContact_Click;
            Grid.SetColumn(removeBtn, 2);
            grid.Children.Add(removeBtn);

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 1, 0, 1),
                Child = grid
            };

            return border;
        }

        private void RemoveContact_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string contact)
            {
                ContactPriorityProvider.SetPriority(contact, ContactPriorityProvider.ContactPriority.Default);
                Log.Information("Removed priority override for {Contact}", contact);
                RefreshContactLists();
            }
        }

        private void RefreshRecentContacts_Click(object sender, RoutedEventArgs e)
        {
            RefreshRecentContactsBtn.IsEnabled = false;
            RefreshRecentContactsBtn.Content = "Loading...";

            try
            {
                var contacts = TeamsMessageMonitor.GetRecentContacts();
                RecentContactsList.Items.Clear();
                NoRecentContacts.Visibility = contacts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                if (contacts.Count == 0)
                    NoRecentContacts.Text = "No recent contacts found in Teams data";

                foreach (var (name, lastSeen) in contacts)
                {
                    RecentContactsList.Items.Add(CreateRecentContactRow(name, lastSeen));
                }

                Log.Information("Loaded {Count} recent contacts from Teams LevelDB", contacts.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error loading recent contacts");
                NoRecentContacts.Text = "Error loading contacts";
                NoRecentContacts.Visibility = Visibility.Visible;
            }
            finally
            {
                RefreshRecentContactsBtn.IsEnabled = true;
                RefreshRecentContactsBtn.Content = "Refresh from Teams";
            }
        }

        private UIElement CreateRecentContactRow(string contact, DateTime lastSeen)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Contact name + last seen
            var namePanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            namePanel.Children.Add(new TextBlock
            {
                Text = contact,
                Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (lastSeen > DateTime.MinValue)
            {
                namePanel.Children.Add(new TextBlock
                {
                    Text = $"Last seen: {lastSeen:MMM d, h:mm tt}",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                    FontSize = 10
                });
            }
            Grid.SetColumn(namePanel, 0);
            grid.Children.Add(namePanel);

            // Current priority badge (if set)
            var currentPriority = ContactPriorityProvider.GetPriority(contact);
            if (currentPriority != ContactPriorityProvider.ContactPriority.Default)
            {
                var badgeColor = currentPriority switch
                {
                    ContactPriorityProvider.ContactPriority.Urgent => Brushes.OrangeRed,
                    ContactPriorityProvider.ContactPriority.Normal => Brushes.DodgerBlue,
                    ContactPriorityProvider.ContactPriority.Low => Brushes.Gray,
                    _ => Brushes.Gray
                };
                var badge = new Border
                {
                    Background = badgeColor,
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = currentPriority.ToString(),
                        Foreground = Brushes.White,
                        FontSize = 10,
                        FontWeight = FontWeights.Bold
                    }
                };
                Grid.SetColumn(badge, 1);
                grid.Children.Add(badge);
            }

            // Priority buttons
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var priorities = new[]
            {
                (ContactPriorityProvider.ContactPriority.Urgent, "#6D2020", "#F88", "U"),
                (ContactPriorityProvider.ContactPriority.Normal, "#1D3D5D", "#8BF", "N"),
                (ContactPriorityProvider.ContactPriority.Low, "#3D3D3D", "#AAA", "L"),
                (ContactPriorityProvider.ContactPriority.Default, "#2D2D2D", "#888", "x"),
            };

            foreach (var (priority, bg, fg, label) in priorities)
            {
                var btn = new Button
                {
                    Content = label,
                    Tag = new Tuple<string, ContactPriorityProvider.ContactPriority>(contact, priority),
                    Width = 24,
                    Height = 22,
                    Margin = new Thickness(2, 0, 0, 0),
                    Background = new BrushConverter().ConvertFromString(bg) as Brush,
                    Foreground = new BrushConverter().ConvertFromString(fg) as Brush,
                    BorderThickness = new Thickness(currentPriority == priority ? 1 : 0),
                    BorderBrush = currentPriority == priority ? Brushes.White : null,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = priority == ContactPriorityProvider.ContactPriority.Default ? "Remove override" : priority.ToString()
                };
                btn.Click += RecentContactPriority_Click;
                buttonPanel.Children.Add(btn);
            }

            Grid.SetColumn(buttonPanel, 2);
            grid.Children.Add(buttonPanel);

            return new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 1, 0, 1),
                Child = grid
            };
        }

        private void RecentContactPriority_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is Tuple<string, ContactPriorityProvider.ContactPriority> tag)
            {
                ContactPriorityProvider.SetPriority(tag.Item1, tag.Item2);
                Log.Information("Set priority for {Contact} to {Priority}", tag.Item1, tag.Item2);
                RefreshContactLists();
                // Re-trigger refresh of recent contacts to update badges
                RefreshRecentContacts_Click(sender, e);
            }
        }

        private void SaveTimings_Click(object sender, RoutedEventArgs e)
        {
            var settings = MeetNowSettings.Instance;

            var offTime = AutopilotOffTimeBox.Text.Trim();
            if (TimeSpan.TryParse(offTime, out _))
                settings.AutopilotOffTime = offTime;

            if (int.TryParse(OpDelayBox.Text, out var opDelay) && opDelay >= 1)
                settings.TeamsOperationDelaySeconds = opDelay;
            if (int.TryParse(TypingDurationBox.Text, out var typingDur) && typingDur >= 1)
                settings.SimulateTypingDurationSeconds = typingDur;
            if (int.TryParse(TypingCooldownBox.Text, out var typingCd) && typingCd >= 0)
                settings.SimulateTypingCooldownMinutes = typingCd;
            if (int.TryParse(AutoReplyDelayBox.Text, out var replyDelay) && replyDelay >= 1)
                settings.AutoReplyDelayMinutes = replyDelay;
            if (int.TryParse(AutoReplyThresholdBox.Text, out var threshold) && threshold >= 1)
                settings.AutoReplyMessageThreshold = threshold;

            settings.Save();
            LoadSettings(); // refresh UI with validated values
        }

        protected override void OnClosed(System.EventArgs e)
        {
            base.OnClosed(e);
            _instance = null;
        }
    }
}
