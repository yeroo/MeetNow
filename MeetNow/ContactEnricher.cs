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
            // DISABLED: Enrichment API calls return 401 and repeated failures
            // could trigger IT security alerts. Re-enable once we have a safe
            // token acquisition strategy (e.g. intercepting tokens from actual
            // Teams API calls that succeed).
            return;

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
            // 5-second interval to allow WS hook to capture bearer token first
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
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

            var displayName = ContactDatabase.GetById(teamsUserId)?.DisplayName ?? "";
            var safeDisplayName = displayName.Replace("'", "\\'").Replace("\\", "\\\\");

            var js = $@"
(function() {{
    window.__meetNowEnrich = undefined;
    (async function() {{
    try {{
        // Use the bearer token captured from C# network interceptor
        var token = window.__meetNowCapturedToken;
        if (!token) {{
            window.__meetNowEnrich = JSON.stringify({{ error: 'no captured token' }});
            return;
        }}

        // Step 2: Search with the bearer token
        var searchResp = await fetch('/api/mt/part/emea-02/beta/users/searchV2?includeDLs=false&includeBots=false&enableGuest=true&source=newChat&skypeTeamsInfo=true', {{
            method: 'POST',
            credentials: 'include',
            headers: {{
                'Content-Type': 'application/json',
                'Authorization': 'Bearer ' + token
            }},
            body: JSON.stringify({{ queryText: '{safeDisplayName}' }})
        }});
        if (!searchResp.ok) {{
            window.__meetNowEnrich = JSON.stringify({{ error: 'search status ' + searchResp.status }});
            return;
        }}
        var data = await searchResp.json();
        window.__meetNowEnrich = JSON.stringify({{
            rawKeys: Object.keys(data),
            resultCount: (data.value || data.results || []).length,
            firstResult: (data.value || data.results || [])[0] || null,
            tokenKeyUsed: Object.keys(tokens || {{}})[0]
        }});
    }} catch(e) {{ window.__meetNowEnrich = JSON.stringify({{ error: e.message }}); }}
}})();
return 'started';
}})();";

            try
            {
                // Inject the captured bearer token into the page
                var capturedToken = _extractor.CapturedBearerToken;
                if (capturedToken != null)
                {
                    await _extractor.EvaluateJsAsync(
                        $"(function() {{ window.__meetNowCapturedToken = '{capturedToken}'; return 'ok'; }})();");
                }

                // Inject — the outer sync IIFE returns 'started', inner async stores result in global
                var injectResult = await _extractor.EvaluateJsAsync(js);
                Log.Information("Enrich inject for {Id}: {Result}", teamsUserId, injectResult);

                // Wait for async fetch to complete
                await Task.Delay(5000);

                // Read the result
                var readResult = await _extractor.EvaluateJsAsync(
                    "(function() { var r = window.__meetNowEnrich; window.__meetNowEnrich = undefined; return typeof r === 'undefined' ? '__UNDEFINED__' : (r === null ? '__NULL__' : r); })();");
                Log.Information("Enrich read for {Id}: {Result}", teamsUserId, readResult ?? "(c# null)");
                if (readResult == "__UNDEFINED__" || readResult == "__NULL__") readResult = null;

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
