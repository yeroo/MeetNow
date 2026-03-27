using System;

namespace MeetNow.Models
{
    public enum ContactSource { Chat, Search, Manual }
    public enum EnrichmentStatus { Pending, Enriched, Failed }

    public class Contact
    {
        public string TeamsUserId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string? Email { get; set; }
        public string? JobTitle { get; set; }
        public string? Department { get; set; }
        public string? Phone { get; set; }
        public string? ProfilePictureUrl { get; set; }
        public DateTime LastSeenTimestamp { get; set; }
        public bool IsPinned { get; set; }
        public ContactSource Source { get; set; }
        public DateTime LastUpdated { get; set; }
        public EnrichmentStatus EnrichmentStatus { get; set; } = EnrichmentStatus.Pending;
    }
}
