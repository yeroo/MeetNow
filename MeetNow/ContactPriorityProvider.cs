using MeetNow.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MeetNow
{
    /// <summary>
    /// Manages per-contact urgency priority overrides.
    /// Contacts can be promoted to always-urgent, always-normal, always-low,
    /// or left at "default" (use the normal classification algorithm).
    /// Persisted to %LOCALAPPDATA%\MeetNow\contact_priorities.json.
    /// </summary>
    public static class ContactPriorityProvider
    {
        public enum ContactPriority
        {
            Default,
            Low,
            Normal,
            Urgent
        }

        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MeetNow", "contact_priorities.json");

        private static Dictionary<string, ContactPriority>? _cache;

        private static Dictionary<string, ContactPriority> Load()
        {
            if (_cache != null) return _cache;

            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    _cache = JsonSerializer.Deserialize<Dictionary<string, ContactPriority>>(json)
                             ?? new Dictionary<string, ContactPriority>(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load contact priorities");
            }

            _cache ??= new Dictionary<string, ContactPriority>(StringComparer.OrdinalIgnoreCase);
            return _cache;
        }

        private static void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath)!;
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save contact priorities");
            }
        }

        /// <summary>
        /// Gets the priority override for a sender. Returns Default if none set.
        /// </summary>
        public static ContactPriority GetPriority(string sender)
        {
            if (string.IsNullOrWhiteSpace(sender)) return ContactPriority.Default;
            var dict = Load();
            return dict.TryGetValue(sender, out var priority) ? priority : ContactPriority.Default;
        }

        /// <summary>
        /// Sets a priority override for a sender. Setting to Default removes the override.
        /// </summary>
        public static void SetPriority(string sender, ContactPriority priority)
        {
            if (string.IsNullOrWhiteSpace(sender)) return;
            var dict = Load();

            if (priority == ContactPriority.Default)
                dict.Remove(sender);
            else
                dict[sender] = priority;

            Save();
            Log.Information("Contact priority set: {Sender} → {Priority}", sender, priority);
        }

        /// <summary>
        /// Returns all contacts with a specific priority.
        /// </summary>
        public static IReadOnlyList<string> GetContactsByPriority(ContactPriority priority)
        {
            var dict = Load();
            var result = new List<string>();
            foreach (var kvp in dict)
            {
                if (kvp.Value == priority)
                    result.Add(kvp.Key);
            }
            return result;
        }

        /// <summary>
        /// Tries to map the contact priority to a MessageUrgency.
        /// Returns true if there's an override (non-Default), false otherwise.
        /// </summary>
        public static bool TryGetUrgencyOverride(string sender, out MessageUrgency urgency, out string reason)
        {
            var priority = GetPriority(sender);
            switch (priority)
            {
                case ContactPriority.Urgent:
                    urgency = MessageUrgency.Urgent;
                    reason = "contact promoted to Urgent";
                    return true;
                case ContactPriority.Normal:
                    urgency = MessageUrgency.Normal;
                    reason = "contact promoted to Normal";
                    return true;
                case ContactPriority.Low:
                    urgency = MessageUrgency.Low;
                    reason = "contact promoted to Low";
                    return true;
                default:
                    urgency = MessageUrgency.Low;
                    reason = "";
                    return false;
            }
        }
    }
}
