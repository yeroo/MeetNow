using MeetNow.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MeetNow
{
    public partial class FindPersonWindow : Window
    {
        private DispatcherTimer? _debounceTimer;

        public FindPersonWindow()
        {
            InitializeComponent();
            SearchBox.Focus();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_debounceTimer == null)
            {
                _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                _debounceTimer.Tick += (s, args) =>
                {
                    _debounceTimer.Stop();
                    _ = SearchAsync(SearchBox.Text.Trim());
                };
            }
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private async System.Threading.Tasks.Task SearchAsync(string query)
        {
            if (query.Length < 3)
            {
                ResultsPanel.Children.Clear();
                StatusText.Text = query.Length > 0 ? "Type at least 3 characters..." : "";
                return;
            }

            // Local search first
            var localResults = ContactDatabase.GetByName(query);
            ShowResults(localResults, query);

            // Remote search via WebViewManager if few local results
            if (localResults.Count < 5 && WebViewManager.Instance.IsInitialized)
            {
                StatusText.Text = $"Searching Teams directory for \"{query}\"...";
                try
                {
                    List<Contact>? remoteResults = null;
                    var searchQuery = query; // capture for lambda

                    await WebViewManager.Instance.RequestTask("FindPerson",
                        async instance =>
                        {
                            remoteResults = await Tasks.PeopleEnricherTask.SearchAsync(instance, searchQuery);
                        });

                    if (remoteResults != null && remoteResults.Count > 0)
                    {
                        // Merge with local, deduplicate
                        var allIds = new HashSet<string>(localResults.Select(c => c.TeamsUserId), StringComparer.OrdinalIgnoreCase);
                        var merged = new List<Contact>(localResults);
                        foreach (var r in remoteResults)
                        {
                            if (allIds.Add(r.TeamsUserId))
                            {
                                ContactDatabase.Upsert(r);
                                merged.Add(r);
                            }
                        }
                        ShowResults(merged, query);
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Remote search failed");
                }
            }

            StatusText.Text = $"{ResultsPanel.Children.Count} result(s)";
        }

        private void ShowResults(List<Contact> contacts, string query)
        {
            ResultsPanel.Children.Clear();
            foreach (var contact in contacts.Take(20))
            {
                ResultsPanel.Children.Add(CreateContactRow(contact));
            }
        }

        private UIElement CreateContactRow(Contact contact)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Info panel
            var infoPanel = new StackPanel();

            var nameLine = new TextBlock
            {
                Text = contact.DisplayName,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)),
                FontWeight = FontWeights.SemiBold
            };
            infoPanel.Children.Add(nameLine);

            var details = new List<string>();
            if (!string.IsNullOrWhiteSpace(contact.Email)) details.Add(contact.Email);
            if (!string.IsNullOrWhiteSpace(contact.JobTitle)) details.Add(contact.JobTitle);
            if (details.Count > 0)
            {
                infoPanel.Children.Add(new TextBlock
                {
                    Text = string.Join(" | ", details),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA))
                });
            }

            // Enrichment status indicator
            if (contact.EnrichmentStatus == EnrichmentStatus.Pending)
            {
                infoPanel.Children.Add(new TextBlock
                {
                    Text = "Loading details...",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                    FontStyle = FontStyles.Italic
                });
            }

            Grid.SetColumn(infoPanel, 0);
            grid.Children.Add(infoPanel);

            // Pin button
            var pinBtn = new Button
            {
                Content = contact.IsPinned ? "\u2605" : "\u2606", // filled/empty star
                FontSize = 16,
                Width = 32,
                Height = 32,
                Background = Brushes.Transparent,
                Foreground = contact.IsPinned
                    ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00))
                    : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                BorderBrush = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = contact.TeamsUserId
            };
            pinBtn.Click += PinBtn_Click;
            Grid.SetColumn(pinBtn, 1);
            grid.Children.Add(pinBtn);

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 2, 0, 2),
                Child = grid
            };

            return border;
        }

        private void PinBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string userId)
            {
                var contact = ContactDatabase.GetById(userId);
                if (contact != null)
                {
                    ContactDatabase.SetPinned(userId, !contact.IsPinned);
                    _ = SearchAsync(SearchBox.Text.Trim()); // Refresh
                }
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
