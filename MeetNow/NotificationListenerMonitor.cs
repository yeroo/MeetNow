using MeetNow.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace MeetNow
{
    /// <summary>
    /// Monitors Teams messages by:
    /// 1. Polling the Teams window title for unread count changes
    /// 2. Scanning the UIA tree for toast notification elements
    /// Does NOT require package identity or admin rights.
    /// </summary>
    public class NotificationListenerMonitor : IDisposable
    {
        private readonly string _currentUserName;
        private readonly HashSet<string> _processedToasts = new();
        private Timer? _pollTimer;
        private bool _disposed;
        private bool _polling;
        private string _lastTeamsTitle = "";
        private int _lastUnreadCount;

        private const int PollIntervalMs = 3000; // 3 seconds

        // Match "(N) " prefix in Teams window title
        private static readonly Regex UnreadCountRegex = new(@"^\((\d+)\)\s", RegexOptions.Compiled);

        // Win32 imports
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        public event Action<TeamsMessage>? NewMessageDetected;

        public NotificationListenerMonitor(string currentUserName)
        {
            _currentUserName = currentUserName;
        }

        public System.Threading.Tasks.Task<bool> StartAsync()
        {
            return System.Threading.Tasks.Task.FromResult(Start());
        }

        public bool Start()
        {
            try
            {
                // Initialize current Teams title
                var teamsWindow = FindTeamsWindow();
                if (teamsWindow.hwnd != IntPtr.Zero)
                {
                    _lastTeamsTitle = teamsWindow.title;
                    var match = UnreadCountRegex.Match(_lastTeamsTitle);
                    _lastUnreadCount = match.Success ? int.Parse(match.Groups[1].Value) : 0;
                    Log.Information("Teams window found: title={Title}, unread={Count}",
                        _lastTeamsTitle, _lastUnreadCount);
                }
                else
                {
                    Log.Warning("Teams window not found at startup - will keep checking");
                }

                // Start polling
                _pollTimer = new Timer(PollForChanges, null, PollIntervalMs, PollIntervalMs);
                Log.Information("Teams notification polling started (every {Interval}s)", PollIntervalMs / 1000);

                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to start notification monitor");
                return false;
            }
        }

        private void PollForChanges(object? state)
        {
            if (_polling || _disposed) return;
            _polling = true;

            try
            {
                // Check Teams window title for unread count changes
                CheckTeamsWindowTitle();

                // Scan UIA tree for active toast notification elements
                ScanForToastElements();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in notification poll");
            }
            finally
            {
                _polling = false;
            }
        }

        private void CheckTeamsWindowTitle()
        {
            var teamsWindow = FindTeamsWindow();
            if (teamsWindow.hwnd == IntPtr.Zero) return;

            var currentTitle = teamsWindow.title;
            if (currentTitle == _lastTeamsTitle) return;

            // Title changed
            var match = UnreadCountRegex.Match(currentTitle);
            int currentUnread = match.Success ? int.Parse(match.Groups[1].Value) : 0;

            if (currentUnread > _lastUnreadCount)
            {
                int newMessages = currentUnread - _lastUnreadCount;
                Log.Information("Teams unread count changed: {Old} -> {New} ({Delta} new)",
                    _lastUnreadCount, currentUnread, newMessages);

                // Extract what we can from the title
                // Title format: "(N) Chat | Sender Name | Microsoft Teams"
                // or: "(N) Activity | Microsoft Teams"
                var titleParts = currentTitle.Split('|').Select(p => p.Trim()).ToArray();
                string context = titleParts.Length > 0 ? UnreadCountRegex.Replace(titleParts[0], "").Trim() : "Teams";
                string sender = titleParts.Length > 1 ? titleParts[1] : "Unknown";

                // Skip "Microsoft Teams" as sender
                if (sender.Equals("Microsoft Teams", StringComparison.OrdinalIgnoreCase) && titleParts.Length > 2)
                    sender = titleParts.Length > 1 ? titleParts[1] : "Unknown";

                var message = new TeamsMessage
                {
                    Id = $"title_{DateTime.UtcNow.Ticks}",
                    Sender = sender,
                    Content = $"New message in {context}",
                    Timestamp = DateTime.Now,
                    ThreadType = context.Equals("Chat", StringComparison.OrdinalIgnoreCase) ? "chat" : "channel",
                    IsMention = !string.IsNullOrEmpty(_currentUserName) &&
                        currentTitle.Contains(_currentUserName, StringComparison.OrdinalIgnoreCase)
                };

                NewMessageDetected?.Invoke(message);
            }
            else if (currentTitle != _lastTeamsTitle)
            {
                Log.Information("Teams title changed: {Title}", currentTitle);
            }

            _lastTeamsTitle = currentTitle;
            _lastUnreadCount = currentUnread;
        }

        private void ScanForToastElements()
        {
            try
            {
                // Walk the desktop's top-level UIA children looking for toast notification elements
                var root = AutomationElement.RootElement;
                var walker = TreeWalker.ControlViewWalker;
                var child = walker.GetFirstChild(root);
                int maxChildren = 50;

                while (child != null && maxChildren-- > 0)
                {
                    try
                    {
                        var className = child.Current.ClassName ?? "";
                        var name = child.Current.Name ?? "";
                        var controlType = child.Current.ControlType;

                        // Look for toast notification containers
                        // On Windows 11, toasts appear as top-level elements with specific names/classes
                        bool isToast = false;

                        if (className.Contains("Toast", StringComparison.OrdinalIgnoreCase) ||
                            className.Contains("Notification", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("New notification", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Notification", StringComparison.OrdinalIgnoreCase))
                        {
                            isToast = true;
                        }

                        // Also check for unnamed windows that might be toasts
                        // (Windows 11 toast popups from ShellExperienceHost)
                        if (!isToast && controlType == ControlType.Window)
                        {
                            int pid = child.Current.ProcessId;
                            try
                            {
                                var proc = Process.GetProcessById(pid);
                                if (proc.ProcessName.Contains("ShellExperience", StringComparison.OrdinalIgnoreCase))
                                {
                                    // This is a ShellExperienceHost element - check if it has toast content
                                    var texts = ExtractAllText(child);
                                    if (texts.Count > 1)
                                    {
                                        var key = $"shell_{string.Join("_", texts.Take(3))}";
                                        if (!_processedToasts.Contains(key))
                                        {
                                            _processedToasts.Add(key);
                                            Log.Information("ShellExperienceHost element: name={Name}, texts={Texts}",
                                                name, string.Join(" | ", texts));
                                            ProcessToastTexts(texts);
                                        }
                                    }
                                }
                            }
                            catch { }
                        }

                        if (isToast)
                        {
                            var texts = ExtractAllText(child);
                            var key = $"toast_{name}_{string.Join("_", texts.Take(3))}";
                            if (!_processedToasts.Contains(key) && texts.Count > 0)
                            {
                                _processedToasts.Add(key);
                                Log.Information("Toast element found: name={Name}, class={Class}, texts={Texts}",
                                    name, className, string.Join(" | ", texts));
                                ProcessToastTexts(texts);
                            }
                        }
                    }
                    catch { }

                    try { child = walker.GetNextSibling(child); } catch { break; }
                }

                if (_processedToasts.Count > 5000)
                    _processedToasts.Clear();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Error scanning for toast elements");
            }
        }

        private (IntPtr hwnd, string title) FindTeamsWindow()
        {
            IntPtr foundHwnd = IntPtr.Zero;
            string foundTitle = "";

            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;

                var classBuf = new StringBuilder(256);
                GetClassName(hwnd, classBuf, 256);
                var className = classBuf.ToString();

                // Teams 2.0 uses "TeamsWebView" class
                if (className != "TeamsWebView") return true;

                var titleBuf = new StringBuilder(512);
                GetWindowText(hwnd, titleBuf, 512);
                var title = titleBuf.ToString();

                if (title.Contains("Microsoft Teams", StringComparison.OrdinalIgnoreCase))
                {
                    foundHwnd = hwnd;
                    foundTitle = title;
                    return false; // Stop enumeration
                }

                return true;
            }, IntPtr.Zero);

            return (foundHwnd, foundTitle);
        }

        private void ProcessToastTexts(List<string> texts)
        {
            if (texts.Count < 2) return;

            // Check if it's a Teams notification
            bool isTeams = texts.Any(t =>
                t.Contains("Teams", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Microsoft Teams", StringComparison.OrdinalIgnoreCase));

            if (!isTeams) return;

            var senderName = texts[0];
            var messageBody = string.Join(" ", texts.Skip(1));

            if (senderName.Equals("Microsoft Teams", StringComparison.OrdinalIgnoreCase) && texts.Count > 2)
            {
                senderName = texts[1];
                messageBody = string.Join(" ", texts.Skip(2));
            }

            if (string.IsNullOrWhiteSpace(messageBody) || messageBody.Length < 3)
                return;

            var message = new TeamsMessage
            {
                Id = $"toast_{DateTime.UtcNow.Ticks}",
                Sender = senderName,
                Content = messageBody,
                Timestamp = DateTime.Now,
                ThreadType = "chat",
                IsMention = !string.IsNullOrEmpty(_currentUserName) &&
                    messageBody.Contains(_currentUserName, StringComparison.OrdinalIgnoreCase)
            };

            Log.Information("Teams toast detected: from={Sender}, content={Content}",
                message.Sender,
                message.Content.Length > 80 ? message.Content[..80] + "..." : message.Content);

            NewMessageDetected?.Invoke(message);
        }

        private static List<string> ExtractAllText(AutomationElement root)
        {
            var texts = new List<string>();
            try { ExtractTextRecursive(root, texts, 0, 4); }
            catch { }
            return texts;
        }

        private static void ExtractTextRecursive(AutomationElement element, List<string> texts, int depth, int maxDepth)
        {
            if (depth > maxDepth || texts.Count > 15) return;

            try
            {
                var name = element.Current.Name;
                if (!string.IsNullOrWhiteSpace(name) && name.Length > 1 && !texts.Contains(name))
                    texts.Add(name);
            }
            catch { return; }

            try
            {
                var walker = TreeWalker.RawViewWalker;
                var child = walker.GetFirstChild(element);
                int maxChildren = 15;
                while (child != null && maxChildren-- > 0)
                {
                    ExtractTextRecursive(child, texts, depth + 1, maxDepth);
                    try { child = walker.GetNextSibling(child); } catch { break; }
                }
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pollTimer?.Dispose();
        }
    }
}
