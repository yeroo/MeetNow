using MeetNow.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace MeetNow
{
    /// <summary>
    /// Monitors for Teams message notifications using two approaches:
    /// 1. UI Automation: Detects Windows toast notification windows from Teams in real-time
    /// 2. LevelDB polling: Reads message content from Teams local data when available
    /// </summary>
    public class TeamsMessageMonitor : IDisposable
    {
        private const int LEVELDB_POLL_INTERVAL_SECONDS = 10;

        private Timer? _levelDbPollTimer;
        private string? _currentLogFile;
        private long _lastLogFileSize;
        private readonly HashSet<string> _processedMessageIds = new();
        private readonly string _currentUserName;
        private readonly string _dbPath;
        private bool _disposed;

        // Win32 hook for detecting toast windows
        private delegate void WinEventDelegate(
            IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
        private IntPtr _hookHandle;

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private const uint EVENT_OBJECT_SHOW = 0x8002;
        private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

        // Regex patterns for parsing LevelDB binary data (ASCII fields only)
        // Content/preview fields use byte-level parsing to handle both Latin1 and UTF-16LE encodings
        private static readonly Regex TimestampPattern = new(@"composetime"".{1,5}?(\d{4}-\d{2}-\d{2}T[\d:.]+Z)", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex SenderPattern = new(@"imdisplayname"".{1,5}?([^\x00-\x08""]{2,}?)""", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex SenderTokenPattern = new(@"fromDisplayNameInToken"".{1,5}?([^\x00-\x08""]{2,}?)""", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex MentionPattern = new(@"mentionsa.{0,3}?""([^""]+)""", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex ThreadTypePattern = new(@"threadType"".{1,5}?(chat|channel|meeting|topic|space)", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex ClientMessageIdPattern = new(@"clientmessageid"".{1,5}?([\d]+)", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex HtmlTagPattern = new(@"<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex IsLastFromMePattern = new(@"isLastMessageFromMeT", RegexOptions.Compiled);

        public event Action<TeamsMessage>? NewMessageDetected;

        public static string GetLevelDbPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", "MSTeams_8wekyb3d8bbwe", "LocalCache", "Microsoft", "MSTeams",
                "EBWebView", "WV2Profile_tfw", "IndexedDB",
                "https_teams.microsoft.com_0.indexeddb.leveldb");
        }

        public TeamsMessageMonitor(string currentUserName)
        {
            _currentUserName = currentUserName;
            _dbPath = GetLevelDbPath();
        }

        public bool Start()
        {
            // Start UI Automation hook for toast notifications
            StartToastHook();

            // Start LevelDB polling for message content enrichment
            if (Directory.Exists(_dbPath))
            {
                FindCurrentLogFile();
                _levelDbPollTimer = new Timer(PollLevelDb, null,
                    TimeSpan.FromSeconds(LEVELDB_POLL_INTERVAL_SECONDS),
                    TimeSpan.FromSeconds(LEVELDB_POLL_INTERVAL_SECONDS));
                Log.Information("LevelDB polling started on: {Path}", _dbPath);
            }

            return true;
        }

        #region Toast Notification Detection via UI Automation

        private void StartToastHook()
        {
            try
            {
                // Use Automation to watch for new notification elements
                // Subscribe to structure change events on the desktop
                Automation.AddAutomationEventHandler(
                    WindowPattern.WindowOpenedEvent,
                    AutomationElement.RootElement,
                    TreeScope.Subtree,
                    OnWindowOpened);

                Log.Information("UI Automation toast hook registered");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to register UI Automation hook");
            }
        }

        private void OnWindowOpened(object sender, AutomationEventArgs e)
        {
            try
            {
                if (sender is not AutomationElement element)
                    return;

                string className;
                string name;
                int processId;
                try
                {
                    className = element.Current.ClassName ?? "";
                    name = element.Current.Name ?? "";
                    processId = element.Current.ProcessId;
                }
                catch
                {
                    return; // Element may have been destroyed
                }

                // Windows toast notifications use "Windows.UI.Core.CoreWindow"
                // with name "New notification" or the notification content
                // Also check for "Windows.UI.Notifications...." windows
                bool isToast = className == "Windows.UI.Core.CoreWindow" ||
                               className.Contains("NotificationToast") ||
                               className == "Shell_NotifyBubble";

                if (!isToast)
                    return;

                // Check if this is from a Teams process
                bool isTeams = false;
                try
                {
                    var proc = Process.GetProcessById(processId);
                    isTeams = proc.ProcessName.Contains("msedgewebview2", StringComparison.OrdinalIgnoreCase) ||
                              proc.ProcessName.Contains("MSTeams", StringComparison.OrdinalIgnoreCase);
                }
                catch { }

                // Also try to read the notification text from the UI element tree
                if (!string.IsNullOrEmpty(name))
                {
                    Log.Information("Toast window detected: class={Class}, name={Name}, pid={Pid}, isTeams={IsTeams}",
                        className, name.Length > 80 ? name[..80] : name, processId, isTeams);
                }

                // Try to extract notification text from child elements
                var textParts = ExtractTextFromElement(element);
                if (textParts.Count > 0)
                {
                    Log.Information("Toast text parts: {Parts}", string.Join(" | ", textParts));

                    // Parse sender and message from notification text
                    // Typical Teams toast: title = sender name, body = message preview
                    var toastSender = textParts.Count > 0 ? textParts[0] : "Unknown";
                    var content = textParts.Count > 1 ? string.Join(" ", textParts.Skip(1)) : name;

                    if (!string.IsNullOrWhiteSpace(content) && content.Length > 2)
                    {
                        var message = new TeamsMessage
                        {
                            Id = $"toast_{DateTime.UtcNow.Ticks}",
                            Sender = toastSender,
                            Content = content,
                            Timestamp = DateTime.Now,
                            ThreadType = "chat", // Assume chat for toast notifications
                            IsMention = !string.IsNullOrEmpty(_currentUserName) &&
                                content.Contains(_currentUserName, StringComparison.OrdinalIgnoreCase)
                        };

                        Log.Information("Teams notification: from={ToastSender}, content={Content}",
                            message.Sender, message.Content.Length > 80 ? message.Content[..80] + "..." : message.Content);

                        NewMessageDetected?.Invoke(message);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Error processing window event");
            }
        }

        private static List<string> ExtractTextFromElement(AutomationElement element)
        {
            var texts = new List<string>();
            try
            {
                // Walk the UI tree to find text elements
                var walker = TreeWalker.ContentViewWalker;
                var child = walker.GetFirstChild(element);
                int maxChildren = 20;
                while (child != null && maxChildren-- > 0)
                {
                    try
                    {
                        var childName = child.Current.Name;
                        if (!string.IsNullOrWhiteSpace(childName) && childName.Length > 1)
                        {
                            texts.Add(childName);
                        }

                        // Check grandchildren too
                        var grandchild = walker.GetFirstChild(child);
                        int maxGrandchildren = 10;
                        while (grandchild != null && maxGrandchildren-- > 0)
                        {
                            try
                            {
                                var gcName = grandchild.Current.Name;
                                if (!string.IsNullOrWhiteSpace(gcName) && gcName.Length > 1)
                                {
                                    texts.Add(gcName);
                                }
                            }
                            catch { }
                            try { grandchild = walker.GetNextSibling(grandchild); } catch { break; }
                        }
                    }
                    catch { }
                    try { child = walker.GetNextSibling(child); } catch { break; }
                }
            }
            catch { }
            return texts;
        }

        #endregion

        #region LevelDB Polling (for message content enrichment)

        private void FindCurrentLogFile()
        {
            try
            {
                var logFiles = Directory.GetFiles(_dbPath, "*.log");
                if (logFiles.Length > 0)
                {
                    _currentLogFile = logFiles
                        .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                        .First();
                    _lastLogFileSize = new FileInfo(_currentLogFile).Length;
                    Log.Information("Watching LevelDB log file: {File}, initial size: {Size} bytes",
                        Path.GetFileName(_currentLogFile), _lastLogFileSize);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error finding Teams LevelDB log file");
            }
        }

        private void PollLevelDb(object? state)
        {
            try
            {
                var logFiles = Directory.GetFiles(_dbPath, "*.log");
                if (logFiles.Length == 0) return;

                var latestLog = logFiles
                    .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                    .First();

                if (_currentLogFile != latestLog)
                {
                    Log.Information("New LevelDB log file detected: {File}", Path.GetFileName(latestLog));
                    _currentLogFile = latestLog;
                    _lastLogFileSize = 0;
                }

                if (_currentLogFile == null || !File.Exists(_currentLogFile))
                    return;

                long currentSize;
                try { currentSize = new FileInfo(_currentLogFile).Length; }
                catch { return; }

                if (currentSize <= _lastLogFileSize)
                    return;

                Log.Information("Teams LevelDB grew: {OldSize} -> {NewSize} bytes (+{Delta})",
                    _lastLogFileSize, currentSize, currentSize - _lastLogFileSize);

                // Read only new data
                byte[] newData;
                using (var fs = new FileStream(_currentLogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    fs.Seek(_lastLogFileSize, SeekOrigin.Begin);
                    newData = new byte[currentSize - _lastLogFileSize];
                    int bytesRead = fs.Read(newData, 0, newData.Length);
                    if (bytesRead < newData.Length)
                        Array.Resize(ref newData, bytesRead);
                }
                _lastLogFileSize = currentSize;

                var messages = ParseMessages(newData);
                Log.Information("Parsed {Count} messages from LevelDB delta (raw bytes sample: {Sample})",
                    messages.Count,
                    messages.Count == 0 && newData.Length > 0
                        ? Encoding.UTF8.GetString(newData, 0, Math.Min(500, newData.Length)).Replace("\0", "·").Replace("\n", "\\n").Replace("\r", "")
                        : "n/a");

                foreach (var msg in messages)
                {
                    if (_processedMessageIds.Contains(msg.Id))
                        continue;
                    _processedMessageIds.Add(msg.Id);

                    if (string.IsNullOrEmpty(msg.Sender))
                        continue;

                    if (!string.IsNullOrEmpty(_currentUserName))
                    {
                        msg.IsMention = msg.MentionedNames.Any(name =>
                            name.Contains(_currentUserName, StringComparison.OrdinalIgnoreCase)) ||
                            msg.Content.Contains($"@{_currentUserName}", StringComparison.OrdinalIgnoreCase);
                    }

                    Log.Information("LevelDB message from {Sender}: {Content} (type={ThreadType}, mention={IsMention})",
                        msg.Sender, msg.Content.Length > 80 ? msg.Content[..80] + "..." : msg.Content,
                        msg.ThreadType, msg.IsMention);

                    NewMessageDetected?.Invoke(msg);
                }

                if (_processedMessageIds.Count > 10000)
                    _processedMessageIds.Clear();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error polling Teams LevelDB");
            }
        }

        /// <summary>
        /// Read a protobuf-style varint from the byte array at the given position.
        /// Returns the decoded integer value.
        /// </summary>
        private static int ReadVarint(byte[] data, int pos, out int bytesRead)
        {
            bytesRead = 0;
            int result = 0;
            int shift = 0;
            while (pos < data.Length)
            {
                byte b = data[pos];
                bytesRead++;
                pos++;
                result |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            return result;
        }

        /// <summary>
        /// Extract a string field value from raw LevelDB bytes.
        /// Field names are encoded as: 0x22 (string tag) + varint(fieldNameLength) + fieldNameBytes
        /// Values follow with either: 0x22 + varint(len) + Latin1 bytes (one-byte string)
        ///                         or: 0x63 + varint(len) + UTF-16LE bytes (two-byte string)
        /// Optional 0x00 padding may appear between field name and value tag.
        /// </summary>
        private static string ExtractFieldFromBytes(byte[] data, int start, int end, string fieldName)
        {
            // Build the exact byte pattern: 0x22 + varint(len) + fieldname bytes
            byte[] fnBytes = Encoding.ASCII.GetBytes(fieldName);
            byte fnLen = (byte)fnBytes.Length; // assumes field name < 128 chars

            end = Math.Min(end, data.Length);
            string bestResult = "";

            for (int i = start; i <= end - fnBytes.Length - 4; i++)
            {
                // Match: 0x22 + fnLen + fieldNameBytes
                if (data[i] != 0x22 || data[i + 1] != fnLen) continue;

                bool match = true;
                for (int j = 0; j < fnBytes.Length; j++)
                {
                    if (data[i + 2 + j] != fnBytes[j]) { match = false; break; }
                }
                if (!match) continue;

                // Found field name. Now read the value.
                int pos = i + 2 + fnBytes.Length;

                // Skip 0x00 padding bytes
                while (pos < end && data[pos] == 0x00) pos++;
                if (pos >= end) continue;

                byte tag = data[pos];
                pos++;

                if (tag == 0x22) // kOneByteString (Latin1/ASCII)
                {
                    int len = ReadVarint(data, pos, out int bytesRead);
                    pos += bytesRead;
                    if (len > 0 && len < 10000 && pos + len <= end)
                    {
                        var val = Encoding.UTF8.GetString(data, pos, len);
                        if (val.Length > bestResult.Length) bestResult = val;
                    }
                }
                else if (tag == 0x63) // kTwoByteString (UTF-16LE)
                {
                    int len = ReadVarint(data, pos, out int bytesRead);
                    pos += bytesRead;
                    if (len > 0 && len < 20000 && pos + len <= end)
                    {
                        var val = Encoding.Unicode.GetString(data, pos, len);
                        if (val.Length > bestResult.Length) bestResult = val;
                    }
                }
            }
            return bestResult;
        }

        /// <summary>
        /// Find the byte offset of an ASCII string near the expected position in the data array.
        /// </summary>
        private static int FindBytesNear(byte[] data, byte[] pattern, int approxOffset, int window = 500)
        {
            int start = Math.Max(0, approxOffset - window);
            int end = Math.Min(data.Length - pattern.Length, approxOffset + window);
            for (int i = start; i <= end; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        private List<TeamsMessage> ParseMessages(byte[] data)
        {
            var messages = new List<TeamsMessage>();
            var text = Encoding.UTF8.GetString(data);

            var timestamps = TimestampPattern.Matches(text);
            int skippedOld = 0, skippedOwnMsg = 0;
            Log.Debug("LevelDB delta: {Len} bytes, found {Count} composetime timestamps", data.Length, timestamps.Count);

            foreach (Match tsMatch in timestamps)
            {
                var timestamp = tsMatch.Groups[1].Value;
                if (!DateTime.TryParse(timestamp, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var dt))
                    continue;

                // Skip old messages (more than 5 minutes ago)
                if (dt.ToLocalTime() < DateTime.Now.AddMinutes(-5))
                {
                    skippedOld++;
                    continue;
                }

                int searchStart = Math.Max(0, tsMatch.Index - 3000);
                int searchEnd = Math.Min(text.Length, tsMatch.Index + 2000);
                var chunk = text[searchStart..searchEnd];

                // Skip if this is our own message
                if (IsLastFromMePattern.IsMatch(chunk))
                {
                    skippedOwnMsg++;
                    continue;
                }

                // Extract sender - try imdisplayname first, then fromDisplayNameInToken
                var senderMatch = SenderPattern.Match(chunk);
                if (!senderMatch.Success)
                    senderMatch = SenderTokenPattern.Match(chunk);
                string sender = senderMatch.Success ? senderMatch.Groups[1].Value.Trim() : "";

                // Extract content from raw bytes using proper V8 serialization decoding
                // This handles both Latin1 (tag 0x22) and UTF-16LE (tag 0x63) encodings
                string content = "";

                // Find exact byte offset of this timestamp in raw data
                byte[] tsBytes = Encoding.ASCII.GetBytes(timestamp);
                int byteOffset = FindBytesNear(data, tsBytes, tsMatch.Index, 1000);
                if (byteOffset >= 0)
                {
                    int bStart = Math.Max(0, byteOffset - 5000);
                    int bEnd = Math.Min(data.Length, byteOffset + 3000);

                    // Try preview first (plain text), then content
                    content = ExtractFieldFromBytes(data, bStart, bEnd, "preview");
                    if (string.IsNullOrEmpty(content))
                        content = ExtractFieldFromBytes(data, bStart, bEnd, "content");

                    // Strip HTML tags if content contains them
                    if (!string.IsNullOrEmpty(content) && content.Contains('<'))
                        content = HtmlTagPattern.Replace(content, "").Trim();
                }

                var threadMatch = ThreadTypePattern.Match(chunk);
                string threadType = threadMatch.Success ? threadMatch.Groups[1].Value : "";

                var idMatch = ClientMessageIdPattern.Match(chunk);
                string msgId = idMatch.Success ? idMatch.Groups[1].Value : $"{timestamp}_{sender.GetHashCode()}";

                var mentionMatches = MentionPattern.Matches(chunk);
                var mentionedNames = mentionMatches.Cast<Match>()
                    .Select(m => m.Groups[1].Value).Distinct().ToArray();

                Log.Information("LevelDB parsed: sender={Sender}, content={Content}, thread={Thread}, id={Id}",
                    sender, content.Length > 60 ? content[..60] : content, threadType, msgId);

                if (!string.IsNullOrEmpty(content) && !string.IsNullOrEmpty(sender))
                {
                    messages.Add(new TeamsMessage
                    {
                        Id = msgId, Sender = sender, Content = content,
                        Timestamp = dt.ToLocalTime(), ThreadType = threadType,
                        MentionedNames = mentionedNames
                    });
                }
            }

            if (messages.Count == 0 && timestamps.Count > 0)
                Log.Information("LevelDB parse stats: {Total} timestamps, {Old} too old, {Own} own messages",
                    timestamps.Count, skippedOld, skippedOwnMsg);

            return messages;
        }

        #endregion

        // Pattern to extract person display names from Teams chat list entries.
        // Chat list entries have profile picture URLs: orgid:<guid>?displayname=<url-encoded-name>&
        private static readonly Regex ChatContactPattern = new(
            @"orgid:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\?displayname=([^&]+)&",
            RegexOptions.Compiled);

        /// <summary>
        /// Scans Teams LevelDB files (.log and .ldb) to extract recent chat contact names.
        /// Uses two strategies:
        /// 1. Chat list entries with profile picture URLs containing displayname (from .log and .ldb)
        /// 2. Message records with imdisplayname/composetime (from .log only)
        /// Returns up to 30 unique contacts sorted by most recently seen.
        /// </summary>
        public static List<(string Name, DateTime LastSeen)> GetRecentContacts()
        {
            var dbPath = GetLevelDbPath();
            var contacts = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            if (!Directory.Exists(dbPath))
            {
                Log.Warning("LevelDB path not found for recent contacts: {Path}", dbPath);
                return new List<(string, DateTime)>();
            }

            try
            {
                // Scan all .ldb and .log files
                var files = Directory.GetFiles(dbPath, "*.ldb")
                    .Concat(Directory.GetFiles(dbPath, "*.log"))
                    .ToArray();

                foreach (var file in files)
                {
                    try
                    {
                        byte[] data;
                        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            data = new byte[fs.Length];
                            fs.Read(data, 0, data.Length);
                        }

                        var text = Encoding.UTF8.GetString(data);

                        // Strategy 1: Extract from chat list profile picture URLs (orgid?displayname=...)
                        foreach (Match m in ChatContactPattern.Matches(text))
                        {
                            var name = Uri.UnescapeDataString(m.Groups[1].Value).Trim();
                            if (name.Length < 3) continue;
                            if (!contacts.ContainsKey(name))
                                contacts[name] = DateTime.MinValue;
                        }

                        // Strategy 2: Extract from message records (composetime + imdisplayname) — .log files
                        if (file.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (Match tsMatch in TimestampPattern.Matches(text))
                            {
                                if (!DateTime.TryParse(tsMatch.Groups[1].Value, CultureInfo.InvariantCulture,
                                    DateTimeStyles.RoundtripKind, out var dt))
                                    continue;

                                int searchStart = Math.Max(0, tsMatch.Index - 3000);
                                int searchEnd = Math.Min(text.Length, tsMatch.Index + 2000);
                                var chunk = text[searchStart..searchEnd];

                                var senderMatch = SenderPattern.Match(chunk);
                                if (!senderMatch.Success)
                                    senderMatch = SenderTokenPattern.Match(chunk);
                                if (!senderMatch.Success) continue;

                                string sender = senderMatch.Groups[1].Value.Trim();
                                if (string.IsNullOrWhiteSpace(sender) || sender.Length < 2) continue;

                                var localTime = dt.ToLocalTime();
                                if (!contacts.TryGetValue(sender, out var existing) || localTime > existing)
                                    contacts[sender] = localTime;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "Error reading LevelDB file for contacts: {File}", Path.GetFileName(file));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error scanning LevelDB for recent contacts");
            }

            // Sort: contacts with timestamps first (most recent), then alphabetically
            return contacts
                .OrderByDescending(kvp => kvp.Value)
                .ThenBy(kvp => kvp.Key)
                .Take(30)
                .Select(kvp => (kvp.Key, kvp.Value))
                .ToList();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { Automation.RemoveAllEventHandlers(); } catch { }
            if (_hookHandle != IntPtr.Zero)
            {
                UnhookWinEvent(_hookHandle);
                _hookHandle = IntPtr.Zero;
            }
            _levelDbPollTimer?.Dispose();
        }
    }
}
