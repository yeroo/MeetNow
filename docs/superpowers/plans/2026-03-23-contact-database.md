# Contact Database Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a persistent contact database that auto-discovers contacts from WebView2 traffic, enriches them with full profile data, and provides a "Find Person" search dialog.

**Architecture:** Static `ContactDatabase` class stores contacts as JSON in `%LOCALAPPDATA%\MeetNow\contacts.json` using `ConcurrentDictionary` for thread safety. `TeamsWebViewDataExtractor` parses profile picture URLs to auto-discover contacts. `ContactEnricher` resolves stub contacts to full profiles via Teams People API (JS eval through the extractor). `FindPersonWindow` provides manual search and pin.

**Tech Stack:** WPF .NET 8, System.Text.Json, System.Collections.Concurrent, WebView2 JS eval

**Note:** API paths use hardcoded region `part/emea-02` (discovered from POC traffic). A follow-up task will add dynamic region discovery from captured traffic. For now this works for the user's Shell EMEA environment.

**Spec:** `docs/superpowers/specs/2026-03-23-contact-database-design.md`

---

### Task 1: Create Contact Model and ContactDatabase

**Files:**
- Create: `MeetNow/Models/Contact.cs`
- Create: `MeetNow/ContactDatabase.cs`

- [ ] **Step 1: Create the Contact model**

Create `MeetNow/Models/Contact.cs`:

```csharp
using System;

namespace MeetNow.Models
{
    public enum ContactSource { Chat, Search, Manual }
    public enum EnrichmentStatus { Pending, Enriched, Failed }

    public class Contact
    {
        public string TeamsUserId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string? Email { get; set; }
        public string? JobTitle { get; set; }
        public string? Department { get; set; }
        public string? Phone { get; set; }
        public string? ProfilePictureUrl { get; set; }
        public DateTime LastSeenTimestamp { get; set; }
        public bool IsPinned { get; set; }
        public ContactSource Source { get; set; }
        public DateTime LastUpdated { get; set; }
        public EnrichmentStatus EnrichmentStatus { get; set; } = EnrichmentStatus.Pending;
    }
}
```

- [ ] **Step 2: Create ContactDatabase static class**

Create `MeetNow/ContactDatabase.cs`:

```csharp
using MeetNow.Models;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace MeetNow
{
    public static class ContactDatabase
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MeetNow", "contacts.json");

        private static ConcurrentDictionary<string, Contact> _contacts = new(StringComparer.OrdinalIgnoreCase);
        private static Timer? _saveTimer;
        private static bool _dirty;
        private static readonly object _saveLock = new();

        static ContactDatabase()
        {
            Load();
            Prune();
        }

        public static List<Contact> GetByName(string query)
        {
            if (string.IsNullOrWhiteSpace(query) || query.Length < 3)
                return new List<Contact>();

            var search = query.Trim().ToLowerInvariant();
            return _contacts.Values
                .Where(c => MatchesWordBoundary(c.DisplayName, search)
                    || (c.Email != null && c.Email.Contains(search, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(c => c.IsPinned)
                .ThenByDescending(c => c.LastSeenTimestamp)
                .ToList();
        }

        public static Contact? GetByEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email)) return null;
            return _contacts.Values.FirstOrDefault(c =>
                string.Equals(c.Email, email, StringComparison.OrdinalIgnoreCase));
        }

        public static Contact? GetById(string teamsUserId)
        {
            _contacts.TryGetValue(teamsUserId, out var contact);
            return contact;
        }

        public static void Upsert(Contact contact)
        {
            _contacts.AddOrUpdate(contact.TeamsUserId,
                _ =>
                {
                    contact.LastUpdated = DateTime.Now;
                    return contact;
                },
                (_, existing) =>
                {
                    // Merge: keep richer data, update timestamps
                    existing.DisplayName = !string.IsNullOrWhiteSpace(contact.DisplayName)
                        ? contact.DisplayName : existing.DisplayName;
                    existing.Email = contact.Email ?? existing.Email;
                    existing.JobTitle = contact.JobTitle ?? existing.JobTitle;
                    existing.Department = contact.Department ?? existing.Department;
                    existing.Phone = contact.Phone ?? existing.Phone;
                    existing.ProfilePictureUrl = contact.ProfilePictureUrl ?? existing.ProfilePictureUrl;
                    if (contact.LastSeenTimestamp > existing.LastSeenTimestamp)
                        existing.LastSeenTimestamp = contact.LastSeenTimestamp;
                    if (contact.EnrichmentStatus == EnrichmentStatus.Enriched)
                        existing.EnrichmentStatus = contact.EnrichmentStatus;
                    existing.LastUpdated = DateTime.Now;
                    return existing;
                });

            ScheduleSave();
        }

        public static List<Contact> GetPinned()
        {
            return _contacts.Values
                .Where(c => c.IsPinned)
                .OrderBy(c => c.DisplayName)
                .ToList();
        }

        public static void SetPinned(string teamsUserId, bool pinned)
        {
            if (_contacts.TryGetValue(teamsUserId, out var contact))
            {
                contact.IsPinned = pinned;
                contact.LastUpdated = DateTime.Now;
                ScheduleSave();
            }
        }

        public static List<Contact> GetAll()
        {
            return _contacts.Values
                .OrderByDescending(c => c.IsPinned)
                .ThenByDescending(c => c.LastSeenTimestamp)
                .ToList();
        }

        public static void Prune()
        {
            var cutoff = DateTime.Now.AddDays(-90);
            var toRemove = _contacts.Values
                .Where(c => !c.IsPinned && c.LastSeenTimestamp < cutoff)
                .Select(c => c.TeamsUserId)
                .ToList();

            foreach (var id in toRemove)
                _contacts.TryRemove(id, out _);

            if (toRemove.Count > 0)
            {
                Log.Information("ContactDatabase: pruned {Count} stale contacts", toRemove.Count);
                ScheduleSave();
            }
        }

        public static void FlushAndDispose()
        {
            _saveTimer?.Dispose();
            _saveTimer = null;
            if (_dirty) SaveNow();
        }

        private static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var list = JsonSerializer.Deserialize<List<Contact>>(json) ?? new();
                    _contacts = new ConcurrentDictionary<string, Contact>(
                        list.Where(c => !string.IsNullOrEmpty(c.TeamsUserId))
                            .ToDictionary(c => c.TeamsUserId, c => c),
                        StringComparer.OrdinalIgnoreCase);
                    Log.Information("ContactDatabase: loaded {Count} contacts", _contacts.Count);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load contact database");
            }
        }

        private static void ScheduleSave()
        {
            _dirty = true;
            _saveTimer ??= new Timer(_ => SaveNow(), null, 30000, Timeout.Infinite);
        }

        private static void SaveNow()
        {
            lock (_saveLock)
            {
                try
                {
                    var dir = Path.GetDirectoryName(FilePath)!;
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    var list = _contacts.Values.ToList();
                    var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(FilePath, json);
                    _dirty = false;
                    _saveTimer?.Dispose();
                    _saveTimer = null;
                    Log.Debug("ContactDatabase: saved {Count} contacts", list.Count);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to save contact database");
                }
            }
        }

        private static bool MatchesWordBoundary(string displayName, string search)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return false;
            var name = displayName.ToLowerInvariant();

            // Match at start of any word (split on space, comma, hyphen)
            var words = name.Split(new[] { ' ', ',', '-', '/' }, StringSplitOptions.RemoveEmptyEntries);
            return words.Any(w => w.StartsWith(search)) || name.Contains(search);
        }
    }
}
```

- [ ] **Step 3: Build to verify**

Run:
```bash
cd MeetNow && dotnet build
```
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add MeetNow/Models/Contact.cs MeetNow/ContactDatabase.cs
git commit -m "feat: add Contact model and ContactDatabase with thread-safe storage"
```

---

### Task 2: Add Auto-Discovery of Contacts from Profile Picture URLs

**Files:**
- Modify: `MeetNow/TeamsWebViewDataExtractor.cs:81-105` (OnResponseReceived)

- [ ] **Step 1: Add contact extraction to OnResponseReceived**

In `TeamsWebViewDataExtractor.cs`, add a new method and call it from `OnResponseReceived`. After the traffic logging block (after line 94 `LogTraffic(...)`) and before the JSON content-type check (line 97), add:

```csharp
// Auto-discover contacts from profile picture URLs
TryExtractContact(uri);
```

Then add this method to the class:

```csharp
private void TryExtractContact(string uri)
{
    try
    {
        // Pattern 1: /profilepicturev2/8:orgid:GUID?displayname=Name
        if (uri.Contains("profilepicturev2/8:orgid:", StringComparison.OrdinalIgnoreCase))
        {
            var orgIdStart = uri.IndexOf("8:orgid:", StringComparison.OrdinalIgnoreCase);
            var orgIdEnd = uri.IndexOf('?', orgIdStart);
            if (orgIdEnd < 0) orgIdEnd = uri.Length;
            var teamsUserId = uri[orgIdStart..orgIdEnd];

            string? displayName = null;
            var dnParam = "displayname=";
            var dnStart = uri.IndexOf(dnParam, StringComparison.OrdinalIgnoreCase);
            if (dnStart >= 0)
            {
                dnStart += dnParam.Length;
                var dnEnd = uri.IndexOf('&', dnStart);
                if (dnEnd < 0) dnEnd = uri.Length;
                displayName = Uri.UnescapeDataString(uri[dnStart..dnEnd]);
            }

            if (!string.IsNullOrWhiteSpace(teamsUserId) && !string.IsNullOrWhiteSpace(displayName))
            {
                ContactDatabase.Upsert(new Models.Contact
                {
                    TeamsUserId = teamsUserId,
                    DisplayName = displayName,
                    LastSeenTimestamp = DateTime.Now,
                    Source = Models.ContactSource.Chat
                });
            }
        }

        // Pattern 2: /mergedProfilePicturev2?usersInfo=[{userId, displayName}]
        if (uri.Contains("mergedProfilePicturev2", StringComparison.OrdinalIgnoreCase)
            && uri.Contains("usersInfo=", StringComparison.OrdinalIgnoreCase))
        {
            var paramStart = uri.IndexOf("usersInfo=", StringComparison.OrdinalIgnoreCase) + 10;
            var paramEnd = uri.IndexOf('&', paramStart);
            var rawParam = paramEnd >= 0 ? uri[paramStart..paramEnd] : uri[paramStart..];
            var usersJson = Uri.UnescapeDataString(rawParam);

            using var doc = System.Text.Json.JsonDocument.Parse(usersJson);
            foreach (var user in doc.RootElement.EnumerateArray())
            {
                var userId = user.GetProperty("userId").GetString();
                var name = user.GetProperty("displayName").GetString();
                if (!string.IsNullOrWhiteSpace(userId) && !string.IsNullOrWhiteSpace(name))
                {
                    ContactDatabase.Upsert(new Models.Contact
                    {
                        TeamsUserId = userId,
                        DisplayName = name,
                        LastSeenTimestamp = DateTime.Now,
                        Source = Models.ContactSource.Chat
                    });
                }
            }
        }
    }
    catch (Exception ex)
    {
        Log.Debug(ex, "Failed to extract contact from URL");
    }
}
```

- [ ] **Step 2: Build and verify**

Run:
```bash
cd MeetNow && dotnet build
```

- [ ] **Step 3: Commit**

```bash
git add MeetNow/TeamsWebViewDataExtractor.cs
git commit -m "feat: auto-discover contacts from profile picture URLs in WebView2 traffic"
```

---

### Task 3: Create ContactEnricher

**Files:**
- Create: `MeetNow/ContactEnricher.cs`

- [ ] **Step 1: Create the enricher class**

Create `MeetNow/ContactEnricher.cs`:

```csharp
using MeetNow.Models;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace MeetNow
{
    public class ContactEnricher
    {
        private readonly TeamsWebViewDataExtractor _extractor;
        private readonly ConcurrentQueue<string> _queue = new();
        private DispatcherTimer? _timer;
        private bool _processing;

        public ContactEnricher(TeamsWebViewDataExtractor extractor)
        {
            _extractor = extractor;
        }

        public void Enqueue(string teamsUserId)
        {
            // Skip if already enriched recently
            var existing = ContactDatabase.GetById(teamsUserId);
            if (existing != null && existing.EnrichmentStatus == EnrichmentStatus.Enriched
                && existing.LastUpdated > DateTime.Now.AddHours(-24))
                return;

            _queue.Enqueue(teamsUserId);
            EnsureTimerRunning();
        }

        public void Start()
        {
            EnsureTimerRunning();
        }

        public void Stop()
        {
            _timer?.Stop();
            _timer = null;
        }

        private void EnsureTimerRunning()
        {
            if (_timer != null) return;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += async (s, e) => await ProcessNextAsync();
            _timer.Start();
        }

        private async Task ProcessNextAsync()
        {
            if (_processing) return;
            _processing = true;

            try
            {
                if (!_queue.TryDequeue(out var teamsUserId)) return;

                var contact = ContactDatabase.GetById(teamsUserId);
                if (contact == null) return;

                // Skip if already enriched recently
                if (contact.EnrichmentStatus == EnrichmentStatus.Enriched
                    && contact.Email != null
                    && contact.LastUpdated > DateTime.Now.AddHours(-24))
                    return;

                Log.Debug("Enriching contact: {Name} ({Id})", contact.DisplayName, teamsUserId);

                // Try Teams People API
                var result = await TryTeamsPeopleApiAsync(teamsUserId);
                if (result != null)
                {
                    ContactDatabase.Upsert(result);
                    return;
                }

                // Mark as failed if all APIs exhausted
                contact.EnrichmentStatus = EnrichmentStatus.Failed;
                contact.LastUpdated = DateTime.Now;
                ContactDatabase.Upsert(contact);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Enrichment error");
            }
            finally
            {
                _processing = false;
            }
        }

        private async Task<Contact?> TryTeamsPeopleApiAsync(string teamsUserId)
        {
            // Extract the orgid GUID from "8:orgid:xxxx-xxxx"
            var orgId = teamsUserId;
            if (orgId.StartsWith("8:orgid:", StringComparison.OrdinalIgnoreCase))
                orgId = orgId["8:orgid:".Length..];

            var js = $@"
(async function() {{
    try {{
        // Try Teams user profile endpoint
        var resp = await fetch('/api/mt/part/emea-02/beta/users/8:orgid:{orgId}/properties', {{
            credentials: 'include'
        }});
        if (!resp.ok) {{
            // Try search as fallback
            var existing = {JsonSerializer.Serialize(ContactDatabase.GetById(teamsUserId)?.DisplayName ?? "")};
            if (existing) {{
                var searchResp = await fetch('/api/mt/part/emea-02/beta/users/searchV2?includeDLs=false&includeBots=false&enableGuest=true&source=newChat&skypeTeamsInfo=true', {{
                    method: 'POST',
                    credentials: 'include',
                    headers: {{ 'Content-Type': 'application/json' }},
                    body: JSON.stringify({{ queryText: existing }})
                }});
                if (searchResp.ok) {{
                    var data = await searchResp.json();
                    var results = data.value || data.results || data || [];
                    if (Array.isArray(results)) {{
                        for (var i = 0; i < results.length; i++) {{
                            var r = results[i];
                            if (r.mri === '8:orgid:{orgId}' || r.objectId === '{orgId}') {{
                                return JSON.stringify({{
                                    email: r.email || r.sipAddress || r.userPrincipalName || null,
                                    displayName: r.displayName || null,
                                    jobTitle: r.jobTitle || null,
                                    department: r.department || null,
                                    phone: r.phoneNumber || r.phone || null
                                }});
                            }}
                        }}
                    }}
                }}
            }}
            return null;
        }}
        var data = await resp.json();
        return JSON.stringify({{
            email: data.email || data.sipAddress || data.userPrincipalName || null,
            displayName: data.displayName || null,
            jobTitle: data.jobTitle || null,
            department: data.department || null,
            phone: data.phoneNumber || data.phone || null
        }});
    }} catch(e) {{ return null; }}
}})();";

            try
            {
                // ExecuteScriptAsync resolves JS Promises natively
                var readResult = await _extractor.EvaluateJsAsync(js);

                if (readResult == null) return null;

                using var doc = JsonDocument.Parse(readResult);
                var root = doc.RootElement;

                var contact = ContactDatabase.GetById(teamsUserId) ?? new Contact { TeamsUserId = teamsUserId };
                contact.Email = root.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : contact.Email;
                contact.DisplayName = root.TryGetProperty("displayName", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : contact.DisplayName;
                contact.JobTitle = root.TryGetProperty("jobTitle", out var j) && j.ValueKind == JsonValueKind.String ? j.GetString() : contact.JobTitle;
                contact.Department = root.TryGetProperty("department", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : contact.Department;
                contact.Phone = root.TryGetProperty("phone", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : contact.Phone;
                contact.EnrichmentStatus = contact.Email != null ? EnrichmentStatus.Enriched : EnrichmentStatus.Failed;
                contact.LastUpdated = DateTime.Now;

                Log.Information("Enriched contact: {Name} → {Email}", contact.DisplayName, contact.Email ?? "(no email)");
                return contact;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Teams People API enrichment failed for {Id}", teamsUserId);
                return null;
            }
        }
    }
}
```

- [ ] **Step 2: Build to verify**

Run:
```bash
cd MeetNow && dotnet build
```

- [ ] **Step 3: Commit**

```bash
git add MeetNow/ContactEnricher.cs
git commit -m "feat: add ContactEnricher with Teams People API resolution"
```

---

### Task 4: Wire ContactEnricher into TeamsWebViewWindow

**Files:**
- Modify: `MeetNow/TeamsWebViewWindow.xaml.cs` (add enricher field, init, dispose)
- Modify: `MeetNow/TeamsWebViewDataExtractor.cs` (queue enrichment after auto-discovery)

- [ ] **Step 1: Add enricher to TeamsWebViewWindow**

In `MeetNow/TeamsWebViewWindow.xaml.cs`, add a field:

```csharp
private ContactEnricher? _enricher;
```

In `InitializeWebView()`, after the extractor is created and attached, add:

```csharp
_enricher = new ContactEnricher(_extractor);
_enricher.Start();
```

Add a public property:

```csharp
public ContactEnricher? Enricher => _enricher;
```

In `DisposeWebView()`, add before `_extractor?.StopJsProbing()`:

```csharp
_enricher?.Stop();
```

- [ ] **Step 2: Add enrichment queueing to auto-discovery**

In `TeamsWebViewDataExtractor.cs`, add an event that fires when a new contact is discovered:

```csharp
public event Action<string>? ContactDiscovered;
```

In `TryExtractContact`, after each `ContactDatabase.Upsert(...)` call, add:

```csharp
ContactDiscovered?.Invoke(teamsUserId);
```

(For pattern 2, use `userId` instead of `teamsUserId`.)

Then in `TeamsWebViewWindow.xaml.cs`, after creating the enricher, subscribe:

```csharp
_extractor.ContactDiscovered += userId => _enricher?.Enqueue(userId);
```

- [ ] **Step 3: Build and verify**

Run:
```bash
cd MeetNow && dotnet build
```

- [ ] **Step 4: Commit**

```bash
git add MeetNow/TeamsWebViewWindow.xaml.cs MeetNow/TeamsWebViewDataExtractor.cs
git commit -m "feat: wire ContactEnricher into WebView2 pipeline with auto-discovery events"
```

---

### Task 5: Create FindPersonWindow

**Files:**
- Create: `MeetNow/FindPersonWindow.xaml`
- Create: `MeetNow/FindPersonWindow.xaml.cs`

- [ ] **Step 1: Create the XAML**

Create `MeetNow/FindPersonWindow.xaml`:

```xml
<Window x:Class="MeetNow.FindPersonWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Find Person" Width="500" Height="450"
        WindowStartupLocation="CenterScreen"
        Background="#1F1F1F" Foreground="#DDD"
        Topmost="True" ResizeMode="CanResize">
    <Grid Margin="12">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <TextBox x:Name="SearchBox" Grid.Row="0" FontSize="14"
                 Background="#2A2A2A" Foreground="#EEE" BorderBrush="#444"
                 Padding="8,6" Margin="0,0,0,8"
                 TextChanged="SearchBox_TextChanged"/>

        <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto">
            <StackPanel x:Name="ResultsPanel"/>
        </ScrollViewer>

        <TextBlock x:Name="StatusText" Grid.Row="2" Foreground="#888"
                   FontSize="11" Margin="0,8,0,0"/>
    </Grid>
</Window>
```

- [ ] **Step 2: Create the code-behind**

Create `MeetNow/FindPersonWindow.xaml.cs`:

```csharp
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

            var storeJs = @"
(async function() {
    try {
        var resp = await fetch('/api/mt/part/emea-02/beta/users/searchV2?includeDLs=false&includeBots=false&enableGuest=true&source=newChat&skypeTeamsInfo=true', {
            method: 'POST',
            credentials: 'include',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ queryText: '" + query.Replace("'", "\\'") + @"' })
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
            var resultJson = await ext.EvaluateJsAsync(storeJs);

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
```

- [ ] **Step 3: Build to verify**

Run:
```bash
cd MeetNow && dotnet build
```

- [ ] **Step 4: Commit**

```bash
git add MeetNow/FindPersonWindow.xaml MeetNow/FindPersonWindow.xaml.cs
git commit -m "feat: add FindPersonWindow with search, remote lookup, and pin support"
```

---

### Task 6: Wire FindPersonWindow and ContactDatabase into MainWindow

**Files:**
- Modify: `MeetNow/MainWindow.xaml:21` (add Find Person menu item)
- Modify: `MeetNow/MainWindow.xaml.cs` (click handler, init ContactDatabase, dispose)

- [ ] **Step 1: Add tray menu item**

In `MeetNow/MainWindow.xaml`, add after the "Teams WebView" MenuItem (line 21):

```xml
<MenuItem Header="Find Person" Click="MenuItem_FindPersonClick"/>
```

- [ ] **Step 2: Add field and handlers to MainWindow.xaml.cs**

Add a field:

```csharp
private FindPersonWindow? _findPersonWindow;
```

Add the click handler:

```csharp
private void MenuItem_FindPersonClick(object sender, RoutedEventArgs e)
{
    if (_findPersonWindow == null)
    {
        _findPersonWindow = new FindPersonWindow(
            () => _teamsWebViewWindow?.Extractor,
            () => _teamsWebViewWindow?.Enricher);
    }

    if (_findPersonWindow.IsVisible)
        _findPersonWindow.Hide();
    else
        _findPersonWindow.Show();
}
```

In the `OnClosed` override, add before `_teamsWebViewWindow?.DisposeWebView()`:

```csharp
ContactDatabase.FlushAndDispose();
```

- [ ] **Step 3: Build and manually test**

Run:
```bash
cd MeetNow && dotnet build
```

Manual test: Run the app, let Teams load in WebView2, then click "Find Person" in the tray menu. The search dialog should open. Type a name — local results from auto-discovered contacts should appear. Pinning should toggle the star.

- [ ] **Step 4: Commit**

```bash
git add MeetNow/MainWindow.xaml MeetNow/MainWindow.xaml.cs
git commit -m "feat: wire FindPersonWindow and ContactDatabase into MainWindow"
```

---

### Task 7: Manual Validation

**Files:** None (testing only)

- [ ] **Step 1: Full app test**

Run the app. Verify:
1. WebView2 opens, loads Teams, auto-logs in
2. Navigate around in Teams (chats) — profile picture URLs generate contacts
3. Check `%LOCALAPPDATA%\MeetNow\contacts.json` — should contain discovered contacts with TeamsUserId and DisplayName
4. Open "Find Person" from tray menu
5. Search for a colleague's name — should show local results and trigger remote search
6. Pin a contact — star fills, contact persists across app restart
7. Enrichment runs — after a few seconds, enriched contacts should show email/title

- [ ] **Step 2: Verify contacts.json**

Check the file has the expected structure:
```bash
cat "%LOCALAPPDATA%\MeetNow\contacts.json" | head -50
```

Should show contacts with `TeamsUserId`, `DisplayName`, and potentially `Email`, `JobTitle` for enriched contacts.
