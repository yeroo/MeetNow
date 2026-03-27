using Serilog;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace MeetNow.Tasks
{
    /// <summary>
    /// Discovers actual keyboard shortcuts used by Teams web by scanning the DOM after
    /// Teams loads. Teams web uses different shortcuts than the desktop app
    /// (e.g. Ctrl+Alt+E for search instead of Ctrl+E).
    /// </summary>
    public static class TeamsShortcutDiscovery
    {
        private static readonly Dictionary<string, string> _shortcuts =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Returns the discovered shortcuts, keyed by logical name (e.g. "Search", "Chat").
        /// Falls back to built-in defaults when discovery has not yet run.
        /// </summary>
        public static IReadOnlyDictionary<string, string> Shortcuts => _shortcuts;

        /// <summary>
        /// Executes JavaScript in the provided WebView2 instance to scan nav buttons'
        /// aria-label/title attributes and the search box placeholder for shortcut patterns
        /// such as "Chat Ctrl+Alt+3". Parses the results and stores them in the dictionary.
        /// Applies defaults for any shortcut not found in the DOM.
        /// </summary>
        public static async Task DiscoverAsync(WebViewInstance instance)
        {
            Log.Information("TeamsShortcutDiscovery: scanning DOM for keyboard shortcuts");

            const string discoverJs = @"
(function() {
    try {
        var result = {};

        // Scan nav buttons: look for aria-label or title containing a Ctrl+ shortcut
        var navButtons = document.querySelectorAll(
            'button[aria-label], [role=""tab""][aria-label], [role=""menuitem""][aria-label]');
        var shortcutPattern = /(Ctrl\+[A-Za-z0-9+]+)/;

        for (var i = 0; i < navButtons.length; i++) {
            var el = navButtons[i];
            var label = el.getAttribute('aria-label') || el.getAttribute('title') || '';
            var match = label.match(shortcutPattern);
            if (match) {
                // Derive a logical name: take the leading word(s) before the shortcut
                // e.g. ""Chat Ctrl+Alt+3"" -> name=""Chat"", shortcut=""Ctrl+Alt+3""
                var shortcut = match[1];
                var nameRaw = label.replace(shortcut, '').replace(/[()[\]]/g, '').trim();
                // Collapse whitespace and take first word as the canonical name
                var name = nameRaw.split(/\s+/)[0];
                if (name && shortcut) {
                    result[name] = shortcut;
                }
            }
        }

        // Also check title attributes on elements that may not have aria-label
        var titledEls = document.querySelectorAll('[title]');
        for (var j = 0; j < titledEls.length; j++) {
            var title = titledEls[j].getAttribute('title') || '';
            var tm = title.match(shortcutPattern);
            if (tm) {
                var tShortcut = tm[1];
                var tNameRaw = title.replace(tShortcut, '').replace(/[()[\]]/g, '').trim();
                var tName = tNameRaw.split(/\s+/)[0];
                if (tName && tShortcut && !result[tName]) {
                    result[tName] = tShortcut;
                }
            }
        }

        // Search box: read placeholder or aria-label for the search shortcut
        var searchInput = document.querySelector('input[aria-label*=""Search""]')
            || document.querySelector('input[placeholder*=""Search""]')
            || document.querySelector('input[id=""searchInput""]');

        if (searchInput) {
            var searchLabel = searchInput.getAttribute('aria-label')
                || searchInput.getAttribute('placeholder') || '';
            var sm = searchLabel.match(shortcutPattern);
            if (sm) {
                result['Search'] = sm[1];
            }
        }

        return JSON.stringify(result);
    } catch (e) {
        return JSON.stringify({ error: e.message });
    }
})();";

            try
            {
                var json = await instance.EvaluateJsAsync(discoverJs);
                if (!string.IsNullOrEmpty(json))
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("error", out var errProp))
                    {
                        Log.Warning("TeamsShortcutDiscovery: JS error: {Error}", errProp.GetString());
                    }
                    else
                    {
                        foreach (var prop in root.EnumerateObject())
                        {
                            var name = prop.Name;
                            var shortcut = prop.Value.GetString();
                            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(shortcut))
                            {
                                _shortcuts[name] = shortcut!;
                                Log.Debug("TeamsShortcutDiscovery: {Name} = {Shortcut}", name, shortcut);
                            }
                        }

                        Log.Information("TeamsShortcutDiscovery: discovered {Count} shortcuts", _shortcuts.Count);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "TeamsShortcutDiscovery: failed to evaluate discovery script");
            }

            // Apply defaults for any shortcut not found via DOM scanning
            ApplyDefaults();
        }

        /// <summary>
        /// Returns the shortcut for the given logical name, or <paramref name="fallback"/>
        /// if the name is not in the discovered dictionary.
        /// </summary>
        public static string GetShortcut(string name, string fallback)
        {
            if (_shortcuts.TryGetValue(name, out var shortcut))
                return shortcut;

            Log.Debug("TeamsShortcutDiscovery: no shortcut found for '{Name}', using fallback '{Fallback}'",
                name, fallback);
            return fallback;
        }

        // ---------------------------------------------------------------------------
        // Internals
        // ---------------------------------------------------------------------------

        private static readonly IReadOnlyDictionary<string, string> DefaultShortcuts =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Search"]   = "Ctrl+Alt+E",
                ["Chat"]     = "Ctrl+Alt+3",
                ["Activity"] = "Ctrl+Alt+1",
                ["Calendar"] = "Ctrl+Alt+4",
            };

        private static void ApplyDefaults()
        {
            foreach (var kvp in DefaultShortcuts)
            {
                if (!_shortcuts.ContainsKey(kvp.Key))
                {
                    _shortcuts[kvp.Key] = kvp.Value;
                    Log.Debug("TeamsShortcutDiscovery: applied default {Name} = {Shortcut}",
                        kvp.Key, kvp.Value);
                }
            }
        }
    }
}
