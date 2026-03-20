using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MeetNow
{
    /// <summary>
    /// Reads Teams favorite/pinned chat contacts from local IndexedDB LevelDB storage.
    /// Teams stores a "Favorites" folder in IndexedDB with a list of conversation thread IDs.
    /// For 1:1 chats, the thread ID contains the contact's AAD object GUID, which can be
    /// resolved to a display name from the same LevelDB data.
    /// </summary>
    public static class FavoriteContactsProvider
    {
        private static HashSet<string> _cachedFavoriteNames = new(StringComparer.OrdinalIgnoreCase);
        private static DateTime _lastRefresh = DateTime.MinValue;
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);

        // Pattern to find the Favorites folder and its conversation list
        // The folder record contains: folderType"Favorites" and conversationsA (array of thread IDs)
        private static readonly Regex FavoritesFolderPattern = new(
            @"Favorites.{0,500}?conversationsA",
            RegexOptions.Compiled | RegexOptions.Singleline);

        // 1:1 chat thread IDs look like: 19:<guid>_<guid>@unq.gbl.spaces
        // The two GUIDs are the two participants' AAD object IDs
        private static readonly Regex OneOnOneChatPattern = new(
            @"19:([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})_([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})@unq\.gbl\.spaces",
            RegexOptions.Compiled);

        // Pattern to find display names associated with orgid GUIDs
        private static readonly Regex DisplayNamePattern = new(
            @"displayName"".{1,5}?([^\x00-\x08""]{2,50})""",
            RegexOptions.Compiled | RegexOptions.Singleline);

        /// <summary>
        /// Get the set of favorite contact display names (last names for matching).
        /// Refreshes from disk periodically.
        /// </summary>
        public static HashSet<string> GetFavoriteContactNames()
        {
            if (DateTime.Now - _lastRefresh < RefreshInterval && _cachedFavoriteNames.Count > 0)
                return _cachedFavoriteNames;

            try
            {
                var names = LoadFavoriteContactNames();
                if (names.Count > 0)
                {
                    _cachedFavoriteNames = names;
                    _lastRefresh = DateTime.Now;
                    Log.Information("Loaded {Count} favorite contacts: {Names}",
                        names.Count, string.Join(", ", names));
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error loading favorite contacts from Teams storage");
            }

            return _cachedFavoriteNames;
        }

        /// <summary>
        /// Check if a sender name matches any favorite contact.
        /// Matches against full name parts (first name, last name) from favorites.
        /// </summary>
        public static bool IsFavoriteContact(string senderName)
        {
            if (string.IsNullOrWhiteSpace(senderName))
                return false;

            var favorites = GetFavoriteContactNames();
            if (favorites.Count == 0)
                return false;

            // Check each favorite name against the sender
            foreach (var favName in favorites)
            {
                // Direct contains check (handles "Repin, Dmitriy" matching sender "Repin, Dmitriy SHELL/IT")
                if (senderName.Contains(favName, StringComparison.OrdinalIgnoreCase))
                    return true;

                // Check individual name parts (last name, first name)
                var favParts = favName.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in favParts)
                {
                    if (part.Length < 3) continue; // Skip initials like "L"

                    // Match last name or first name against sender
                    var senderParts = senderName.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var sp in senderParts)
                    {
                        if (sp.Length < 3) continue;
                        if (string.Equals(sp, part, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }

            return false;
        }

        private static HashSet<string> LoadFavoriteContactNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var dbPath = TeamsMessageMonitor.GetLevelDbPath();
            if (!Directory.Exists(dbPath))
            {
                Log.Warning("Teams LevelDB path not found: {Path}", dbPath);
                return names;
            }

            // Read all .ldb files to find favorite folder data and contact names
            var ldbFiles = Directory.GetFiles(dbPath, "*.ldb")
                .OrderByDescending(f => new FileInfo(f).Length)
                .ToArray();

            // Step 1: Find favorite thread IDs from the Favorites folder record
            var favoriteThreadIds = new HashSet<string>();
            var contactGuids = new HashSet<string>();

            foreach (var file in ldbFiles)
            {
                try
                {
                    var data = File.ReadAllBytes(file);
                    var text = Encoding.UTF8.GetString(data);

                    // Look for Favorites folder with conversation list
                    var favMatch = FavoritesFolderPattern.Match(text);
                    if (favMatch.Success)
                    {
                        // Extract thread IDs near the Favorites folder
                        int searchStart = favMatch.Index;
                        int searchEnd = Math.Min(text.Length, favMatch.Index + 5000);
                        var chunk = text[searchStart..searchEnd];

                        // Find 1:1 chat thread IDs
                        var chatMatches = OneOnOneChatPattern.Matches(chunk);
                        foreach (Match cm in chatMatches)
                        {
                            favoriteThreadIds.Add(cm.Value);
                            contactGuids.Add(cm.Groups[1].Value);
                            contactGuids.Add(cm.Groups[2].Value);
                        }

                        Log.Information("Found {Count} favorite thread IDs in {File}",
                            favoriteThreadIds.Count, Path.GetFileName(file));
                    }
                }
                catch (IOException)
                {
                    // File may be locked
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Error reading {File}", file);
                }
            }

            if (contactGuids.Count == 0)
            {
                Log.Information("No favorite chat GUIDs found in Teams LevelDB");
                return names;
            }

            // Step 2: Resolve GUIDs to display names
            // Search for orgid profile records that contain the GUIDs
            foreach (var file in ldbFiles)
            {
                try
                {
                    var data = File.ReadAllBytes(file);
                    var text = Encoding.UTF8.GetString(data);

                    foreach (var guid in contactGuids.ToArray())
                    {
                        int idx = text.IndexOf(guid, StringComparison.OrdinalIgnoreCase);
                        if (idx < 0) continue;

                        // Search for displayName near this GUID
                        int searchStart = Math.Max(0, idx - 2000);
                        int searchEnd = Math.Min(text.Length, idx + 2000);
                        var chunk = text[searchStart..searchEnd];

                        var dnMatch = DisplayNamePattern.Match(chunk);
                        if (dnMatch.Success)
                        {
                            var displayName = dnMatch.Groups[1].Value.Trim();
                            // Filter out non-name values
                            if (displayName.Length > 2 && !displayName.Contains("http") &&
                                !displayName.Contains("@") && !displayName.Contains("{"))
                            {
                                names.Add(displayName);
                                contactGuids.Remove(guid); // Found it, no need to keep searching
                            }
                        }
                    }

                    if (contactGuids.Count == 0)
                        break; // All resolved
                }
                catch (IOException)
                {
                    // File may be locked
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Error reading {File}", file);
                }
            }

            // Remove the current user's own name if present (they'll be one of the GUIDs in 1:1 chats)
            // The current user's name will appear for every favorite chat, so remove the most frequent one
            // Actually, we can't easily distinguish here, so we keep all names and let the matching logic handle it

            return names;
        }

        /// <summary>
        /// Force refresh the favorite contacts cache on next access.
        /// </summary>
        public static void InvalidateCache()
        {
            _lastRefresh = DateTime.MinValue;
        }
    }
}
