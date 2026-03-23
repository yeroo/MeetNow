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

                Log.Information("Enriched contact: {Name} -> {Email}", contact.DisplayName, contact.Email ?? "(no email)");
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
