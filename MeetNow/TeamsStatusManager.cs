using Serilog;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace MeetNow
{
    /// <summary>
    /// Changes Teams presence/status and automates chat via WebView DOM automation.
    /// Replaces the previous Win32 P/Invoke approach (FindWindow, SendKeys, mouse_event)
    /// with offscreen WebView2 DOM manipulation — no focus stealing, no fragile coordinates.
    /// </summary>
    public static class TeamsStatusManager
    {
        public enum TeamsStatus
        {
            Available,
            Busy,
            Away,
            DoNotDisturb,
            BeRightBack
        }

        /// <summary>
        /// Run a JS script on the WebView via the UI thread dispatcher.
        /// WebView2 WPF controls must be accessed from the thread that created them.
        /// </summary>
        private static async Task<string?> EvalOnUiThread(WebViewInstance instance, string script)
        {
            return await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => instance.EvaluateJsAsync(script)).Task.Unwrap();
        }

        private static async Task NavigateOnUiThread(WebViewInstance instance, string url)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => instance.NavigateAndWaitAsync(url)).Task.Unwrap();
        }

        /// <summary>
        /// Set Teams status using slash commands typed into the search bar via DOM automation.
        /// </summary>
        public static async Task<bool> SetStatusAsync(TeamsStatus status)
        {
            var command = status switch
            {
                TeamsStatus.Available => "/available",
                TeamsStatus.Busy => "/busy",
                TeamsStatus.Away => "/away",
                TeamsStatus.DoNotDisturb => "/dnd",
                TeamsStatus.BeRightBack => "/brb",
                _ => "/available"
            };

            Log.Information("SetStatus: setting Teams status to {Status} (command: {Command})", status, command);

            var instance = WebViewManager.Instance.TeamsAutomationInstance;
            if (instance == null || !instance.IsReady)
            {
                Log.Warning("SetStatus: TeamsAutomation WebView not available");
                return false;
            }

            try
            {
                // Step 0: Navigate to Teams home to ensure clean state
                TeamsOperationQueue.CurrentStep = "Navigating to Teams";
                await NavigateOnUiThread(instance, "https://teams.microsoft.com");
                await Task.Delay(2000);

                // Dismiss any dialogs with Escape
                await EvalOnUiThread(instance, @"(function() {
                    for (var i = 0; i < 3; i++) {
                        document.dispatchEvent(new KeyboardEvent('keydown', {key: 'Escape', keyCode: 27, bubbles: true}));
                    }
                    return 'ok';
                })();");
                await Task.Delay(500);

                // Step 1: Find the search input
                TeamsOperationQueue.CurrentStep = "Finding search input";
                var searchFound = await EvalOnUiThread(instance,@"(function() {
                    var el = document.querySelector('#ms-searchux-input')
                         || document.querySelector('input[id=""ms-searchux-input""]')
                         || document.querySelector('input[type=""search""]')
                         || document.querySelector('input[aria-label*=""Search""]')
                         || document.querySelector('input[placeholder*=""Search""]');
                    if (el) { el.focus(); el.click(); return 'found'; }
                    return 'not_found';
                })();");

                // If search input not found, try clicking a search button first
                if (searchFound != "found")
                {
                    TeamsOperationQueue.CurrentStep = "Clicking search button";
                    Log.Information("SetStatus: search input not found directly, trying search button");

                    // Click "Expand search box" button
                    await EvalOnUiThread(instance, @"(function() {
                        var btn = document.querySelector('button[aria-label*=""search"" i]')
                               || document.querySelector('button[aria-label*=""Search""]');
                        if (btn) btn.click();
                    })();");

                    await Task.Delay(1000);

                    searchFound = await EvalOnUiThread(instance, @"(function() {
                        var el = document.querySelector('#ms-searchux-input')
                             || document.querySelector('input[type=""search""]')
                             || document.querySelector('input[aria-label*=""Search""]');
                        if (el) { el.focus(); el.click(); return 'found'; }
                        return 'not_found';
                    })();");

                    if (searchFound != "found")
                    {
                        // Dump DOM diagnostics to understand Teams v2 structure
                        var diag = await EvalOnUiThread(instance, @"(function() {
                            var inputs = document.querySelectorAll('input');
                            var buttons = document.querySelectorAll('button');
                            var inputInfo = [];
                            for (var i = 0; i < inputs.length && i < 10; i++) {
                                inputInfo.push({
                                    tag: 'input',
                                    type: inputs[i].type,
                                    id: inputs[i].id,
                                    ariaLabel: (inputs[i].getAttribute('aria-label') || '').substring(0, 80),
                                    placeholder: (inputs[i].placeholder || '').substring(0, 80),
                                    className: (inputs[i].className || '').substring(0, 60)
                                });
                            }
                            var btnInfo = [];
                            for (var j = 0; j < buttons.length && j < 20; j++) {
                                var label = buttons[j].getAttribute('aria-label') || '';
                                var text = (buttons[j].textContent || '').trim();
                                if (label.length > 0 || text.length > 0) {
                                    btnInfo.push({
                                        ariaLabel: label.substring(0, 80),
                                        text: text.substring(0, 40),
                                        id: buttons[j].id
                                    });
                                }
                            }
                            return JSON.stringify({ url: location.href, title: document.title, inputs: inputInfo, buttons: btnInfo });
                        })();");
                        Log.Warning("SetStatus: DOM diagnostic: {Diag}", diag);
                        TeamsOperationQueue.CurrentStep = "Failed — search input not found";
                        return false;
                    }
                }

                // Step 2: Clear search and type command via CDP (real browser keystrokes)
                TeamsOperationQueue.CurrentStep = $"Typing {command}";

                // Focus the search input
                await EvalOnUiThread(instance, @"(function() {
                    var el = document.querySelector('#ms-searchux-input')
                         || document.querySelector('input[type=""search""]');
                    if (el) { el.focus(); el.select(); }
                })();");
                await Task.Delay(300);

                // Type each character via CDP Input.dispatchKeyEvent (real browser-level input)
                foreach (var ch in command)
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                        () => instance.TypeCharAsync(ch)).Task.Unwrap();
                    await Task.Delay(80);
                }

                // Step 3: Wait for autocomplete
                TeamsOperationQueue.CurrentStep = "Waiting for autocomplete";
                await Task.Delay(2000);

                // Step 4: Press Enter via CDP (real browser-level Enter key)
                TeamsOperationQueue.CurrentStep = "Enter (execute)";
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                    () => instance.SendEnterAsync()).Task.Unwrap();
                Log.Information("SetStatus: Enter sent via CDP");

                await Task.Delay(1000);

                // Step 5: Dismiss with Escape
                TeamsOperationQueue.CurrentStep = "Esc (cleanup)";
                await EvalOnUiThread(instance,@"(function() {
                    var el = document.activeElement || document.body;
                    el.dispatchEvent(new KeyboardEvent('keydown', {
                        key: 'Escape', code: 'Escape', keyCode: 27, which: 27,
                        bubbles: true, cancelable: true
                    }));
                })();");

                await Task.Delay(300);

                TeamsOperationQueue.CurrentStep = "Done";
                Log.Information("SetStatus: Teams status command sent: {Command}", command);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "SetStatus: error setting Teams status");
                TeamsOperationQueue.CurrentStep = "Failed";
                return false;
            }
        }

        /// <summary>
        /// Open a 1:1 chat by searching for the person in the search box, then clicking their result.
        /// Uses email if available, falls back to name. Returns true if compose box is focused.
        /// </summary>
        private static async Task<bool> OpenChatViaSearch(WebViewInstance instance, string senderName, string logPrefix)
        {
            string searchQuery;

            // If input is already an email, use it directly
            if (senderName.Contains('@'))
            {
                searchQuery = senderName;
            }
            else
            {
                var searchName = ExtractSearchName(senderName);
                // Try to get email for more precise search
                var contacts = ContactDatabase.GetByName(searchName);
                searchQuery = contacts.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Email))?.Email ?? searchName;
            }

            Log.Information("{Prefix}: searching for '{Query}'", logPrefix, searchQuery);

            // Navigate to Teams home first
            TeamsOperationQueue.CurrentStep = "Navigating to Teams";
            await NavigateOnUiThread(instance, "https://teams.microsoft.com");
            await Task.Delay(2000);

            // Open new chat via Alt+Shift+N shortcut (CDP)
            TeamsOperationQueue.CurrentStep = "Opening new chat (Alt+Shift+N)";
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => instance.SendShortcutAsync("n", alt: true, shift: true)).Task.Unwrap();
            Log.Information("{Prefix}: sent Alt+Shift+N", logPrefix);
            await Task.Delay(2000);

            // Type the recipient in the To field via CDP
            TeamsOperationQueue.CurrentStep = $"Typing recipient: {searchQuery}";
            foreach (var ch in searchQuery)
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                    () => instance.TypeCharAsync(ch)).Task.Unwrap();
                await Task.Delay(80);
            }

            // Wait for suggestions to appear
            await Task.Delay(2000);

            // Press Enter to select first suggestion
            TeamsOperationQueue.CurrentStep = "Selecting recipient";
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => instance.SendEnterAsync()).Task.Unwrap();
            await Task.Delay(1500);

            // Dump search results DOM for debugging
            var resultsDump = await EvalOnUiThread(instance, @"(function() {
                var items = [];
                // Broad search: anything that looks like a clickable result
                var candidates = document.querySelectorAll('[role=""listitem""], [role=""option""], [data-tid*=""result""], [data-tid*=""person""], [class*=""result""], [class*=""Result""], [class*=""person""], [class*=""Person""]');
                for (var i = 0; i < candidates.length && i < 10; i++) {
                    items.push({
                        tag: candidates[i].tagName,
                        role: candidates[i].getAttribute('role') || '',
                        ariaLabel: (candidates[i].getAttribute('aria-label') || '').substring(0, 80),
                        text: (candidates[i].textContent || '').trim().substring(0, 80),
                        dataTid: candidates[i].getAttribute('data-tid') || '',
                        className: (candidates[i].className || '').substring(0, 60)
                    });
                }
                return JSON.stringify({ count: candidates.length, items: items });
            })();");
            Log.Information("{Prefix}: search results DOM: {Dump}", logPrefix, resultsDump);

            // Click the first search result
            var resultClicked = await EvalOnUiThread(instance, @"(function() {
                // Strategy 1: data-tid with result/person
                var results = document.querySelectorAll('[data-tid*=""search-result""], [data-tid*=""person""]');
                if (results.length > 0) { results[0].click(); return 'clicked_tid:' + results[0].getAttribute('data-tid'); }

                // Strategy 2: role=listitem
                var items = document.querySelectorAll('[role=""listitem""]');
                if (items.length > 0) { items[0].click(); return 'clicked_listitem'; }

                // Strategy 3: role=option
                var options = document.querySelectorAll('[role=""option""]');
                if (options.length > 0) { options[0].click(); return 'clicked_option'; }

                // Strategy 4: any clickable with person-like class
                var persons = document.querySelectorAll('[class*=""person"" i], [class*=""contact"" i], [class*=""result"" i]');
                if (persons.length > 0) { persons[0].click(); return 'clicked_class'; }

                return 'no_results';
            })();");

            Log.Information("{Prefix}: result click = {Result}", logPrefix, resultClicked);

            if (resultClicked == "no_results")
            {
                Log.Warning("{Prefix}: no search results found for '{Query}'", logPrefix, searchQuery);
                return false;
            }

            // Wait for chat to open
            await Task.Delay(2000);

            // Find and focus compose box
            TeamsOperationQueue.CurrentStep = "Focusing compose box";
            var composeFound = await EvalOnUiThread(instance, @"(function() {
                var el = document.querySelector('[data-tid=""ckeditor-replyConversation""]')
                     || document.querySelector('[role=""textbox""][contenteditable=""true""]')
                     || document.querySelector('div[contenteditable=""true""]');
                if (el) { el.focus(); return 'found'; }
                return 'not_found';
            })();");

            if (composeFound != "found")
            {
                Log.Warning("{Prefix}: compose box not found after search", logPrefix);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Opens the 1:1 chat with the sender, types "Hi", waits (typing indicator shows),
        /// then clears the text. Does not send anything.
        /// </summary>
        public static async Task<bool> SimulateTypingAsync(string senderName)
        {
            Log.Information("SimulateTyping: start for '{Name}'", senderName);

            var instance = WebViewManager.Instance.TeamsAutomationInstance;
            if (instance == null || !instance.IsReady)
            {
                Log.Warning("SimulateTyping: TeamsAutomation WebView not available");
                return false;
            }

            try
            {
                if (!await OpenChatViaSearch(instance, senderName, "SimulateTyping"))
                {
                    TeamsOperationQueue.CurrentStep = "Failed — chat not opened";
                    await NavigateBackToTeams(instance);
                    return false;
                }

                // Type "Hi" via CDP — sender sees "is typing..."
                TeamsOperationQueue.CurrentStep = "Typing indicator active";
                foreach (var ch in "Hi")
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                        () => instance.TypeCharAsync(ch)).Task.Unwrap();
                    await Task.Delay(80);
                }

                // Hold for configured duration
                var typingDuration = MeetNowSettings.Instance.SimulateTypingDurationSeconds * 1000;
                Log.Information("SimulateTyping: holding for {Duration}s", typingDuration / 1000);
                await Task.Delay(typingDuration);

                // Select all + Delete to clear without sending
                TeamsOperationQueue.CurrentStep = "Clearing text";
                await EvalOnUiThread(instance, @"(function() {
                    var el = document.querySelector('[data-tid=""ckeditor-replyConversation""]')
                         || document.querySelector('[role=""textbox""][contenteditable=""true""]')
                         || document.querySelector('div[contenteditable=""true""]');
                    if (el) { el.textContent = ''; el.dispatchEvent(new Event('input', {bubbles: true})); }
                })();");

                await NavigateBackToTeams(instance);

                TeamsOperationQueue.CurrentStep = "Done";
                Log.Information("SimulateTyping: complete for '{Name}'", senderName);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "SimulateTyping: error for '{Name}'", senderName);
                TeamsOperationQueue.CurrentStep = "Failed";
                await NavigateBackToTeams(instance);
                return false;
            }
        }

        /// <summary>
        /// Opens the 1:1 chat and actually sends a message.
        /// </summary>
        public static async Task<bool> SendMessageAsync(string senderName, string message)
        {
            Log.Information("SendMessage: start for '{Name}', message: '{Message}'", senderName, message);

            var instance = WebViewManager.Instance.TeamsAutomationInstance;
            if (instance == null || !instance.IsReady)
            {
                Log.Warning("SendMessage: TeamsAutomation WebView not available");
                return false;
            }

            try
            {
                if (!await OpenChatViaSearch(instance, senderName, "SendMessage"))
                {
                    TeamsOperationQueue.CurrentStep = "Failed — chat not opened";
                    await NavigateBackToTeams(instance);
                    return false;
                }

                // Type the message via CDP
                TeamsOperationQueue.CurrentStep = "Typing message";
                foreach (var ch in message)
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                        () => instance.TypeCharAsync(ch)).Task.Unwrap();
                    await Task.Delay(30);
                }

                await Task.Delay(500);

                // Press Enter to send via CDP
                TeamsOperationQueue.CurrentStep = "Sending";
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                    () => instance.SendEnterAsync()).Task.Unwrap();

                await Task.Delay(1000);
                await NavigateBackToTeams(instance);

                TeamsOperationQueue.CurrentStep = "Done";
                Log.Information("SendMessage: sent '{Message}' to '{Name}'", message, senderName);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "SendMessage: error for '{Name}'", senderName);
                TeamsOperationQueue.CurrentStep = "Failed";
                await NavigateBackToTeams(instance);
                return false;
            }
        }

        /// <summary>
        /// Extracts a searchable name from sender format like "Doe, John TESTORG/IT"
        /// </summary>
        private static string ExtractSearchName(string sender)
        {
            // Remove org/dept suffix (after /)
            var slashIdx = sender.IndexOf('/');
            var name = slashIdx >= 0 ? sender[..slashIdx].Trim() : sender.Trim();

            // Handle "Last, First ORG" format
            var commaIdx = name.IndexOf(',');
            if (commaIdx >= 0)
            {
                var last = name[..commaIdx].Trim();
                var rest = name[(commaIdx + 1)..].Trim();
                // First word after comma is the first name, rest might be org
                var firstParts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (firstParts.Length > 0)
                    return $"{firstParts[0]} {last}";
            }

            return name;
        }

        /// <summary>
        /// Resolves a display name to a Teams user ID (e.g. "8:orgid:GUID") via ContactDatabase.
        /// </summary>
        private static string? ResolveTeamsUserId(string searchName)
        {
            var contacts = ContactDatabase.GetByName(searchName);
            var match = contacts.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.TeamsUserId));
            if (match != null)
            {
                Log.Information("ResolveTeamsUserId: '{Name}' -> {Id}", searchName, match.TeamsUserId);
                return match.TeamsUserId;
            }

            Log.Warning("ResolveTeamsUserId: no contact found for '{Name}'", searchName);
            return null;
        }

        /// <summary>
        /// Navigates the automation instance back to the Teams home page.
        /// </summary>
        private static async Task NavigateBackToTeams(WebViewInstance instance)
        {
            try
            {
                await NavigateOnUiThread(instance,"https://teams.microsoft.com");
                await Task.Delay(1000);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "NavigateBackToTeams: failed to navigate back");
            }
        }
    }
}
