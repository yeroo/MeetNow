using MeetNow.Models;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace MeetNow
{
    public static class ContactDatabase
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MeetNow", "contacts.json");

        private static ConcurrentDictionary<string, Contact> _contacts = new(StringComparer.OrdinalIgnoreCase);
        private static Timer? _saveTimer;
        private static bool _dirty;
        private static readonly object _saveLock = new();

        static ContactDatabase()
        {
            Load();
            Prune();
        }

        public static List<Contact> GetByName(string query)
        {
            if (string.IsNullOrWhiteSpace(query) || query.Length < 3)
                return new List<Contact>();

            var search = query.Trim().ToLowerInvariant();
            return _contacts.Values
                .Where(c => MatchesWordBoundary(c.DisplayName, search)
                    || (c.Email != null && c.Email.Contains(search, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(c => c.IsPinned)
                .ThenByDescending(c => c.LastSeenTimestamp)
                .ToList();
        }

        public static Contact? GetByEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email)) return null;
            return _contacts.Values.FirstOrDefault(c =>
                string.Equals(c.Email, email, StringComparison.OrdinalIgnoreCase));
        }

        public static Contact? GetById(string teamsUserId)
        {
            _contacts.TryGetValue(teamsUserId, out var contact);
            return contact;
        }

        public static void Upsert(Contact contact)
        {
            _contacts.AddOrUpdate(contact.TeamsUserId,
                _ =>
                {
                    contact.LastUpdated = DateTime.Now;
                    return contact;
                },
                (_, existing) =>
                {
                    existing.DisplayName = !string.IsNullOrWhiteSpace(contact.DisplayName)
                        ? contact.DisplayName : existing.DisplayName;
                    existing.Email = contact.Email ?? existing.Email;
                    existing.JobTitle = contact.JobTitle ?? existing.JobTitle;
                    existing.Department = contact.Department ?? existing.Department;
                    existing.Phone = contact.Phone ?? existing.Phone;
                    existing.ProfilePictureUrl = contact.ProfilePictureUrl ?? existing.ProfilePictureUrl;
                    if (contact.LastSeenTimestamp > existing.LastSeenTimestamp)
                        existing.LastSeenTimestamp = contact.LastSeenTimestamp;
                    if (contact.EnrichmentStatus == EnrichmentStatus.Enriched)
                        existing.EnrichmentStatus = contact.EnrichmentStatus;
                    existing.LastUpdated = DateTime.Now;
                    return existing;
                });

            ScheduleSave();
        }

        public static List<Contact> GetPinned()
        {
            return _contacts.Values
                .Where(c => c.IsPinned)
                .OrderBy(c => c.DisplayName)
                .ToList();
        }

        public static void SetPinned(string teamsUserId, bool pinned)
        {
            if (_contacts.TryGetValue(teamsUserId, out var contact))
            {
                contact.IsPinned = pinned;
                contact.LastUpdated = DateTime.Now;
                ScheduleSave();
            }
        }

        public static List<Contact> GetAll()
        {
            return _contacts.Values
                .OrderByDescending(c => c.IsPinned)
                .ThenByDescending(c => c.LastSeenTimestamp)
                .ToList();
        }

        public static void Prune()
        {
            var cutoff = DateTime.Now.AddDays(-90);
            var toRemove = _contacts.Values
                .Where(c => !c.IsPinned && c.LastSeenTimestamp < cutoff)
                .Select(c => c.TeamsUserId)
                .ToList();

            foreach (var id in toRemove)
                _contacts.TryRemove(id, out _);

            if (toRemove.Count > 0)
            {
                Log.Information("ContactDatabase: pruned {Count} stale contacts", toRemove.Count);
                ScheduleSave();
            }
        }

        public static void FlushAndDispose()
        {
            _saveTimer?.Dispose();
            _saveTimer = null;
            if (_dirty) SaveNow();
        }

        private static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var list = JsonSerializer.Deserialize<List<Contact>>(json) ?? new();
                    _contacts = new ConcurrentDictionary<string, Contact>(
                        list.Where(c => !string.IsNullOrEmpty(c.TeamsUserId))
                            .ToDictionary(c => c.TeamsUserId, c => c),
                        StringComparer.OrdinalIgnoreCase);
                    Log.Information("ContactDatabase: loaded {Count} contacts", _contacts.Count);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load contact database");
            }
        }

        private static void ScheduleSave()
        {
            _dirty = true;
            _saveTimer ??= new Timer(_ => SaveNow(), null, 30000, Timeout.Infinite);
        }

        private static void SaveNow()
        {
            lock (_saveLock)
            {
                try
                {
                    var dir = Path.GetDirectoryName(FilePath)!;
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    var list = _contacts.Values.ToList();
                    var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(FilePath, json);
                    _dirty = false;
                    _saveTimer?.Dispose();
                    _saveTimer = null;
                    Log.Debug("ContactDatabase: saved {Count} contacts", list.Count);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to save contact database");
                }
            }
        }

        private static bool MatchesWordBoundary(string displayName, string search)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return false;
            var name = displayName.ToLowerInvariant();

            var words = name.Split(new[] { ' ', ',', '-', '/' }, StringSplitOptions.RemoveEmptyEntries);
            return words.Any(w => w.StartsWith(search)) || name.Contains(search);
        }
    }
}
