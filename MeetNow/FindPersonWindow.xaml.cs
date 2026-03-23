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
        private readonly Func<TeamsWebViewDataExtractor?> _getExtractor;
        private readonly Func<ContactEnricher?> _getEnricher;

        public FindPersonWindow(Func<TeamsWebViewDataExtractor?>? getExtractor = null, Func<ContactEnricher?>? getEnricher = null)
        {
            InitializeComponent();
            _getExtractor = getExtractor ?? (() => null);
            _getEnricher = getEnricher ?? (() => null);
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

            // Remote search if few local results
            var extractor = _getExtractor();
            if (localResults.Count < 5 && extractor != null)
            {
                StatusText.Text = $"Searching Teams directory for \"{query}\"...";
                try
                {
                    var remoteResults = await SearchTeamsDirectoryAsync(query);
                    if (remoteResults.Count > 0)
                    {
                        // Merge with local, deduplicate
                        var allIds = new HashSet<string>(localResults.Select(c => c.TeamsUserId), StringComparer.OrdinalIgnoreCase);
                        var merged = new List<Contact>(localResults);
                        foreach (var r in remoteResults)
                        {
                            if (allIds.Add(r.TeamsUserId))
                            {
                                ContactDatabase.Upsert(r);
                                _getEnricher()?.Enqueue(r.TeamsUserId);
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

        private async System.Threading.Tasks.Task<List<Contact>> SearchTeamsDirectoryAsync(string query)
        {
            var ext = _getExtractor();
            if (ext == null) return new();

            var js = @"
(async function() {
    try {
        var resp = await fetch('/api/mt/part/emea-02/beta/users/searchV2?includeDLs=false&includeBots=false&enableGuest=true&source=newChat&skypeTeamsInfo=true', {
            method: 'POST',
            credentials: 'include',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ queryText: '" + query.Replace("'", "\\'").Replace("\\", "\\\\") + @"' })
        });
        if (!resp.ok) return null;
        var data = await resp.json();
        var results = data.value || data.results || [];
        return JSON.stringify(results.map(function(r) {
            return {
                userId: r.mri || ('8:orgid:' + r.objectId) || '',
                displayName: r.displayName || '',
                email: r.email || r.sipAddress || r.userPrincipalName || null,
                jobTitle: r.jobTitle || null,
                department: r.department || null
            };
        }).slice(0, 20));
    } catch(e) { return null; }
})();";

            // ExecuteScriptAsync resolves JS Promises natively
            var resultJson = await ext.EvaluateJsAsync(js);

            if (resultJson == null) return new();

            var contacts = new List<Contact>();
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(resultJson);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var userId = item.TryGetProperty("userId", out var u) ? u.GetString() : null;
                    var name = item.TryGetProperty("displayName", out var n) ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(name)) continue;

                    contacts.Add(new Contact
                    {
                        TeamsUserId = userId,
                        DisplayName = name,
                        Email = item.TryGetProperty("email", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString() : null,
                        JobTitle = item.TryGetProperty("jobTitle", out var j) && j.ValueKind == System.Text.Json.JsonValueKind.String ? j.GetString() : null,
                        Department = item.TryGetProperty("department", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String ? d.GetString() : null,
                        Source = ContactSource.Search,
                        LastSeenTimestamp = DateTime.Now,
                        EnrichmentStatus = EnrichmentStatus.Pending
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to parse search results");
            }
            return contacts;
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
