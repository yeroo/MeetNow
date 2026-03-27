using MeetNow.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace MeetNow.Tasks
{
    /// <summary>
    /// Transient task — passive profile card enrichment and UI-automated search.
    /// All data extraction is passive — MeetNow only reads responses from requests
    /// Teams itself initiates. NO fetch() calls with auth tokens.
    /// </summary>
    public static class PeopleEnricherTask
    {
        /// <summary>
        /// Scheduled run: navigate to Teams, iterate contacts with Pending status,
        /// click their chat in the sidebar to trigger profile picture/card loads.
        /// Passive interception in WebViewInstance enriches them automatically.
        /// </summary>
        public static async Task RunAsync(WebViewInstance instance)
        {
            Log.Information("PeopleEnricherTask: starting scheduled enrichment run");

            var pending = ContactDatabase.GetPendingEnrichment(10);
            if (pending.Count == 0)
            {
                Log.Information("PeopleEnricherTask: no pending contacts to enrich");
                return;
            }

            foreach (var contact in pending)
            {
                try
                {
                    // Use JS to search for the contact in Teams sidebar by clicking their name
                    // This triggers Teams to load their profile picture and contact card data
                    // which our passive network interception captures
                    var searchJs = $@"
(function() {{
    try {{
        // Try to find contact in the recent chat list
        var items = document.querySelectorAll('[data-tid=""chat-list-item""]');
        for (var i = 0; i < items.length; i++) {{
            var text = items[i].textContent || '';
            if (text.indexOf('{EscapeJs(contact.DisplayName)}') >= 0) {{
                items[i].click();
                return 'clicked';
            }}
        }}
        return 'not_found';
    }} catch(e) {{ return 'error:' + e.message; }}
}})();";

                    var result = await instance.EvaluateJsAsync(searchJs);
                    Log.Debug("PeopleEnricherTask: enrichment for {Name}: {Result}",
                        contact.DisplayName, result);

                    // Wait for Teams to load profile data
                    if (result == "clicked")
                        await Task.Delay(3000);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "PeopleEnricherTask: failed to enrich {Name}", contact.DisplayName);
                }
            }

            Log.Information("PeopleEnricherTask: enrichment run complete ({Count} contacts processed)",
                pending.Count);
        }

        /// <summary>
        /// On-demand search for FindPerson — uses UI automation to search Teams directory.
        /// Types query into Teams search box via DOM manipulation, reads DOM results.
        /// NO fetch() calls.
        /// </summary>
        public static async Task<List<Contact>> SearchAsync(WebViewInstance instance, string query)
        {
            Log.Information("PeopleEnricherTask: searching for '{Query}'", query);

            var safeQuery = EscapeJs(query);

            // Step 1: Open Teams search and type the query
            var searchJs = $@"
(async function() {{
    try {{
        // Focus the search box — Teams uses Ctrl+E or the search input
        var searchInput = document.querySelector('input[id=""searchInput""]')
            || document.querySelector('input[aria-label*=""Search""]')
            || document.querySelector('input[placeholder*=""Search""]');

        if (!searchInput) {{
            // Try clicking the search button first
            var searchBtn = document.querySelector('button[aria-label*=""Search""]')
                || document.querySelector('[data-tid=""search-button""]');
            if (searchBtn) {{
                searchBtn.click();
                await new Promise(r => setTimeout(r, 500));
                searchInput = document.querySelector('input[id=""searchInput""]')
                    || document.querySelector('input[aria-label*=""Search""]')
                    || document.querySelector('input[placeholder*=""Search""]');
            }}
        }}

        if (!searchInput) return JSON.stringify({{ error: 'search input not found' }});

        // Clear and type query
        searchInput.focus();
        searchInput.value = '{safeQuery}';
        searchInput.dispatchEvent(new Event('input', {{ bubbles: true }}));
        searchInput.dispatchEvent(new Event('change', {{ bubbles: true }}));

        // Wait for results
        await new Promise(r => setTimeout(r, 3000));

        // Try to click 'People' tab if available
        var tabs = document.querySelectorAll('[role=""tab""]');
        for (var i = 0; i < tabs.length; i++) {{
            if ((tabs[i].textContent || '').indexOf('People') >= 0) {{
                tabs[i].click();
                await new Promise(r => setTimeout(r, 2000));
                break;
            }}
        }}

        // Read results from DOM
        var results = [];
        var resultElements = document.querySelectorAll('[data-tid=""search-result""]')
            || document.querySelectorAll('[class*=""searchResult""]');

        if (resultElements.length === 0) {{
            // Try alternative selectors
            resultElements = document.querySelectorAll('[role=""listitem""]');
        }}

        for (var i = 0; i < resultElements.length && i < 20; i++) {{
            var el = resultElements[i];
            var nameEl = el.querySelector('[class*=""name""]') || el.querySelector('[class*=""title""]');
            var name = nameEl ? nameEl.textContent.trim() : el.textContent.trim().split('\\n')[0];
            if (name && name.length > 1 && name.length < 100) {{
                results.push({{ displayName: name }});
            }}
        }}

        return JSON.stringify({{ count: results.length, results: results }});
    }} catch(e) {{ return JSON.stringify({{ error: e.message }}); }}
}})();";

            try
            {
                var resultJson = await instance.EvaluateJsAsync(searchJs);
                if (resultJson == null) return new List<Contact>();

                using var doc = JsonDocument.Parse(resultJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var err))
                {
                    Log.Warning("PeopleEnricherTask: search error: {Error}", err.GetString());
                    return new List<Contact>();
                }

                var contacts = new List<Contact>();
                if (root.TryGetProperty("results", out var results))
                {
                    foreach (var item in results.EnumerateArray())
                    {
                        var name = item.TryGetProperty("displayName", out var n) ? n.GetString() : null;
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        contacts.Add(new Contact
                        {
                            TeamsUserId = $"search:{Guid.NewGuid():N}",
                            DisplayName = name,
                            Source = ContactSource.Search,
                            LastSeenTimestamp = DateTime.Now,
                            EnrichmentStatus = EnrichmentStatus.Pending
                        });
                    }
                }

                // Also check local DB for matches (passive interception may have caught results)
                var localMatches = ContactDatabase.GetByName(query);
                foreach (var local in localMatches)
                {
                    if (!contacts.Exists(c =>
                        c.DisplayName.Equals(local.DisplayName, StringComparison.OrdinalIgnoreCase)))
                    {
                        contacts.Add(local);
                    }
                }

                Log.Information("PeopleEnricherTask: search for '{Query}' returned {Count} results",
                    query, contacts.Count);
                return contacts;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "PeopleEnricherTask: search failed for '{Query}'", query);
                return new List<Contact>();
            }
        }

        private static string EscapeJs(string s)
        {
            return s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\"", "\\\"")
                .Replace("\n", "\\n").Replace("\r", "\\r");
        }
    }
}
