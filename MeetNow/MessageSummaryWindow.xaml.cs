using MeetNow.Models;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MeetNow
{
    public partial class MessageSummaryWindow : Window
    {
        private static MessageSummaryWindow? _instance;
        private static readonly ObservableCollection<MessageViewModel> _messages = new();

        public ObservableCollection<MessageViewModel> Messages => _messages;

        public MessageSummaryWindow()
        {
            InitializeComponent();
            DataContext = this;
            UpdateEmptyState();
            _messages.CollectionChanged += (_, _) => UpdateEmptyState();
            MessageList.ContextMenuOpening += MessageList_ContextMenuOpening;
        }

        public static void AddMessage(TeamsMessage msg)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                _messages.Insert(0, new MessageViewModel(msg));
                // Keep last 200 messages
                while (_messages.Count > 200)
                    _messages.RemoveAt(_messages.Count - 1);

                _instance?.UpdateEmptyState();
            });
        }

        public static void ShowWindow()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (_instance == null || !_instance.IsLoaded)
                {
                    _instance = new MessageSummaryWindow();
                }
                _instance.Show();
                _instance.Activate();
                if (_instance.WindowState == WindowState.Minimized)
                    _instance.WindowState = WindowState.Normal;
            });
        }

        public static int MessageCount => _messages.Count;

        public static ObservableCollection<MessageViewModel> AllMessages => _messages;

        private void UpdateEmptyState()
        {
            EmptyText.Visibility = _messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            MessageCountText.Text = _messages.Count > 0 ? $"({_messages.Count})" : "";
        }

        private void ClearClick(object sender, RoutedEventArgs e)
        {
            _messages.Clear();
        }

        private void MessageList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            // Find the ListBoxItem that was right-clicked
            if (e.OriginalSource is not DependencyObject source) return;
            var listBoxItem = FindParent<ListBoxItem>(source);
            if (listBoxItem == null) return;
            if (listBoxItem.DataContext is not MessageViewModel vm) return;

            var contextMenu = listBoxItem.ContextMenu;
            if (contextMenu == null) return;

            var current = ContactPriorityProvider.GetPriority(vm.Sender);

            if (contextMenu.Items[0] is MenuItem parentMenu)
            {
                foreach (var item in parentMenu.Items)
                {
                    if (item is not MenuItem mi || mi.Tag is not string tag) continue;

                    var itemPriority = tag switch
                    {
                        "Urgent" => ContactPriorityProvider.ContactPriority.Urgent,
                        "Normal" => ContactPriorityProvider.ContactPriority.Normal,
                        "Low" => ContactPriorityProvider.ContactPriority.Low,
                        _ => ContactPriorityProvider.ContactPriority.Default
                    };
                    mi.IsChecked = itemPriority == current;

                    // Wire up click handler (remove first to avoid duplicates)
                    mi.Click -= SetPriority_Click;
                    mi.Click += SetPriority_Click;
                }
            }
        }

        private void SetPriority_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem) return;
            if (menuItem.Tag is not string priorityStr) return;

            // Walk up logical tree to find the ContextMenu
            DependencyObject? parent = menuItem;
            ContextMenu? contextMenu = null;
            while (parent != null)
            {
                if (parent is ContextMenu cm) { contextMenu = cm; break; }
                parent = LogicalTreeHelper.GetParent(parent);
            }

            if (contextMenu?.PlacementTarget is not FrameworkElement fe) return;
            if (fe.DataContext is not MessageViewModel vm) return;

            var priority = priorityStr switch
            {
                "Urgent" => ContactPriorityProvider.ContactPriority.Urgent,
                "Normal" => ContactPriorityProvider.ContactPriority.Normal,
                "Low" => ContactPriorityProvider.ContactPriority.Low,
                _ => ContactPriorityProvider.ContactPriority.Default
            };

            ContactPriorityProvider.SetPriority(vm.Sender, priority);
        }

        private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            var current = VisualTreeHelper.GetParent(child);
            while (current != null)
            {
                if (current is T found) return found;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Hide instead of close so messages are preserved
            e.Cancel = true;
            Hide();
        }
    }

    public class MessageViewModel
    {
        public string Sender { get; }
        public string Content { get; }
        public string TimeStr { get; }
        public string Initials { get; }
        public string UrgencyStr { get; }
        public Brush BadgeColor { get; }
        public DateTime Timestamp { get; }
        public TeamsMessage Original { get; }

        public MessageViewModel(TeamsMessage msg)
        {
            Original = msg;
            Sender = msg.Sender;
            Content = msg.Content;
            Timestamp = msg.Timestamp;
            TimeStr = msg.Timestamp.ToString("HH:mm");
            UrgencyStr = msg.Urgency.ToString().ToUpper();
            Initials = GetInitials(msg.Sender);

            BadgeColor = msg.Urgency switch
            {
                MessageUrgency.Urgent => new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)),
                MessageUrgency.Normal => new SolidColorBrush(Color.FromRgb(0x4F, 0x52, 0xB2)),
                _ => new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55))
            };
        }

        private static string GetInitials(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";

            // Handle "LastName, FirstName ORG/DEPT" format
            var parts = name.Split(',');
            if (parts.Length >= 2)
            {
                var last = parts[0].Trim();
                var firstPart = parts[1].Trim().Split(' ')[0];
                return $"{firstPart[0]}{last[0]}".ToUpper();
            }

            // Handle "First Last" format
            var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 2)
                return $"{words[0][0]}{words[1][0]}".ToUpper();

            return name[..Math.Min(2, name.Length)].ToUpper();
        }
    }
}
