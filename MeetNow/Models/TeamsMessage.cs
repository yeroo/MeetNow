using System;

namespace MeetNow.Models
{
    public enum MessageUrgency
    {
        Low,
        Normal,
        Urgent
    }

    public class TeamsMessage
    {
        public string Id { get; set; } = string.Empty;
        public string Sender { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string ThreadType { get; set; } = string.Empty; // "chat", "space", "meeting"
        public bool IsMention { get; set; }
        public string[] MentionedNames { get; set; } = Array.Empty<string>();
        public MessageUrgency Urgency { get; set; } = MessageUrgency.Normal;
        public string UrgencyReason { get; set; } = string.Empty;

        public bool IsDirectChat => string.Equals(ThreadType, "chat", StringComparison.OrdinalIgnoreCase);
    }
}
