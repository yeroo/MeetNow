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
                // Step 1: Find the search input
                TeamsOperationQueue.CurrentStep = "Finding search input";
                var searchFound = await instance.EvaluateJsAsync(@"(function() {
                    var el = document.querySelector('input[aria-label*=""Search""]')
                         || document.querySelector('input[placeholder*=""Search""]')
                         || document.querySelector('input[id=""searchInput""]');
                    if (el) { el.focus(); return 'found'; }
                    return 'not_found';
                })();");

                // If search input not found, try clicking a search button first
                if (searchFound != "found")
                {
                    TeamsOperationQueue.CurrentStep = "Clicking search button";
                    Log.Information("SetStatus: search input not found directly, trying search button");

                    await instance.EvaluateJsAsync(@"(function() {
                        var btn = document.querySelector('button[aria-label*=""Search""]')
                               || document.querySelector('[data-tid=""searchInputBox""]')
                               || document.querySelector('#searchInputBox');
                        if (btn) btn.click();
                    })();");

                    await Task.Delay(1000);

                    searchFound = await instance.EvaluateJsAsync(@"(function() {
                        var el = document.querySelector('input[aria-label*=""Search""]')
                             || document.querySelector('input[placeholder*=""Search""]')
                             || document.querySelector('input[id=""searchInput""]');
                        if (el) { el.focus(); return 'found'; }
                        return 'not_found';
                    })();");

                    if (searchFound != "found")
                    {
                        Log.Warning("SetStatus: could not find search input after clicking button");
                        TeamsOperationQueue.CurrentStep = "Failed — search input not found";
                        return false;
                    }
                }

                // Step 2: Type the slash command into the search input
                TeamsOperationQueue.CurrentStep = $"Typing {command}";
                var commandEscaped = command.Replace("'", "\\'");
                await instance.EvaluateJsAsync($@"(function() {{
                    var el = document.querySelector('input[aria-label*=""Search""]')
                         || document.querySelector('input[placeholder*=""Search""]')
                         || document.querySelector('input[id=""searchInput""]');
                    if (!el) return;
                    var nativeSetter = Object.getOwnPropertyDescriptor(
                        window.HTMLInputElement.prototype, 'value').set;
                    nativeSetter.call(el, '{commandEscaped}');
                    el.dispatchEvent(new Event('input', {{ bubbles: true }}));
                    el.dispatchEvent(new Event('change', {{ bubbles: true }}));
                }})();");

                // Step 3: Wait for autocomplete dropdown
                TeamsOperationQueue.CurrentStep = "Waiting for autocomplete";
                await Task.Delay(1500);

                // Step 4: Press Enter to execute the slash command
                TeamsOperationQueue.CurrentStep = "Enter (execute)";
                await instance.EvaluateJsAsync(@"(function() {
                    var el = document.querySelector('input[aria-label*=""Search""]')
                         || document.querySelector('input[placeholder*=""Search""]')
                         || document.querySelector('input[id=""searchInput""]');
                    if (!el) return;
                    el.dispatchEvent(new KeyboardEvent('keydown', {
                        key: 'Enter', code: 'Enter', keyCode: 13, which: 13,
                        bubbles: true, cancelable: true
                    }));
                })();");

                await Task.Delay(1000);

                // Step 5: Dismiss with Escape
                TeamsOperationQueue.CurrentStep = "Esc (cleanup)";
                await instance.EvaluateJsAsync(@"(function() {
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
                await instance.NavigateAndWaitAsync(chatUrl);
                await Task.Delay(3000);

                // Find the compose box
                TeamsOperationQueue.CurrentStep = "Finding compose box";
                var composeFound = await instance.EvaluateJsAsync(@"(function() {
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
                await instance.EvaluateJsAsync(@"(function() {
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
                await instance.EvaluateJsAsync(@"(function() {
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
                await instance.NavigateAndWaitAsync(chatUrl);
                await Task.Delay(3000);

                // Find the compose box
                TeamsOperationQueue.CurrentStep = "Finding compose box";
                var composeFound = await instance.EvaluateJsAsync(@"(function() {
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
                await instance.EvaluateJsAsync($@"(function() {{
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
                await instance.EvaluateJsAsync(@"(function() {
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
                await instance.NavigateAndWaitAsync("https://teams.microsoft.com");
                await Task.Delay(1000);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "NavigateBackToTeams: failed to navigate back");
            }
        }
    }
}
