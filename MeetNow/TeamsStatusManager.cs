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

                // Step 2: Type the slash command char by char to trigger React handlers
                TeamsOperationQueue.CurrentStep = $"Typing {command}";
                var commandEscaped = command.Replace("'", "\\'").Replace("\\", "\\\\");
                await EvalOnUiThread(instance, $@"(function() {{
                    var el = document.querySelector('#ms-searchux-input')
                         || document.querySelector('input[type=""search""]')
                         || document.querySelector('input[aria-label*=""Search""]');
                    if (!el) return;
                    el.focus();

                    // Type each character using keydown + input event simulation
                    var text = '{commandEscaped}';
                    for (var i = 0; i < text.length; i++) {{
                        var ch = text[i];
                        el.dispatchEvent(new KeyboardEvent('keydown', {{key: ch, code: 'Key' + ch.toUpperCase(), bubbles: true}}));
                        // Update value incrementally
                        var nativeSetter = Object.getOwnPropertyDescriptor(
                            window.HTMLInputElement.prototype, 'value').set;
                        nativeSetter.call(el, text.substring(0, i + 1));
                        el.dispatchEvent(new InputEvent('input', {{data: ch, inputType: 'insertText', bubbles: true}}));
                        el.dispatchEvent(new KeyboardEvent('keyup', {{key: ch, code: 'Key' + ch.toUpperCase(), bubbles: true}}));
                    }}
                }})();");

                // Step 3: Wait for autocomplete dropdown to appear
                TeamsOperationQueue.CurrentStep = "Waiting for autocomplete";
                await Task.Delay(2500);

                // Step 4: Find and click the autocomplete suggestion
                TeamsOperationQueue.CurrentStep = "Selecting command";

                // First dump what's in the dropdown so we can see the DOM
                var dumpResult = await EvalOnUiThread(instance, @"(function() {
                    var all = document.querySelectorAll('[role=""option""], [role=""listbox""] *, [class*=""suggest""], [class*=""Suggest""], [class*=""command""], [class*=""Command""], [class*=""slash""], [data-tid*=""suggest""], [data-tid*=""command""]');
                    var found = [];
                    for (var i = 0; i < all.length && i < 15; i++) {
                        found.push({
                            tag: all[i].tagName,
                            role: all[i].getAttribute('role') || '',
                            ariaLabel: (all[i].getAttribute('aria-label') || '').substring(0, 80),
                            text: (all[i].textContent || '').trim().substring(0, 80),
                            dataTid: all[i].getAttribute('data-tid') || '',
                            className: (all[i].className || '').substring(0, 80),
                            id: all[i].id || ''
                        });
                    }
                    // Also check for any clickable items near the search
                    var clickables = document.querySelectorAll('ul li, [role=""menu""] *, [role=""menuitem""], [class*=""result""] *, [class*=""list""] [class*=""item""]');
                    var extras = [];
                    for (var j = 0; j < clickables.length && j < 10; j++) {
                        var text = (clickables[j].textContent || '').trim();
                        if (text.indexOf('/') >= 0 || text.toLowerCase().indexOf('busy') >= 0 || text.toLowerCase().indexOf('available') >= 0 || text.toLowerCase().indexOf('status') >= 0) {
                            extras.push({ tag: clickables[j].tagName, text: text.substring(0, 80), role: clickables[j].getAttribute('role') || '' });
                        }
                    }
                    return JSON.stringify({ options: found, slashItems: extras });
                })();");
                Log.Information("SetStatus: autocomplete DOM dump: {Dump}", dumpResult);

                var selectResult = await EvalOnUiThread(instance, @"(function() {
                    // Strategy 1: role=""option"" items
                    var options = document.querySelectorAll('[role=""option""]');
                    if (options.length > 0) { options[0].click(); return 'clicked_option:' + (options[0].textContent || '').trim().substring(0, 40); }

                    // Strategy 2: role=""listbox"" children
                    var listbox = document.querySelector('[role=""listbox""]');
                    if (listbox && listbox.children.length > 0) { listbox.children[0].click(); return 'clicked_listbox_child'; }

                    // Strategy 3: any element containing the slash command text
                    var all = document.querySelectorAll('*');
                    for (var i = 0; i < all.length; i++) {
                        var t = (all[i].textContent || '').trim();
                        var c = all[i].children.length;
                        // Leaf or near-leaf element with slash command text
                        if (c < 3 && (t === '/busy' || t === '/available' || t === '/away' || t === '/dnd' || t === '/brb'
                            || t === 'Set status to Busy' || t === 'Set status to Available'
                            || t.indexOf('Set your status') >= 0)) {
                            all[i].click();
                            return 'clicked_text_match:' + t.substring(0, 40);
                        }
                    }

                    // Strategy 4: press Enter
                    var el = document.activeElement || document.querySelector('input[aria-label*=""Search""]');
                    if (el) {
                        el.dispatchEvent(new KeyboardEvent('keydown', {key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true}));
                        return 'enter_dispatched';
                    }
                    return 'nothing_found';
                })();");
                Log.Information("SetStatus: select result = {Result}", selectResult);

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
        /// Opens the 1:1 chat with the sender, types "Hi", waits (typing indicator shows),
        /// then clears the text. Does not send anything.
        /// </summary>
        public static async Task<bool> SimulateTypingAsync(string senderName)
        {
            var searchName = ExtractSearchName(senderName);
            Log.Information("SimulateTyping: start for '{Name}' (search: '{Search}')", senderName, searchName);

            var instance = WebViewManager.Instance.TeamsAutomationInstance;
            if (instance == null || !instance.IsReady)
            {
                Log.Warning("SimulateTyping: TeamsAutomation WebView not available");
                return false;
            }

            var userId = ResolveTeamsUserId(searchName);
            if (userId == null)
            {
                Log.Warning("SimulateTyping: could not resolve Teams user ID for '{Name}'", searchName);
                return false;
            }

            try
            {
                // Navigate to the 1:1 chat via deep link
                TeamsOperationQueue.CurrentStep = "Opening chat";
                var chatUrl = $"https://teams.microsoft.com/l/chat/0/0?users={Uri.EscapeDataString(userId)}";
                Log.Information("SimulateTyping: navigating to {Url}", chatUrl);
                await NavigateOnUiThread(instance,chatUrl);
                await Task.Delay(3000);

                // Find the compose box
                TeamsOperationQueue.CurrentStep = "Finding compose box";
                var composeFound = await EvalOnUiThread(instance,@"(function() {
                    var el = document.querySelector('[data-tid=""ckeditor-replyConversation""]')
                         || document.querySelector('[role=""textbox""][contenteditable=""true""]')
                         || document.querySelector('div[contenteditable=""true""]');
                    if (el) { el.focus(); return 'found'; }
                    return 'not_found';
                })();");

                if (composeFound != "found")
                {
                    Log.Warning("SimulateTyping: compose box not found");
                    TeamsOperationQueue.CurrentStep = "Failed — compose box not found";
                    await NavigateBackToTeams(instance);
                    return false;
                }

                // Type "Hi" — sender sees "is typing..."
                TeamsOperationQueue.CurrentStep = "Typing indicator active";
                await EvalOnUiThread(instance,@"(function() {
                    var el = document.querySelector('[data-tid=""ckeditor-replyConversation""]')
                         || document.querySelector('[role=""textbox""][contenteditable=""true""]')
                         || document.querySelector('div[contenteditable=""true""]');
                    if (!el) return;
                    el.textContent = 'Hi';
                    el.dispatchEvent(new Event('input', { bubbles: true }));
                })();");

                // Hold for configured duration so the typing indicator stays visible
                var typingDuration = MeetNowSettings.Instance.SimulateTypingDurationSeconds * 1000;
                Log.Information("SimulateTyping: holding for {Duration}s", typingDuration / 1000);
                await Task.Delay(typingDuration);

                // Clear the text without sending
                TeamsOperationQueue.CurrentStep = "Clearing text";
                await EvalOnUiThread(instance,@"(function() {
                    var el = document.querySelector('[data-tid=""ckeditor-replyConversation""]')
                         || document.querySelector('[role=""textbox""][contenteditable=""true""]')
                         || document.querySelector('div[contenteditable=""true""]');
                    if (!el) return;
                    el.textContent = '';
                    el.dispatchEvent(new Event('input', { bubbles: true }));
                })();");

                // Navigate back
                TeamsOperationQueue.CurrentStep = "Navigating back";
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
            var searchName = ExtractSearchName(senderName);
            Log.Information("SendMessage: start for '{Name}' (search: '{Search}'), message: '{Message}'",
                senderName, searchName, message);

            var instance = WebViewManager.Instance.TeamsAutomationInstance;
            if (instance == null || !instance.IsReady)
            {
                Log.Warning("SendMessage: TeamsAutomation WebView not available");
                return false;
            }

            var userId = ResolveTeamsUserId(searchName);
            if (userId == null)
            {
                Log.Warning("SendMessage: could not resolve Teams user ID for '{Name}'", searchName);
                return false;
            }

            try
            {
                // Navigate to the 1:1 chat via deep link
                TeamsOperationQueue.CurrentStep = "Opening chat";
                var chatUrl = $"https://teams.microsoft.com/l/chat/0/0?users={Uri.EscapeDataString(userId)}";
                Log.Information("SendMessage: navigating to {Url}", chatUrl);
                await NavigateOnUiThread(instance,chatUrl);
                await Task.Delay(3000);

                // Find the compose box
                TeamsOperationQueue.CurrentStep = "Finding compose box";
                var composeFound = await EvalOnUiThread(instance,@"(function() {
                    var el = document.querySelector('[data-tid=""ckeditor-replyConversation""]')
                         || document.querySelector('[role=""textbox""][contenteditable=""true""]')
                         || document.querySelector('div[contenteditable=""true""]');
                    if (el) { el.focus(); return 'found'; }
                    return 'not_found';
                })();");

                if (composeFound != "found")
                {
                    Log.Warning("SendMessage: compose box not found");
                    TeamsOperationQueue.CurrentStep = "Failed — compose box not found";
                    await NavigateBackToTeams(instance);
                    return false;
                }

                // Type the message
                TeamsOperationQueue.CurrentStep = "Typing message";
                var escapedMessage = message.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n").Replace("\r", "");
                await EvalOnUiThread(instance,$@"(function() {{
                    var el = document.querySelector('[data-tid=""ckeditor-replyConversation""]')
                         || document.querySelector('[role=""textbox""][contenteditable=""true""]')
                         || document.querySelector('div[contenteditable=""true""]');
                    if (!el) return;
                    el.textContent = '{escapedMessage}';
                    el.dispatchEvent(new Event('input', {{ bubbles: true }}));
                }})();");

                await Task.Delay(500);

                // Press Enter to send
                TeamsOperationQueue.CurrentStep = "Sending message";
                await EvalOnUiThread(instance,@"(function() {
                    var el = document.querySelector('[data-tid=""ckeditor-replyConversation""]')
                         || document.querySelector('[role=""textbox""][contenteditable=""true""]')
                         || document.querySelector('div[contenteditable=""true""]');
                    if (!el) return;
                    el.dispatchEvent(new KeyboardEvent('keydown', {
                        key: 'Enter', code: 'Enter', keyCode: 13, which: 13,
                        bubbles: true, cancelable: true
                    }));
                })();");

                await Task.Delay(1000);

                // Navigate back
                TeamsOperationQueue.CurrentStep = "Navigating back";
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
