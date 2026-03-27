using MeetNow.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MeetNow
{
    /// <summary>
    /// In-memory ring buffer of recent Teams messages for MCP queries.
    /// Consumes already-classified messages (urgency + priority overrides applied).
    /// </summary>
    public static class MessageHistory
    {
        private const int MaxMessages = 500;
        private static readonly ConcurrentQueue<TeamsMessage> _messages = new();
        private static int _count;

        /// <summary>
        /// Add a classified message to the history. Called from MainWindow.OnTeamsMessageDetected
        /// after urgency classification and priority overrides have been applied.
        /// </summary>
        public static void Add(TeamsMessage message)
        {
            _messages.Enqueue(message);
            var count = System.Threading.Interlocked.Increment(ref _count);

            // Trim oldest when over capacity
            while (count > MaxMessages && _messages.TryDequeue(out _))
            {
                count = System.Threading.Interlocked.Decrement(ref _count);
            }
        }

        /// <summary>
        /// Query recent messages with optional filters.
        /// </summary>
        public static List<TeamsMessage> GetRecent(int minutes = 60, string? sender = null, MessageUrgency? urgency = null)
        {
            var cutoff = DateTime.Now.AddMinutes(-minutes);
            var results = new List<TeamsMessage>();

            foreach (var msg in _messages)
            {
                if (msg.Timestamp < cutoff) continue;

                if (sender != null && !msg.Sender.Contains(sender, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (urgency.HasValue && msg.Urgency != urgency.Value)
                    continue;

                results.Add(msg);
            }

            return results;
        }
    }
}
