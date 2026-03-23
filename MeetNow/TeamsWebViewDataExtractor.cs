using Microsoft.Web.WebView2.Core;
using MeetNow.Models;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace MeetNow
{
    public class TeamsWebViewDataExtractor
    {
        private static readonly string TrafficLogPath = Path.Combine(
            Path.GetTempPath(), "MeetNow_WebView_Traffic.log");

        // URL patterns to capture response bodies for
        private static readonly string[] InterestingPatterns = new[]
        {
            "/api/calendar/",
            "/me/calendarview",
            "/api/chatsvc/",
            "/messages",
            "/threads",
            "/presence/",
            "/status",
            "/api/mt/",
            "/api/csa/",
            "/owa/",
            "/calendar/api/",
            "outlook.office.com/api/",
            "outlook.office365.com/api/",
            "substrate.office.com/",
            "outlook.cloud.microsoft/",
            "startupdata.ashx",
            "service.svc",
            "loki.delve.office.com/api/",
        };

        private CoreWebView2? _webView;
        private readonly bool _logAllTraffic;
        private readonly ConcurrentBag<TeamsMeeting> _meetings = new();
        private readonly ConcurrentBag<TeamsMessage> _messages = new();
        private DispatcherTimer? _jsProbeTimer;
        private bool _jsProbingStarted;

        private string? _capturedBearerToken;
        public string? CapturedBearerToken => _capturedBearerToken;

        public event Action<TeamsMeeting>? MeetingDetected;
        public event Action<TeamsMessage>? MessageDetected;
        public event Action<string>? ContactDiscovered;

        public TeamsWebViewDataExtractor(bool logAllTraffic = true)
        {
            _logAllTraffic = logAllTraffic;
        }

        public void Attach(CoreWebView2 webView)
        {
            _webView = webView;
            _webView.WebResourceResponseReceived += OnResponseReceived;
            Log.Information("TeamsWebViewDataExtractor attached to WebView2");

            if (_logAllTraffic)
            {
                Log.Information("Traffic logging enabled: {Path}", TrafficLogPath);
            }
        }

        public void Detach()
        {
            if (_webView != null)
            {
                _webView.WebResourceResponseReceived -= OnResponseReceived;
                _webView = null;
            }
        }

        public IReadOnlyList<TeamsMeeting> GetMeetings() => _meetings.ToList();
        public IReadOnlyList<TeamsMessage> GetMessages() => _messages.ToList();

        private async void OnResponseReceived(object? sender,
            CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            try
            {
                var uri = e.Request.Uri;
                var status = e.Response.StatusCode;
                var contentType = e.Response.Headers.GetHeader("Content-Type") ?? "";

                // Log all traffic if enabled
                if (_logAllTraffic)
                {
                    var method = e.Request.Method;
                    LogTraffic($"{method} {status} {contentType} {uri}");
                }

                // Auto-discover contacts from profile picture URLs
                TryExtractContact(uri);

                // Capture auth token from Teams API requests
                if (uri.Contains("/api/mt/", StringComparison.OrdinalIgnoreCase)
                    || uri.Contains("/api/chatsvc/", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var authHeader = e.Request.Headers.GetHeader("Authorization");
                        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        {
                            _capturedBearerToken = authHeader["Bearer ".Length..];
                        }
                    }
                    catch { }
                }

                // Only process JSON (or x-javascript from Outlook) from interesting endpoints
                if (status < 200 || status >= 300) return;
                var isJson = contentType.Contains("json", StringComparison.OrdinalIgnoreCase);
                var isOutlookJs = contentType.Contains("x-javascript", StringComparison.OrdinalIgnoreCase)
                    && (uri.Contains("outlook.office.com", StringComparison.OrdinalIgnoreCase)
                        || uri.Contains("outlook.cloud.microsoft", StringComparison.OrdinalIgnoreCase));
                if (!isJson && !isOutlookJs) return;
                if (!IsInteresting(uri)) return;

                // Try to read the response body
                var body = await ReadResponseBody(e.Response);
                if (body == null) return;

                // Write full body to separate file for large responses
                if (body.Length > 1000)
                {
                    try
                    {
                        var bodyLogPath = Path.Combine(Path.GetTempPath(), "MeetNow_WebView_Bodies.log");
                        File.AppendAllText(bodyLogPath,
                            $"[{DateTime.Now:HH:mm:ss.fff}] {uri}{Environment.NewLine}{body}{Environment.NewLine}---{Environment.NewLine}");
                    }
                    catch { }
                }
                // Passively enrich contacts from GetPersona responses
                TryEnrichFromGetPersona(uri, body);

                LogTraffic($"  BODY ({body.Length} chars): {Truncate(body, 500)}");
                Log.Debug("Captured response from {Uri} ({Length} chars)", uri, body.Length);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error processing WebView2 response");
            }
        }

        private static async Task<string?> ReadResponseBody(CoreWebView2WebResourceResponseView response)
        {
            try
            {
                var stream = await response.GetContentAsync();
                if (stream == null) return null;

                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not read response body");
                return null;
            }
        }

        private static bool IsInteresting(string uri)
        {
            return InterestingPatterns.Any(p =>
                uri.Contains(p, StringComparison.OrdinalIgnoreCase));
        }

        private static void LogTraffic(string line)
        {
            try
            {
                File.AppendAllText(TrafficLogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
            }
            catch
            {
                // Swallow file write errors — logging is best-effort
            }
        }

        private static string Truncate(string s, int maxLength)
        {
            return s.Length <= maxLength ? s : s[..maxLength] + "...";
        }

        private void TryEnrichFromGetPersona(string uri, string body)
        {
            if (!uri.Contains("action=GetPersona", StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var persona = doc.RootElement.GetProperty("Body").GetProperty("Persona");

                var displayName = persona.TryGetProperty("DisplayName", out var dn) ? dn.GetString() : null;
                var email = persona.TryGetProperty("EmailAddress", out var ea)
                    && ea.TryGetProperty("EmailAddress", out var addr) ? addr.GetString() : null;
                var title = persona.TryGetProperty("Title", out var t) ? t.GetString() : null;
                var department = persona.TryGetProperty("Department", out var d) ? d.GetString() : null;
                var company = persona.TryGetProperty("CompanyName", out var c) ? c.GetString() : null;
                var phone = persona.TryGetProperty("BusinessPhoneNumbersArray", out var phones)
                    && phones.GetArrayLength() > 0
                    ? phones[0].GetProperty("Value").GetProperty("Number").GetString() : null;
                var adObjectId = persona.TryGetProperty("ADObjectId", out var ad) ? ad.GetString() : null;

                if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(email)) return;

                // Map ADObjectId to TeamsUserId format
                var teamsUserId = !string.IsNullOrEmpty(adObjectId) ? $"8:orgid:{adObjectId}" : null;

                // Try to find existing contact by email or name
                var existing = teamsUserId != null ? ContactDatabase.GetById(teamsUserId) : null;
                existing ??= ContactDatabase.GetByEmail(email);
                if (existing == null)
                {
                    var nameMatches = ContactDatabase.GetByName(displayName);
                    existing = nameMatches.Count > 0 ? nameMatches[0] : null;
                }

                var contact = existing ?? new Models.Contact();
                contact.TeamsUserId = existing?.TeamsUserId ?? teamsUserId ?? $"ad:{adObjectId}";
                contact.DisplayName = displayName;
                contact.Email = email;
                contact.JobTitle = title ?? contact.JobTitle;
                contact.Department = department ?? contact.Department;
                contact.Phone = phone ?? contact.Phone;
                contact.LastSeenTimestamp = DateTime.Now;
                contact.EnrichmentStatus = Models.EnrichmentStatus.Enriched;

                ContactDatabase.Upsert(contact);
                Log.Information("Contact enriched from GetPersona: {Name} <{Email}> [{Title}]",
                    displayName, email, title ?? "");
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to parse GetPersona response");
            }
        }

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
                        ContactDiscovered?.Invoke(teamsUserId);
                    }
                }

                // Pattern 2: Loki Delve person API — URL contains teamsMri and smtp (email)
        // e.g. loki.delve.office.com/api/v2/person?...teamsMri=8:orgid:GUID&smtp=email@shell.com
        if (uri.Contains("loki.delve.office.com/api/", StringComparison.OrdinalIgnoreCase)
            && uri.Contains("teamsMri=", StringComparison.OrdinalIgnoreCase)
            && uri.Contains("smtp=", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string? teamsMri = null;
                string? email = null;

                var mriParam = "teamsMri=";
                var mriStart = uri.IndexOf(mriParam, StringComparison.OrdinalIgnoreCase);
                if (mriStart >= 0)
                {
                    mriStart += mriParam.Length;
                    var mriEnd = uri.IndexOf('&', mriStart);
                    teamsMri = Uri.UnescapeDataString(mriEnd >= 0 ? uri[mriStart..mriEnd] : uri[mriStart..]);
                }

                var smtpParam = "smtp=";
                var smtpStart = uri.IndexOf(smtpParam, StringComparison.OrdinalIgnoreCase);
                if (smtpStart >= 0)
                {
                    smtpStart += smtpParam.Length;
                    var smtpEnd = uri.IndexOf('&', smtpStart);
                    email = Uri.UnescapeDataString(smtpEnd >= 0 ? uri[smtpStart..smtpEnd] : uri[smtpStart..]);
                }

                if (!string.IsNullOrWhiteSpace(teamsMri) && !string.IsNullOrWhiteSpace(email))
                {
                    var existing = ContactDatabase.GetById(teamsMri);
                    ContactDatabase.Upsert(new Models.Contact
                    {
                        TeamsUserId = teamsMri,
                        DisplayName = existing?.DisplayName ?? "",
                        Email = email,
                        LastSeenTimestamp = DateTime.Now,
                        Source = existing?.Source ?? Models.ContactSource.Chat,
                        EnrichmentStatus = Models.EnrichmentStatus.Enriched
                    });
                    ContactDiscovered?.Invoke(teamsMri);
                    Log.Information("Contact enriched from Loki URL: {Email} ({Id})", email, teamsMri);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to extract contact from Loki URL");
            }
        }

        // Pattern 3: /mergedProfilePicturev2?usersInfo=[{userId, displayName}]
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
                            ContactDiscovered?.Invoke(userId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to extract contact from URL");
            }
        }

        private bool _hooksInjected;

        public async void StartJsProbing(int intervalSeconds = 30)
        {
            if (_jsProbingStarted) return; // Guard against multiple navigation events
            _jsProbingStarted = true;

            // Inject WebSocket hook and webpack explorer once
            if (!_hooksInjected && _webView != null)
            {
                await InjectWebSocketHook();
                await InjectWebpackExplorer();
                _hooksInjected = true;
            }

            // Run calendar discovery once after hooks are ready
            _ = DiscoverCalendarDataAsync();

            _jsProbeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(intervalSeconds)
            };
            _jsProbeTimer.Tick += async (s, e) => await ProbeTeamsStateAsync();
            _jsProbeTimer.Start();

            Log.Information("JS state probing started (every {Interval}s)", intervalSeconds);
        }

        public void StopJsProbing()
        {
            _jsProbeTimer?.Stop();
            _jsProbeTimer = null;
            _jsProbingStarted = false;
        }

        /// <summary>
        /// Monkey-patch WebSocket to intercept all frames sent/received by Teams.
        /// Stores recent messages in window.__meetNowWsMessages (circular buffer).
        /// </summary>
        private async Task InjectWebSocketHook()
        {
            if (_webView == null) return;

            var hookJs = @"
(function() {
    if (window.__meetNowWsHooked) return 'already_hooked';
    window.__meetNowWsHooked = true;
    window.__meetNowWsMessages = [];
    window.__meetNowWsStats = { sent: 0, received: 0, connections: 0 };
    var MAX_MESSAGES = 200;

    var OrigWS = window.WebSocket;
    window.WebSocket = function(url, protocols) {
        var ws = protocols ? new OrigWS(url, protocols) : new OrigWS(url);
        window.__meetNowWsStats.connections++;

        var pushMsg = function(dir, data) {
            // Capture bearer token from auth messages and store permanently
            if (typeof data === 'string' && data.indexOf('Bearer ') >= 0 && !window.__meetNowBearerToken) {
                var tokenMatch = data.match(/Bearer\s+(eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+)/);
                if (tokenMatch) window.__meetNowBearerToken = tokenMatch[1];
            }
            var entry = { dir: dir, ts: Date.now(), url: url };
            if (typeof data === 'string') {
                entry.data = data.substring(0, 2000);
                entry.len = data.length;
            } else {
                entry.data = '[binary ' + (data.byteLength || data.size || '?') + ' bytes]';
                entry.len = data.byteLength || data.size || 0;
            }
            window.__meetNowWsMessages.push(entry);
            if (window.__meetNowWsMessages.length > MAX_MESSAGES) {
                window.__meetNowWsMessages.shift();
            }
        };

        // Intercept send
        var origSend = ws.send.bind(ws);
        ws.send = function(data) {
            window.__meetNowWsStats.sent++;
            pushMsg('out', data);
            return origSend(data);
        };

        // Intercept incoming messages
        ws.addEventListener('message', function(event) {
            window.__meetNowWsStats.received++;
            pushMsg('in', event.data);
        });

        return ws;
    };
    window.WebSocket.prototype = OrigWS.prototype;
    window.WebSocket.CONNECTING = OrigWS.CONNECTING;
    window.WebSocket.OPEN = OrigWS.OPEN;
    window.WebSocket.CLOSING = OrigWS.CLOSING;
    window.WebSocket.CLOSED = OrigWS.CLOSED;

    return 'hooked';
})();";

            try
            {
                var result = await _webView.ExecuteScriptAsync(hookJs);
                LogTraffic($"  WS_HOOK: {result}");
                Log.Information("WebSocket hook injection result: {Result}", result);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to inject WebSocket hook");
            }
        }

        /// <summary>
        /// Tap into webpack's module cache to find Teams internal state.
        /// Stores discovered modules in window.__meetNowWebpackInfo.
        /// </summary>
        private async Task InjectWebpackExplorer()
        {
            if (_webView == null) return;

            var explorerJs = @"
(function() {
    if (window.__meetNowWebpackExplored) return 'already_explored';
    window.__meetNowWebpackExplored = true;
    window.__meetNowWebpackInfo = { modules: 0, interesting: [] };

    try {
        // Tap into webpack chunk loading to get __webpack_require__
        var chunkName = 'webpackChunk_msteams_react_web_client';
        var chunks = window[chunkName];
        if (!chunks) return JSON.stringify({ error: 'no chunks found' });

        // Push a fake module to get access to __webpack_require__
        var wpRequire = null;
        chunks.push([['__meetNow_probe__'], {}, function(require) {
            wpRequire = require;
        }]);

        if (!wpRequire || !wpRequire.c) {
            return JSON.stringify({ error: 'could not get webpack require', hasRequire: !!wpRequire });
        }

        var moduleCache = wpRequire.c;
        var moduleIds = Object.keys(moduleCache);
        window.__meetNowWebpackInfo.modules = moduleIds.length;
        window.__meetNowWebpackRequire = wpRequire;

        // Scan modules for interesting exports
        var interesting = [];
        for (var i = 0; i < moduleIds.length && interesting.length < 50; i++) {
            try {
                var mod = moduleCache[moduleIds[i]];
                if (!mod || !mod.exports) continue;
                var exp = mod.exports;
                var keys = Object.keys(exp);

                // Look for stores, services, state containers
                for (var j = 0; j < keys.length; j++) {
                    var k = keys[j].toLowerCase();
                    if (k.indexOf('calendar') >= 0 || k.indexOf('presence') >= 0 ||
                        k.indexOf('chat') >= 0 || k.indexOf('message') >= 0 ||
                        k.indexOf('meeting') >= 0 || k.indexOf('status') >= 0 ||
                        k.indexOf('store') >= 0 || k.indexOf('state') >= 0 ||
                        k.indexOf('event') >= 0 || k.indexOf('notification') >= 0) {
                        var val = exp[keys[j]];
                        var valType = typeof val;
                        if (valType === 'function' || valType === 'object') {
                            interesting.push({
                                moduleId: moduleIds[i],
                                key: keys[j],
                                type: valType,
                                isClass: valType === 'function' && /^class\s/.test(val.toString().substring(0, 10)),
                                proto: valType === 'object' && val !== null ? Object.keys(val).slice(0, 10) : null
                            });
                        }
                    }
                }
            } catch(e) { /* skip inaccessible modules */ }
        }
        window.__meetNowWebpackInfo.interesting = interesting;
        return JSON.stringify({ modules: moduleIds.length, interesting: interesting.length });
    } catch(e) {
        return JSON.stringify({ error: e.message });
    }
})();";

            try
            {
                var result = await _webView.ExecuteScriptAsync(explorerJs);
                var inner = result != null && result != "null"
                    ? JsonSerializer.Deserialize<string>(result) : result;
                LogTraffic($"  WEBPACK_EXPLORE: {inner}");
                Log.Information("Webpack explorer result: {Result}", inner);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to inject webpack explorer");
            }
        }

        /// <summary>
        /// One-shot calendar data discovery via multiple approaches.
        /// Since ExecuteScriptAsync can't await JS Promises, we inject async code
        /// that stores results in window.__meetNowCal, then read them after a delay.
        /// </summary>
        private async Task DiscoverCalendarDataAsync()
        {
            if (_webView == null) return;

            // Wait for Teams to fully initialize
            await Task.Delay(15000);

            LogTraffic("  === CALENDAR DISCOVERY START ===");

            // Step 1: Inject all async fetches — they store results in window.__meetNowCal
            var injectJs = @"
(function() {
    window.__meetNowCal = { status: 'running' };

    // Helper to get today's ISO date range
    var now = new Date();
    var start = new Date(now.getFullYear(), now.getMonth(), now.getDate()).toISOString();
    var end = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1).toISOString();

    // Approach 1: Graph API — acquire token via MSAL or find PublicClientApplication
    (async function() {
        try {
            var token = null;

            // Strategy A: Find MSAL PublicClientApplication in webpack modules
            var wpReq = window.__meetNowWebpackRequire;
            if (wpReq && wpReq.c) {
                var cache = wpReq.c;
                var ids = Object.keys(cache);
                for (var i = 0; i < ids.length && !token; i++) {
                    try {
                        var mod = cache[ids[i]];
                        if (!mod || !mod.exports) continue;
                        var exp = mod.exports;
                        var keys = Object.keys(exp);
                        for (var j = 0; j < keys.length; j++) {
                            var v = exp[keys[j]];
                            if (v && typeof v === 'object' && typeof v.acquireTokenSilent === 'function') {
                                try {
                                    var accounts = v.getAllAccounts ? v.getAllAccounts() : [];
                                    if (accounts.length > 0) {
                                        var tokenResp = await v.acquireTokenSilent({
                                            scopes: ['Calendars.Read'],
                                            account: accounts[0]
                                        });
                                        token = tokenResp.accessToken;
                                        window.__meetNowCal.msalSource = 'webpack:' + ids[i] + '.' + keys[j];
                                        break;
                                    }
                                } catch(te) {
                                    window.__meetNowCal.msalTokenError = te.message;
                                }
                            }
                        }
                    } catch(e) {}
                }
            }

            // Strategy B: Try getting token from Teams' internal auth service
            if (!token) {
                try {
                    var authResp = await fetch('/api/authsvc/v1.0/authz', {
                        method: 'POST',
                        credentials: 'include',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ resourceUrl: 'https://graph.microsoft.com' })
                    });
                    if (authResp.ok) {
                        var authData = await authResp.json();
                        token = authData.accessToken || authData.token || (authData.tokens && authData.tokens[0] && authData.tokens[0].accessToken);
                        window.__meetNowCal.authSvcResponse = Object.keys(authData);
                    } else {
                        window.__meetNowCal.authSvcStatus = authResp.status;
                    }
                } catch(ae) {
                    window.__meetNowCal.authSvcError = ae.message;
                }
            }

            if (!token) {
                window.__meetNowCal.graph = { error: 'could not acquire Graph token' };
                return;
            }

            var url = 'https://graph.microsoft.com/v1.0/me/calendarview?startDateTime=' +
                encodeURIComponent(start) + '&endDateTime=' + encodeURIComponent(end) +
                '&$select=subject,start,end,organizer,location,onlineMeeting,isOnlineMeeting,onlineMeetingUrl,isCancelled,responseStatus&$top=50';
            var resp = await fetch(url, {
                headers: { 'Authorization': 'Bearer ' + token }
            });
            var data = await resp.json();
            window.__meetNowCal.graph = {
                status: resp.status,
                count: data.value ? data.value.length : 0,
                events: data.value ? data.value.map(function(e) {
                    return { subject: e.subject, start: e.start, end: e.end,
                        organizer: e.organizer ? e.organizer.emailAddress : null,
                        location: e.location ? e.location.displayName : null,
                        isOnline: e.isOnlineMeeting,
                        joinUrl: e.onlineMeetingUrl || (e.onlineMeeting ? e.onlineMeeting.joinUrl : null),
                        response: e.responseStatus ? e.responseStatus.response : null,
                        cancelled: e.isCancelled };
                }) : [],
                error: data.error ? data.error.message : null
            };
        } catch(e) { window.__meetNowCal.graph = { error: e.message }; }
    })();

    // Approach 2: Try authorizeResource to get a Graph-scoped token
    (async function() {
        try {
            var wpReq = window.__meetNowWebpackRequire;
            if (!wpReq || !wpReq.c) { window.__meetNowCal.authRes = { error: 'no webpack' }; return; }
            var mod37887 = wpReq.c['37887'];
            if (!mod37887 || !mod37887.exports || !mod37887.exports.authorizeResource) {
                window.__meetNowCal.authRes = { error: 'authorizeResource not found' };
                return;
            }
            // Try calling authorizeResource — it's fn(5), we need to figure out the args
            // Log its source to understand what it expects
            var fn = mod37887.exports.authorizeResource;
            window.__meetNowCal.authRes = {
                fnLength: fn.length,
                fnSource: fn.toString().substring(0, 500)
            };
        } catch(e) { window.__meetNowCal.authRes = { error: e.message }; }
    })();

    // Approach 3: Navigate to calendar tab and intercept the iframe's network
    // Instead of fetching directly, look at what URLs the calendar iframe loads
    (function() {
        try {
            var iframes = document.querySelectorAll('iframe');
            var calIframes = [];
            for (var i = 0; i < iframes.length; i++) {
                var src = iframes[i].src || '';
                if (src.indexOf('outlook') >= 0 || src.indexOf('calendar') >= 0 || src.indexOf('substrate') >= 0) {
                    calIframes.push({ src: src.substring(0, 300), id: iframes[i].id, name: iframes[i].name });
                }
            }
            window.__meetNowCal.calendarIframes = calIframes;
            window.__meetNowCal.totalIframes = iframes.length;
        } catch(e) { window.__meetNowCal.iframeError = e.message; }
    })();

    // Approach 4: Try Substrate calendar API (what Outlook uses internally)
    (async function() {
        try {
            var resp = await fetch('https://substrate.office.com/calendar/api/v2/views/daily?startDate=' +
                start.split('T')[0] + '&endDate=' + end.split('T')[0], {
                credentials: 'include',
                headers: { 'x-anchormailbox': 'UPN:' }
            });
            var text = await resp.text();
            window.__meetNowCal.substrate = { status: resp.status, bodyLen: text.length, preview: text.substring(0, 3000) };
        } catch(e) { window.__meetNowCal.substrate = { error: e.message }; }
    })();

    // Approach 4: Scan webpack for any auth/token provider objects
    (function() {
        try {
            var wpReq = window.__meetNowWebpackRequire;
            if (!wpReq || !wpReq.c) return;
            var cache = wpReq.c;
            var ids = Object.keys(cache);
            var authModules = [];
            for (var i = 0; i < ids.length && authModules.length < 20; i++) {
                try {
                    var mod = cache[ids[i]];
                    if (!mod || !mod.exports) continue;
                    var keys = Object.keys(mod.exports);
                    for (var j = 0; j < keys.length; j++) {
                        var kl = keys[j].toLowerCase();
                        if (kl.indexOf('token') >= 0 || kl.indexOf('auth') >= 0 ||
                            kl.indexOf('msal') >= 0 || kl.indexOf('credential') >= 0) {
                            var v = mod.exports[keys[j]];
                            var info = { mod: ids[i], key: keys[j], type: typeof v };
                            if (typeof v === 'object' && v !== null) {
                                info.methods = Object.keys(v).filter(function(k) { return typeof v[k] === 'function'; }).slice(0, 10);
                                info.props = Object.keys(v).filter(function(k) { return typeof v[k] !== 'function'; }).slice(0, 10);
                            }
                            if (typeof v === 'function') {
                                info.fnArgs = v.length;
                                info.fnName = v.name || '';
                            }
                            authModules.push(info);
                        }
                    }
                } catch(e) {}
            }
            window.__meetNowCal.authModules = authModules;
        } catch(e) { window.__meetNowCal.authModulesError = e.message; }
    })();

    // Approach 5: Try Teams' internal token endpoint
    (async function() {
        try {
            var resp = await fetch('/trap/tokens', { credentials: 'include' });
            var text = await resp.text();
            window.__meetNowCal.trapTokens = { status: resp.status, len: text.length, preview: text.substring(0, 1000) };
        } catch(e) { window.__meetNowCal.trapTokens = { error: e.message }; }
    })();

    // Mark done after all fire (they run in parallel)
    setTimeout(function() { window.__meetNowCal.status = 'done'; }, 10000);

    return 'injected';
})();";

            try
            {
                await _webView.ExecuteScriptAsync(injectJs);
                LogTraffic("  CAL_INJECT: started async fetches");
            }
            catch (Exception ex)
            {
                LogTraffic($"  CAL_INJECT_ERROR: {ex.Message}");
                return;
            }

            // Step 2: Wait for results, then read them
            await Task.Delay(15000);

            var readJs = @"
(function() {
    var cal = window.__meetNowCal;
    if (!cal) return JSON.stringify({ error: 'no results object' });
    return JSON.stringify(cal);
})();";

            try
            {
                var readResult = await _webView.ExecuteScriptAsync(readJs);
                if (readResult != null && readResult != "null")
                {
                    var inner = JsonSerializer.Deserialize<string>(readResult);
                    if (inner != null)
                        LogTraffic($"  CAL_RESULTS: {Truncate(inner, 8000)}");
                }
            }
            catch (Exception ex)
            {
                LogTraffic($"  CAL_READ_ERROR: {ex.Message}");
            }

            // Step 3: Webpack calendar modules (sync, always works)
            var calModulesJs = @"
(function() {
    try {
        var wpReq = window.__meetNowWebpackRequire;
        if (!wpReq || !wpReq.c) return JSON.stringify({ error: 'no webpack' });
        var cache = wpReq.c;
        var ids = Object.keys(cache);
        var found = [];
        for (var i = 0; i < ids.length; i++) {
            try {
                var mod = cache[ids[i]];
                if (!mod || !mod.exports) continue;
                var exp = mod.exports;
                var keys = Object.keys(exp);
                for (var j = 0; j < keys.length; j++) {
                    var kl = keys[j].toLowerCase();
                    if (kl.indexOf('calendar') >= 0 || kl.indexOf('schedule') >= 0 ||
                        kl.indexOf('meeting') >= 0 || kl.indexOf('agenda') >= 0 ||
                        kl.indexOf('appointment') >= 0) {
                        var v = exp[keys[j]];
                        var vt = typeof v;
                        if (vt === 'function') {
                            found.push({ mod: ids[i], key: keys[j], type: 'fn', args: v.length, name: v.name || '' });
                        } else if (vt === 'object' && v !== null) {
                            var vKeys = Object.keys(v).slice(0, 20);
                            var methods = vKeys.filter(function(k) { return typeof v[k] === 'function'; });
                            if (methods.length > 0 || vKeys.length > 3) {
                                found.push({ mod: ids[i], key: keys[j], type: 'obj', allKeys: vKeys, methods: methods });
                            }
                        } else if (vt === 'string' && v.length < 200) {
                            found.push({ mod: ids[i], key: keys[j], type: 'str', val: v });
                        }
                    }
                }
            } catch(e) {}
        }
        return JSON.stringify({ total: found.length, modules: found });
    } catch(e) { return JSON.stringify({ error: e.message }); }
})();";

            try
            {
                var calModResult = await _webView.ExecuteScriptAsync(calModulesJs);
                if (calModResult != null && calModResult != "null")
                {
                    var inner = JsonSerializer.Deserialize<string>(calModResult);
                    if (inner != null)
                        LogTraffic($"  CAL_WEBPACK: {Truncate(inner, 6000)}");
                }
            }
            catch (Exception ex)
            {
                LogTraffic($"  CAL_WEBPACK_ERROR: {ex.Message}");
            }

            // Step 4: Navigate to Outlook calendar directly
            // The WebView2 shares cookies, so Outlook should be auto-authenticated.
            // The network interceptor will capture all Outlook API calls.
            LogTraffic("  CAL_OUTLOOK_NAV: Navigating to Outlook calendar...");
            try
            {
                _webView!.Navigate("https://outlook.office.com/calendar/view/day");

                // Wait for Outlook to load and make its API calls
                await Task.Delay(20000);

                // Read what we captured — check the traffic log for outlook.office.com JSON calls
                var outlookTrafficJs = @"
(function() {
    // Just confirm we're on Outlook now
    return JSON.stringify({
        title: document.title,
        url: window.location.href
    });
})();";
                var navResult = await _webView.ExecuteScriptAsync(outlookTrafficJs);
                if (navResult != null && navResult != "null")
                {
                    var inner = JsonSerializer.Deserialize<string>(navResult);
                    if (inner != null)
                        LogTraffic($"  CAL_OUTLOOK_PAGE: {inner}");
                }

                // Navigate back to Teams
                await Task.Delay(2000);
                _webView.Navigate("https://teams.microsoft.com");
                LogTraffic("  CAL_OUTLOOK_NAV: Navigated back to Teams");
            }
            catch (Exception ex)
            {
                LogTraffic($"  CAL_OUTLOOK_NAV_ERROR: {ex.Message}");
            }

            LogTraffic("  === CALENDAR DISCOVERY END ===");
        }

        private async Task ProbeTeamsStateAsync()
        {
            if (_webView == null) return;

            try
            {
                // Probe 1: Harvest ALL WebSocket messages (log every frame)
                var wsHarvestJs = @"
(function() {
    try {
        var result = { stats: window.__meetNowWsStats || {} };
        var msgs = window.__meetNowWsMessages || [];
        result.totalBuffered = msgs.length;

        // Log ALL messages, not just filtered ones
        result.messages = [];
        for (var i = 0; i < msgs.length; i++) {
            var m = msgs[i];
            // Parse Trouter frame format: '5:N+::{json}' or '5:N::{json}'
            var parsed = null;
            if (typeof m.data === 'string' && m.data.indexOf('::{') >= 0) {
                try {
                    var jsonStart = m.data.indexOf('::{') + 2;
                    parsed = JSON.parse(m.data.substring(jsonStart));
                } catch(e) {}
            } else if (typeof m.data === 'string' && m.data.charAt(0) === '{') {
                try { parsed = JSON.parse(m.data); } catch(e) {}
            }
            result.messages.push({
                dir: m.dir,
                ts: m.ts,
                url: m.url ? m.url.substring(0, 60) : '',
                len: m.len,
                name: parsed && parsed.name ? parsed.name : null,
                preview: typeof m.data === 'string' ? m.data.substring(0, 300) : m.data
            });
        }

        window.__meetNowWsMessages = [];
        return JSON.stringify(result);
    } catch(e) {
        return JSON.stringify({ error: e.message });
    }
})();";

                var wsResult = await _webView.ExecuteScriptAsync(wsHarvestJs);
                if (wsResult != null && wsResult != "null")
                {
                    var inner = JsonSerializer.Deserialize<string>(wsResult);
                    if (inner != null && !inner.Contains("\"messages\":[]"))
                    {
                        LogTraffic($"  WS_ALL: {Truncate(inner, 4000)}");
                    }
                    else if (inner != null)
                    {
                        using var doc = JsonDocument.Parse(inner);
                        if (doc.RootElement.TryGetProperty("stats", out var stats))
                            LogTraffic($"  WS_STATS: {stats}");
                    }
                }

                // Probe 2: Read React Context _currentValue for app state
                var contextJs = @"
(function() {
    try {
        var wpReq = window.__meetNowWebpackRequire;
        if (!wpReq || !wpReq.c) return JSON.stringify({ error: 'no webpack require' });

        var cache = wpReq.c;
        var mod = cache['93952'] || cache['101262'];
        if (!mod || !mod.exports) return JSON.stringify({ error: 'context module not found' });

        var exp = mod.exports;
        var result = {};

        // Read AppStateContext._currentValue
        if (exp.AppStateContext && exp.AppStateContext._currentValue) {
            var appState = exp.AppStateContext._currentValue;
            var stateKeys = Object.keys(appState);
            result.appStateKeys = stateKeys;
            result.appStateType = typeof appState;

            // Drill into each key to find calendar/presence/chat data
            var drilled = {};
            for (var i = 0; i < stateKeys.length; i++) {
                var k = stateKeys[i];
                var v = appState[k];
                if (v === null || v === undefined) {
                    drilled[k] = null;
                } else if (typeof v === 'string' || typeof v === 'number' || typeof v === 'boolean') {
                    drilled[k] = v;
                } else if (typeof v === 'object') {
                    drilled[k] = { type: Array.isArray(v) ? 'array(' + v.length + ')' : 'object', keys: Object.keys(v).slice(0, 15) };
                } else if (typeof v === 'function') {
                    drilled[k] = { type: 'function', name: v.name || '(anon)' };
                }
            }
            result.appState = drilled;
        } else {
            result.appStateContext = exp.AppStateContext ? '_currentValue is ' + typeof (exp.AppStateContext._currentValue) : 'missing';
        }

        // Read ClientStateContext._currentValue
        if (exp.ClientStateContext && exp.ClientStateContext._currentValue) {
            var clientState = exp.ClientStateContext._currentValue;
            var cKeys = Object.keys(clientState);
            result.clientStateKeys = cKeys;
            var cDrilled = {};
            for (var i = 0; i < cKeys.length; i++) {
                var k = cKeys[i];
                var v = clientState[k];
                if (v === null || v === undefined) {
                    cDrilled[k] = null;
                } else if (typeof v === 'string' || typeof v === 'number' || typeof v === 'boolean') {
                    cDrilled[k] = String(v).substring(0, 200);
                } else if (typeof v === 'object') {
                    cDrilled[k] = { type: Array.isArray(v) ? 'array(' + v.length + ')' : 'object', keys: Object.keys(v).slice(0, 15) };
                } else if (typeof v === 'function') {
                    cDrilled[k] = { type: 'function', name: v.name || '(anon)' };
                }
            }
            result.clientState = cDrilled;
        }

        return JSON.stringify(result);
    } catch(e) {
        return JSON.stringify({ error: e.message, stack: e.stack ? e.stack.substring(0, 200) : '' });
    }
})();";

                try
                {
                    var ctxResult = await _webView.ExecuteScriptAsync(contextJs);
                    LogTraffic($"  REACT_CTX_RAW: {Truncate(ctxResult ?? "null", 4000)}");
                    if (ctxResult != null && ctxResult != "null" && ctxResult != "\"null\"")
                    {
                        var inner = JsonSerializer.Deserialize<string>(ctxResult);
                        if (inner != null)
                            LogTraffic($"  REACT_CTX: {Truncate(inner, 4000)}");
                    }
                }
                catch (Exception ctxEx)
                {
                    LogTraffic($"  REACT_CTX_ERROR: {ctxEx.Message}");
                }

                // Probe 4: Deep webpack scan — dump ALL 78 interesting modules
                var deepScanJs = @"
(function() {
    try {
        var wpReq = window.__meetNowWebpackRequire;
        if (!wpReq || !wpReq.c) return JSON.stringify({ error: 'no webpack' });

        var cache = wpReq.c;
        var ids = Object.keys(cache);
        var found = [];

        for (var i = 0; i < ids.length; i++) {
            try {
                var mod = cache[ids[i]];
                if (!mod || !mod.exports) continue;
                var exp = mod.exports;
                var keys = Object.keys(exp);
                var hasInteresting = false;

                for (var j = 0; j < keys.length; j++) {
                    var kl = keys[j].toLowerCase();
                    if (kl.indexOf('calendar') >= 0 || kl.indexOf('presence') >= 0 ||
                        kl.indexOf('chat') >= 0 || kl.indexOf('message') >= 0 ||
                        kl.indexOf('meeting') >= 0 || kl.indexOf('status') >= 0 ||
                        kl.indexOf('availability') >= 0 || kl.indexOf('notification') >= 0 ||
                        kl.indexOf('conversation') >= 0 || kl.indexOf('contact') >= 0) {
                        hasInteresting = true;
                        break;
                    }
                }
                if (!hasInteresting) continue;

                // Dump this module's full export structure
                var modInfo = { id: ids[i], exports: {} };
                for (var j = 0; j < keys.length; j++) {
                    var k = keys[j];
                    var v = exp[k];
                    if (v === null || v === undefined) {
                        modInfo.exports[k] = null;
                    } else if (typeof v === 'string' || typeof v === 'number') {
                        modInfo.exports[k] = String(v).substring(0, 100);
                    } else if (typeof v === 'function') {
                        modInfo.exports[k] = 'fn(' + v.length + ')' + (v.name ? ':' + v.name : '');
                    } else if (typeof v === 'object') {
                        var subKeys = Object.keys(v).slice(0, 20);
                        var methods = subKeys.filter(function(sk) { return typeof v[sk] === 'function'; });
                        modInfo.exports[k] = { keys: subKeys, methods: methods, isArray: Array.isArray(v), len: Array.isArray(v) ? v.length : subKeys.length };
                    }
                }
                found.push(modInfo);
                if (found.length >= 40) break;
            } catch(e) {}
        }
        return JSON.stringify({ total: found.length, modules: found });
    } catch(e) {
        return JSON.stringify({ error: e.message });
    }
})();";

                try
                {
                    var deepResult = await _webView.ExecuteScriptAsync(deepScanJs);
                    LogTraffic($"  WEBPACK_DEEP_RAW_LEN: {deepResult?.Length ?? 0}");
                    if (deepResult != null && deepResult != "null")
                    {
                        var inner = JsonSerializer.Deserialize<string>(deepResult);
                        if (inner != null)
                            LogTraffic($"  WEBPACK_DEEP: {Truncate(inner, 6000)}");
                    }
                }
                catch (Exception wpEx)
                {
                    LogTraffic($"  WEBPACK_DEEP_ERROR: {wpEx.Message}");
                }

                // Probe 4: Title
                var titleJs = @"(function() { return JSON.stringify({ title: document.title }); })();";
                var titleResult = await _webView.ExecuteScriptAsync(titleJs);
                if (titleResult != null && titleResult != "null")
                {
                    var inner = JsonSerializer.Deserialize<string>(titleResult);
                    if (inner != null) LogTraffic($"  TITLE: {inner}");
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "JS probe failed (page may still be loading)");
            }
        }

        /// <summary>
        /// Execute arbitrary JS in the Teams page context. For POC exploration.
        /// </summary>
        public async Task<string?> EvaluateJsAsync(string script)
        {
            if (_webView == null) return null;

            try
            {
                var result = await _webView.ExecuteScriptAsync(script);
                if (result == "null") return null;
                return JsonSerializer.Deserialize<string>(result);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "JS evaluation failed");
                return null;
            }
        }
    }
}
