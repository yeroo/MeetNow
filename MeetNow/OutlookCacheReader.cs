using IronSnappy;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MeetNow
{
    /// <summary>
    /// Reads calendar events from New Outlook's local IndexedDB cache.
    /// New Outlook stores offline data in a Chromium WebView2 LevelDB database.
    /// This reader parses SSTable (.ldb) files with Snappy decompression to extract
    /// calendar event data including start/end times, subjects, and Teams join URLs.
    /// </summary>
    public static class OutlookCacheReader
    {
        private static readonly string OlkIndexedDbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Olk", "EBWebView", "Default", "IndexedDB",
            "https_outlook.office.com_0.indexeddb.leveldb");

        // Field name byte patterns for V8 serialized IndexedDB data
        private static readonly byte[] StartFieldBytes = Encoding.ASCII.GetBytes("Start");
        private static readonly byte[] EndFieldBytes = Encoding.ASCII.GetBytes("End");
        private static readonly byte[] SubjectFieldBytes = Encoding.ASCII.GetBytes("Subject");
        private static readonly byte[] JoinUrlFieldBytes = Encoding.ASCII.GetBytes("JoinUrl");
        private static readonly byte[] OnlineMeetingJoinUrlFieldBytes = Encoding.ASCII.GetBytes("OnlineMeetingJoinUrl");
        private static readonly byte[] FreeBusyFieldBytes = Encoding.ASCII.GetBytes("FreeBusyType");
        private static readonly byte[] ItemClassFieldBytes = Encoding.ASCII.GetBytes("ItemClass");

        // Also look for Subject as UTF-16LE encoded field name
        private static readonly byte[] SubjectUtf16Bytes = Encoding.Unicode.GetBytes("Subject");
        private static readonly byte[] JoinUrlUtf16Bytes = Encoding.Unicode.GetBytes("JoinUrl");

        /// <summary>
        /// Reads today's calendar events from New Outlook's local cache.
        /// </summary>
        public static TeamsMeeting[] GetTodaysMeetings(DateTime date)
        {
            if (!Directory.Exists(OlkIndexedDbPath))
            {
                Log.Information("Outlook cache directory not found: {Path}", OlkIndexedDbPath);
                return Array.Empty<TeamsMeeting>();
            }

            var allBlocks = ReadAllDecompressedBlocks();
            if (allBlocks.Count == 0)
                return Array.Empty<TeamsMeeting>();

            Log.Information("OutlookCacheReader: {Count} decompressed blocks", allBlocks.Count);

            // Process each block independently to avoid cross-record contamination
            // Deduplicate by normalized subject + start time, preferring entries with Teams URLs
            var meetings = new List<TeamsMeeting>();
            var seenKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var block in allBlocks)
            {
                var blockMeetings = ExtractCalendarEvents(block, date);
                foreach (var m in blockMeetings)
                {
                    string normalizedSubject = StripResponsePrefix(m.Subject);
                    string dedupeKey = $"{normalizedSubject}|{m.Start:HH:mm}";

                    if (seenKeys.TryGetValue(dedupeKey, out int existingIdx))
                    {
                        // Replace if existing has no URL but this one does
                        if (string.IsNullOrEmpty(meetings[existingIdx].TeamsUrl) && !string.IsNullOrEmpty(m.TeamsUrl))
                            meetings[existingIdx] = m;
                    }
                    else
                    {
                        seenKeys[dedupeKey] = meetings.Count;
                        meetings.Add(m);
                    }
                }
            }

            // Remove "(No subject)" entries when a real entry exists at the same time
            var timesWithSubjects = new HashSet<string>(
                meetings.Where(m => m.Subject != "(No subject)")
                        .Select(m => m.Start.ToString("HH:mm")));
            meetings.RemoveAll(m => m.Subject == "(No subject)" && timesWithSubjects.Contains(m.Start.ToString("HH:mm")));

            Log.Information("OutlookCacheReader found {Count} meetings for {Date:yyyy-MM-dd}", meetings.Count, date);
            return meetings.OrderBy(m => m.Start).ToArray();
        }

        /// <summary>
        /// Strips "Accepted: ", "Declined: ", "Tentative: " prefixes from subject lines.
        /// IndexedDB stores response records with these prefixes as separate entries
        /// alongside the original event record.
        /// </summary>
        private static string StripResponsePrefix(string subject)
        {
            foreach (var prefix in new[] { "Accepted: ", "Declined: ", "Tentative: " })
            {
                if (subject.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return subject.Substring(prefix.Length);
            }
            return subject;
        }

        /// <summary>
        /// Reads all .ldb SSTable files and decompresses their data blocks.
        /// Returns one concatenated buffer per SSTable file to preserve record boundaries
        /// within a file while keeping different files separate to avoid cross-file contamination.
        /// </summary>
        private static List<byte[]> ReadAllDecompressedBlocks()
        {
            var perFileBlocks = new List<byte[]>();

            try
            {
                var ldbFiles = Directory.GetFiles(OlkIndexedDbPath, "*.ldb");
                foreach (var ldbFile in ldbFiles)
                {
                    try
                    {
                        var blocks = DecompressSstFile(ldbFile);
                        if (blocks.Count > 0)
                        {
                            // Concatenate all blocks within the same SSTable file
                            int totalLen = blocks.Sum(b => b.Length);
                            var combined = new byte[totalLen];
                            int offset = 0;
                            foreach (var block in blocks)
                            {
                                Buffer.BlockCopy(block, 0, combined, offset, block.Length);
                                offset += block.Length;
                            }
                            perFileBlocks.Add(combined);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "Error reading SSTable {File}", Path.GetFileName(ldbFile));
                    }
                }

                // Try to read .log file (WAL - may be locked, use shared access)
                var logFiles = Directory.GetFiles(OlkIndexedDbPath, "*.log");
                foreach (var logFile in logFiles)
                {
                    try
                    {
                        using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        var data = new byte[fs.Length];
                        fs.Read(data, 0, data.Length);
                        perFileBlocks.Add(data);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "Error reading log file {File}", Path.GetFileName(logFile));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error reading Outlook cache files");
            }

            return perFileBlocks;
        }

        /// <summary>
        /// Reads a block from the SSTable file and decompresses if needed.
        /// Block on disk: [block_data (size bytes)] [compression_type (1 byte)] [crc32 (4 bytes)]
        /// </summary>
        private static byte[]? ReadBlock(byte[] fileBytes, long offset, long size)
        {
            int bStart = (int)offset;
            int bDataLen = (int)size;
            int bEnd = bStart + bDataLen;

            if (bStart < 0 || bEnd + 5 > fileBytes.Length)
                return null;

            byte compressionType = fileBytes[bEnd]; // 0=none, 1=snappy

            if (compressionType == 1)
            {
                var compressed = new byte[bDataLen];
                Buffer.BlockCopy(fileBytes, bStart, compressed, 0, bDataLen);
                return Snappy.Decode(compressed);
            }
            else
            {
                var block = new byte[bDataLen];
                Buffer.BlockCopy(fileBytes, bStart, block, 0, bDataLen);
                return block;
            }
        }

        /// <summary>
        /// Decompresses data blocks from a LevelDB SSTable (.ldb) file.
        /// </summary>
        private static List<byte[]> DecompressSstFile(string filePath)
        {
            var results = new List<byte[]>();
            var fileBytes = File.ReadAllBytes(filePath);

            if (fileBytes.Length < 48)
                return results;

            // Read footer (last 48 bytes)
            int footerStart = fileBytes.Length - 48;
            byte[] magic = { 0x57, 0xfb, 0x80, 0x8b, 0x24, 0x75, 0x47, 0xdb };
            bool hasMagic = true;
            for (int i = 0; i < 8; i++)
            {
                if (fileBytes[footerStart + 40 + i] != magic[i])
                {
                    hasMagic = false;
                    break;
                }
            }

            if (!hasMagic)
            {
                return new List<byte[]> { fileBytes };
            }

            // Parse footer: metaindex_handle + index_handle
            int pos = footerStart;
            ReadVarint64(fileBytes, pos, out int bytesRead); pos += bytesRead; // metaindex offset
            ReadVarint64(fileBytes, pos, out bytesRead); pos += bytesRead;     // metaindex size
            long indexOffset = ReadVarint64(fileBytes, pos, out bytesRead); pos += bytesRead;
            long indexSize = ReadVarint64(fileBytes, pos, out bytesRead);

            if (indexOffset < 0 || indexOffset + indexSize + 5 > fileBytes.Length)
                return new List<byte[]> { fileBytes };

            // Read and decompress the index block itself
            byte[]? indexBlockData = ReadBlock(fileBytes, indexOffset, indexSize);
            if (indexBlockData == null || indexBlockData.Length < 4)
                return new List<byte[]> { fileBytes };

            // Parse the index block to get data block handles
            // Block format: [entries...] [restart_point_offsets (4 bytes each)] [num_restarts (4 bytes)]
            int numRestarts = BitConverter.ToInt32(indexBlockData, indexBlockData.Length - 4);
            int indexDataEnd = indexBlockData.Length - 4 - numRestarts * 4;

            var blockHandles = new List<(long offset, long size)>();
            pos = 0;
            while (pos < indexDataEnd && pos < indexBlockData.Length - 4)
            {
                int sharedBytes = (int)ReadVarint64(indexBlockData, pos, out bytesRead); pos += bytesRead;
                int nonSharedBytes = (int)ReadVarint64(indexBlockData, pos, out bytesRead); pos += bytesRead;
                int valueLength = (int)ReadVarint64(indexBlockData, pos, out bytesRead); pos += bytesRead;

                if (sharedBytes < 0 || nonSharedBytes < 0 || valueLength < 0 ||
                    pos + nonSharedBytes + valueLength > indexBlockData.Length)
                    break;

                pos += nonSharedBytes; // skip key delta

                if (valueLength > 0 && pos + valueLength <= indexBlockData.Length)
                {
                    int handlePos = pos;
                    long blockOffset = ReadVarint64(indexBlockData, handlePos, out bytesRead); handlePos += bytesRead;
                    long blockSize = ReadVarint64(indexBlockData, handlePos, out bytesRead);
                    blockHandles.Add((blockOffset, blockSize));
                }
                pos += valueLength;
            }

            // Read and decompress each data block
            int successCount = 0;
            foreach (var (blockOffset, blockSize) in blockHandles)
            {
                try
                {
                    var blockData = ReadBlock(fileBytes, blockOffset, blockSize);
                    if (blockData != null && blockData.Length > 0)
                    {
                        results.Add(blockData);
                        successCount++;
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Error decompressing block at offset {Offset} in {File}",
                        blockOffset, Path.GetFileName(filePath));
                }
            }

            Log.Debug("SSTable {File}: {Handles} blocks found, {Success} decompressed",
                Path.GetFileName(filePath), blockHandles.Count, successCount);

            return results;
        }

        private static long ReadVarint64(byte[] data, int pos, out int bytesRead)
        {
            bytesRead = 0;
            long result = 0;
            int shift = 0;
            while (pos < data.Length && shift < 63)
            {
                byte b = data[pos];
                bytesRead++;
                pos++;
                result |= (long)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            return result;
        }

        /// <summary>
        /// Reads a length-prefixed string value from the V8 serialized data.
        /// Format: 0x22 (string tag) + length_byte + string_bytes
        /// For strings > 127 bytes, length uses varint encoding.
        /// </summary>
        private static string? ReadLengthPrefixedString(byte[] data, int offset)
        {
            if (offset >= data.Length) return null;

            int len = (int)ReadVarint64(data, offset, out int bytesRead);
            offset += bytesRead;

            if (len <= 0 || len > 10000 || offset + len > data.Length)
                return null;

            return Encoding.UTF8.GetString(data, offset, len);
        }

        /// <summary>
        /// Finds a field name pattern and reads the following string value.
        /// Pattern: fieldName + stringTag + varint(length) + string_value
        /// stringTag is 0x22 (Latin1/ASCII one-byte string) or 0x63 (UTF-16LE two-byte string)
        /// </summary>
        private static string? FindFieldValue(byte[] data, int start, int end, byte[] fieldBytes)
        {
            end = Math.Min(end, data.Length);

            for (int i = start; i <= end - fieldBytes.Length - 3; i++)
            {
                bool match = true;
                for (int j = 0; j < fieldBytes.Length; j++)
                {
                    if (data[i + j] != fieldBytes[j]) { match = false; break; }
                }
                if (!match) continue;

                int pos = i + fieldBytes.Length;
                if (pos >= end) continue;

                byte tag = data[pos];
                if (tag == 0x22) // One-byte string (Latin1/ASCII)
                {
                    pos++;
                    var value = ReadLengthPrefixedString(data, pos);
                    if (value != null && value.Length > 0)
                        return value;
                }
                else if (tag == 0x63) // Two-byte string (UTF-16LE)
                {
                    pos++;
                    int len = (int)ReadVarint64(data, pos, out int bytesRead);
                    pos += bytesRead;
                    if (len > 0 && len < 20000 && pos + len <= end)
                    {
                        var value = Encoding.Unicode.GetString(data, pos, len);
                        if (value.Length > 0)
                            return value;
                    }
                }
            }
            return null;
        }


        /// <summary>
        /// Finds ALL occurrences of a field and returns their values.
        /// </summary>
        private static List<(int offset, string value)> FindAllFieldValues(byte[] data, int start, int end, byte[] fieldBytes)
        {
            var results = new List<(int, string)>();
            end = Math.Min(end, data.Length);

            for (int i = start; i <= end - fieldBytes.Length - 3; i++)
            {
                bool match = true;
                for (int j = 0; j < fieldBytes.Length; j++)
                {
                    if (data[i + j] != fieldBytes[j]) { match = false; break; }
                }
                if (!match) continue;

                int pos = i + fieldBytes.Length;
                if (pos >= end) continue;

                byte tag = data[pos];
                if (tag == 0x22) // One-byte string
                {
                    pos++;
                    var value = ReadLengthPrefixedString(data, pos);
                    if (value != null && value.Length > 0)
                        results.Add((i, value));
                }
                else if (tag == 0x63) // Two-byte string (UTF-16LE)
                {
                    pos++;
                    int len = (int)ReadVarint64(data, pos, out int bytesRead);
                    pos += bytesRead;
                    if (len > 0 && len < 20000 && pos + len <= end)
                    {
                        var value = Encoding.Unicode.GetString(data, pos, len);
                        if (value.Length > 0)
                            results.Add((i, value));
                    }
                }
            }
            return results;
        }

        /// <summary>
        /// Extracts calendar events from the decompressed LevelDB data.
        /// Strategy: Find all "Start" date fields, then look for nearby End/Subject/JoinUrl fields.
        /// For events where Subject is in a separate record (after a large Preview field),
        /// we skip past the Preview content and search for Subject in the following data.
        /// </summary>
        private static TeamsMeeting[] ExtractCalendarEvents(byte[] data, DateTime targetDate)
        {
            var meetings = new List<TeamsMeeting>();
            var seenKeys = new HashSet<string>();

            string targetDateStr = targetDate.ToString("yyyy-MM-dd");

            // Find all Start date occurrences
            var startFields = FindAllFieldValues(data, 0, data.Length, StartFieldBytes);
            Log.Debug("OutlookCacheReader: found {Count} Start fields in block of {Size} bytes", startFields.Count, data.Length);

            foreach (var (startOffset, startValue) in startFields)
            {
                // Parse the start date
                if (!DateTime.TryParse(startValue, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var startDt))
                    continue;

                var localStart = startDt.Kind == DateTimeKind.Utc ? startDt.ToLocalTime() : startDt;

                // Filter: only events for the target date
                if (localStart.Date != targetDate.Date)
                    continue;

                // Search a window around this Start field for related fields
                // Keep it tight (4KB back, 3KB forward) to avoid matching neighboring records
                int windowStart = Math.Max(0, startOffset - 4000);
                int windowEnd = Math.Min(data.Length, startOffset + 3000);

                // Check if this is actually a calendar event (look for ItemClass = IPM.Schedule or FreeBusyType)
                var itemClass = FindFieldValue(data, windowStart, windowEnd, ItemClassFieldBytes);
                var freeBusy = FindFieldValue(data, windowStart, windowEnd, FreeBusyFieldBytes);

                // If itemClass contains "Schedule" or we have FreeBusyType, it's a calendar event
                bool isCalendarEvent = (itemClass != null && itemClass.Contains("Schedule")) ||
                                       freeBusy != null;

                if (!isCalendarEvent)
                    continue;

                // Extract End date
                string? endValue = FindFieldValue(data, startOffset + 20, windowEnd, EndFieldBytes);
                DateTime localEnd = localStart.AddHours(1); // default 1 hour
                if (endValue != null && DateTime.TryParse(endValue, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var endDt))
                {
                    localEnd = endDt.Kind == DateTimeKind.Utc ? endDt.ToLocalTime() : endDt;
                }

                // Extract Subject — search FORWARD first (up to 50KB to skip past large
                // HTML body fields), then fall back to backward search. Forward is preferred
                // because backward search can pick up Subject from a neighboring event's record.
                // The Subject is often in a separate IndexedDB summary record stored immediately
                // after the detail record (Start/End/Preview/HTMLBody), so it can be 30-40KB ahead.
                int forwardSearchEnd = Math.Min(data.Length, startOffset + 50000);
                string? subject = FindFieldValue(data, startOffset, forwardSearchEnd, SubjectFieldBytes);
                if (string.IsNullOrEmpty(subject))
                    subject = FindFieldValue(data, startOffset, forwardSearchEnd, SubjectUtf16Bytes);

                // Backward search as fallback (works when Subject is stored before Start)
                if (string.IsNullOrEmpty(subject))
                {
                    subject = FindFieldValue(data, windowStart, startOffset, SubjectFieldBytes);
                    if (string.IsNullOrEmpty(subject))
                        subject = FindFieldValue(data, windowStart, startOffset, SubjectUtf16Bytes);
                }

                if (string.IsNullOrEmpty(subject))
                    subject = "(No subject)";
                else
                    subject = StripResponsePrefix(subject);

                // Extract Teams join URL — search forward (same strategy as Subject)
                var joinUrl = FindFieldValue(data, startOffset, forwardSearchEnd, OnlineMeetingJoinUrlFieldBytes);
                if (string.IsNullOrEmpty(joinUrl))
                    joinUrl = FindFieldValue(data, startOffset, forwardSearchEnd, JoinUrlFieldBytes);
                if (string.IsNullOrEmpty(joinUrl))
                    joinUrl = FindFieldValue(data, windowStart, forwardSearchEnd, JoinUrlUtf16Bytes);

                // Deduplicate by normalized subject + start time
                string dedupeKey = $"{StripResponsePrefix(subject)}|{localStart:HH:mm}";
                if (seenKeys.Contains(dedupeKey))
                    continue;
                seenKeys.Add(dedupeKey);

                // URL-decode the join URL if present
                if (!string.IsNullOrEmpty(joinUrl))
                {
                    try { joinUrl = Uri.UnescapeDataString(joinUrl); } catch { }
                }

                Log.Information("OutlookCache event: {Start:HH:mm}-{End:HH:mm} {Subject} (URL={HasUrl})",
                    localStart, localEnd, subject, !string.IsNullOrEmpty(joinUrl));

                meetings.Add(new TeamsMeeting
                {
                    Subject = subject,
                    Start = localStart,
                    End = localEnd,
                    TeamsUrl = joinUrl ?? ""
                });
            }

            return meetings.OrderBy(m => m.Start).ToArray();
        }
    }
}
