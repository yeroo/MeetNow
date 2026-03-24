using Microsoft.Web.WebView2.Core;
using MeetNow.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace MeetNow
{
    public enum InstanceType { Persistent, Transient }

    public class WebViewInstance : IDisposable
    {
        private static readonly string UserDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MeetNow", "WebView2Profile");

        private Window? _hostWindow;
        private Microsoft.Web.WebView2.Wpf.WebView2? _webView;
        private string? _capturedBearerToken;

        public string Name { get; }
        public InstanceType InstanceType { get; }
        public CoreWebView2? CoreWebView2 => _webView?.CoreWebView2;
        public bool IsReady { get; private set; }
        public string? CurrentUrl => _webView?.CoreWebView2?.Source;
        public string? CapturedBearerToken => _capturedBearerToken;
        public Window? HostWindow => _hostWindow;

        public event Action<string, string?, IDictionary<string, string>>? ResponseReceived;
        public event Action<string>? ContactDiscovered;

        public WebViewInstance(string name, InstanceType type)
        {
            Name = name;
            InstanceType = type;
        }

        public async Task InitializeAsync(CoreWebView2Environment environment)
        {
            // Create offscreen host window
            _hostWindow = new Window
            {
                Title = $"MeetNow WebView [{Name}]",
                Width = 1200, Height = 800,
                Left = -10000, Top = -10000,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow
            };

            _webView = new Microsoft.Web.WebView2.Wpf.WebView2();
            _hostWindow.Content = _webView;
            _hostWindow.Show();

            await _webView.EnsureCoreWebView2Async(environment);

            // Spoof Edge user-agent
            var edgeVersion = environment.BrowserVersionString;
            _webView.CoreWebView2.Settings.UserAgent =
                $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{edgeVersion} Safari/537.36 Edg/{edgeVersion}";

            // Subscribe to network events
            _webView.CoreWebView2.WebResourceResponseReceived += OnResponseReceived;
            _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

            Log.Information("WebViewInstance [{Name}] initialized", Name);
        }

        public async Task NavigateAndWaitAsync(string url, int timeoutMs = 15000)
        {
            if (_webView?.CoreWebView2 == null) return;

            IsReady = false;
            var tcs = new TaskCompletionSource<bool>();
            void handler(object? s, CoreWebView2NavigationCompletedEventArgs e)
            {
                _webView.CoreWebView2.NavigationCompleted -= handler;
                IsReady = e.IsSuccess;
                tcs.TrySetResult(e.IsSuccess);
            }
            _webView.CoreWebView2.NavigationCompleted += handler;
            _webView.CoreWebView2.Navigate(url);

            var timeout = Task.Delay(timeoutMs);
            await Task.WhenAny(tcs.Task, timeout);
            if (!tcs.Task.IsCompleted)
            {
                _webView.CoreWebView2.NavigationCompleted -= handler;
                Log.Warning("WebViewInstance [{Name}] navigation timeout: {Url}", Name, url);
            }
        }

        public async Task<string?> EvaluateJsAsync(string script)
        {
            if (_webView?.CoreWebView2 == null) return null;
            try
            {
                var result = await _webView.CoreWebView2.ExecuteScriptAsync(script);
                if (result == "null") return null;
                return JsonSerializer.Deserialize<string>(result);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "WebViewInstance [{Name}] JS eval failed", Name);
                return null;
            }
        }

        public async Task<bool> HeartbeatAsync()
        {
            try
            {
                var result = await EvaluateJsAsync("(function() { return 'alive'; })();");
                return result == "alive";
            }
            catch { return false; }
        }

        private async void OnResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            try
            {
                var uri = e.Request.Uri;

                // Capture bearer token passively (diagnostic only)
                if (uri.Contains("/api/mt/", StringComparison.OrdinalIgnoreCase)
                    || uri.Contains("/api/chatsvc/", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var authHeader = e.Request.Headers.GetHeader("Authorization");
                        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                            _capturedBearerToken = authHeader["Bearer ".Length..];
                    }
                    catch { }
                }

                // Contact auto-discovery from profile picture URLs
                TryExtractContact(uri);

                // Read body for interesting responses
                var status = e.Response.StatusCode;
                var contentType = e.Response.Headers.GetHeader("Content-Type") ?? "";
                if (status >= 200 && status < 300 && IsInterestingResponse(uri, contentType))
                {
                    string? body = null;
                    try
                    {
                        var stream = await e.Response.GetContentAsync();
                        if (stream != null)
                        {
                            using var reader = new StreamReader(stream);
                            body = await reader.ReadToEndAsync();
                        }
                    }
                    catch { }

                    // Passive enrichment from GetPersona
                    if (body != null)
                        TryEnrichFromGetPersona(uri, body);

                    // Notify subscribers
                    var headers = new Dictionary<string, string>();
                    ResponseReceived?.Invoke(uri, body, headers);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "WebViewInstance [{Name}] response processing error", Name);
            }
        }

        private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            IsReady = e.IsSuccess;
            Log.Information("WebViewInstance [{Name}] navigated: {Url} (success={Success})",
                Name, CurrentUrl, e.IsSuccess);
        }

        // Copied verbatim from TeamsWebViewDataExtractor.cs
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
                        ContactDatabase.Upsert(new Contact
                        {
                            TeamsUserId = teamsUserId,
                            DisplayName = displayName,
                            LastSeenTimestamp = DateTime.Now,
                            Source = ContactSource.Chat
                        });
                        ContactDiscovered?.Invoke(teamsUserId);
                    }
                }

                // Pattern 2: Loki Delve person API — URL contains teamsMri and smtp (email)
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
                            ContactDatabase.Upsert(new Contact
                            {
                                TeamsUserId = teamsMri,
                                DisplayName = existing?.DisplayName ?? "",
                                Email = email,
                                LastSeenTimestamp = DateTime.Now,
                                Source = existing?.Source ?? ContactSource.Chat,
                                EnrichmentStatus = EnrichmentStatus.Enriched
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

                    using var doc = JsonDocument.Parse(usersJson);
                    foreach (var user in doc.RootElement.EnumerateArray())
                    {
                        var userId = user.GetProperty("userId").GetString();
                        var name = user.GetProperty("displayName").GetString();
                        if (!string.IsNullOrWhiteSpace(userId) && !string.IsNullOrWhiteSpace(name))
                        {
                            ContactDatabase.Upsert(new Contact
                            {
                                TeamsUserId = userId,
                                DisplayName = name,
                                LastSeenTimestamp = DateTime.Now,
                                Source = ContactSource.Chat
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

        // Copied verbatim from TeamsWebViewDataExtractor.cs
        private void TryEnrichFromGetPersona(string uri, string body)
        {
            if (!uri.Contains("action=GetPersona", StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                using var doc = JsonDocument.Parse(body);
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

                var contact = existing ?? new Contact();
                contact.TeamsUserId = existing?.TeamsUserId ?? teamsUserId ?? $"ad:{adObjectId}";
                contact.DisplayName = displayName;
                contact.Email = email;
                contact.JobTitle = title ?? contact.JobTitle;
                contact.Department = department ?? contact.Department;
                contact.Phone = phone ?? contact.Phone;
                contact.LastSeenTimestamp = DateTime.Now;
                contact.EnrichmentStatus = EnrichmentStatus.Enriched;

                ContactDatabase.Upsert(contact);
                Log.Information("Contact enriched from GetPersona: {Name} <{Email}> [{Title}]",
                    displayName, email, title ?? "");
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to parse GetPersona response");
            }
        }

        private static bool IsInterestingResponse(string uri, string contentType)
        {
            if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                && !(contentType.Contains("x-javascript", StringComparison.OrdinalIgnoreCase)
                     && (uri.Contains("outlook.office.com", StringComparison.OrdinalIgnoreCase)
                         || uri.Contains("outlook.cloud.microsoft", StringComparison.OrdinalIgnoreCase))))
                return false;

            string[] patterns = { "/api/calendar/", "/me/calendarview", "/api/chatsvc/",
                "/messages", "/threads", "/presence/", "/status", "/api/mt/", "/api/csa/",
                "outlook.cloud.microsoft/", "startupdata.ashx", "service.svc",
                "loki.delve.office.com/api/" };
            foreach (var p in patterns)
                if (uri.Contains(p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public void Dispose()
        {
            if (_webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.WebResourceResponseReceived -= OnResponseReceived;
                _webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            }
            _webView?.Dispose();
            _hostWindow?.Close();
            Log.Information("WebViewInstance [{Name}] disposed", Name);
        }
    }
}
