using MeetNow.Models;
using Serilog;
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MeetNow
{
    /// <summary>
    /// Classifies Teams message urgency using GitHub Models API (OpenAI-compatible).
    /// Free tier: 50 requests/day for GPT-4o, more for smaller models.
    /// Requires a GitHub Personal Access Token with "models:read" scope.
    /// </summary>
    public class UrgencyClassifier : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _model;
        private bool _disposed;

        private const string GitHubModelsEndpoint = "https://models.inference.ai.azure.com/chat/completions";

        private const string SystemPrompt = @"You are a message urgency classifier for a workplace chat system.
Classify the urgency of the message as exactly one of: urgent, normal, low.

Guidelines:
- URGENT: Direct requests needing immediate action, production issues, blockers, someone explicitly asking for you, time-sensitive deadlines (today/now), escalations, someone waiting for your response to proceed.
- NORMAL: Questions directed at you, task assignments, meeting-related messages, requests with reasonable timelines.
- LOW: FYI messages, general channel discussions not directed at you, automated notifications, thank-you messages, social chat.

If the message mentions the recipient by name or contains @mention, increase urgency by one level.
If it's a direct chat (not a channel), increase urgency by one level.

Respond with ONLY a JSON object: {""urgency"": ""urgent|normal|low"", ""reason"": ""brief explanation""}";

        public UrgencyClassifier(string githubToken, string model = "gpt-4o-mini")
        {
            _model = model;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {githubToken}");
        }

        public async Task<(MessageUrgency urgency, string reason)> ClassifyAsync(TeamsMessage message)
        {
            try
            {
                var userPrompt = new StringBuilder();
                userPrompt.AppendLine($"Sender: {message.Sender}");
                userPrompt.AppendLine($"Chat type: {(message.IsDirectChat ? "direct message" : "channel/group")}");
                userPrompt.AppendLine($"Contains @mention of me: {message.IsMention}");
                userPrompt.AppendLine($"Message: {message.Content}");

                var requestBody = new
                {
                    model = _model,
                    messages = new[]
                    {
                        new { role = "system", content = SystemPrompt },
                        new { role = "user", content = userPrompt.ToString() }
                    },
                    temperature = 0.1,
                    max_tokens = 100
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(GitHubModelsEndpoint, content);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    Log.Warning("GitHub Models API error {StatusCode}: {Error}", response.StatusCode, errorBody);
                    // Fall back to rule-based classification
                    return LocalUrgencyClassifier.Classify(message);
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseJson);
                var choice = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString() ?? "";

                return ParseClassification(choice, message);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error classifying message urgency");
                return FallbackClassify(message);
            }
        }

        private static (MessageUrgency, string) ParseClassification(string aiResponse, TeamsMessage message)
        {
            try
            {
                // Try to parse JSON response
                // Clean the response - remove markdown code fences if present
                var cleaned = aiResponse.Trim();
                if (cleaned.StartsWith("```"))
                {
                    var lines = cleaned.Split('\n');
                    cleaned = string.Join('\n', lines[1..^1]);
                }

                using var doc = JsonDocument.Parse(cleaned);
                var urgencyStr = doc.RootElement.GetProperty("urgency").GetString() ?? "normal";
                var reason = doc.RootElement.TryGetProperty("reason", out var reasonProp)
                    ? reasonProp.GetString() ?? ""
                    : "";

                var urgency = urgencyStr.ToLowerInvariant() switch
                {
                    "urgent" => MessageUrgency.Urgent,
                    "low" => MessageUrgency.Low,
                    _ => MessageUrgency.Normal
                };

                return (urgency, reason);
            }
            catch
            {
                // If AI response isn't valid JSON, try keyword matching
                var lower = aiResponse.ToLowerInvariant();
                if (lower.Contains("urgent"))
                    return (MessageUrgency.Urgent, aiResponse);
                if (lower.Contains("low"))
                    return (MessageUrgency.Low, aiResponse);
                return FallbackClassify(message);
            }
        }

        private static readonly string[] UrgentKeywords = {
            "urgent", "asap", "emergency", "critical", "blocked", "blocker",
            "production issue", "prod issue", "p1", "sev1", "sev 1",
            "immediately", "right now", "right away", "help asap",
            "need your help", "need you", "are you available",
            "waiting for you", "waiting on you", "please respond",
            "deadline today", "due today", "eod", "end of day",
            "escalat", "outage", "down", "broken", "fire"
        };

        private static readonly string[] NormalKeywords = {
            "can you", "could you", "would you", "please",
            "when you get a chance", "question", "review",
            "take a look", "check this", "thoughts on",
            "meeting", "call", "sync", "catch up",
            "assigned", "task", "action item"
        };

        /// <summary>
        /// Rule-based classification using keywords and message context.
        /// </summary>
        public static (MessageUrgency, string) FallbackClassify(TeamsMessage message)
        {
            var lower = message.Content.ToLowerInvariant();

            // Check for urgent keywords
            foreach (var kw in UrgentKeywords)
            {
                if (lower.Contains(kw))
                {
                    // Urgent keyword in DM = urgent
                    if (message.IsDirectChat)
                        return (MessageUrgency.Urgent, $"Direct message with urgent keyword: \"{kw}\"");
                    // Urgent keyword in channel with mention = urgent
                    if (message.IsMention)
                        return (MessageUrgency.Urgent, $"Mentioned with urgent keyword: \"{kw}\"");
                    // Urgent keyword in channel without mention = normal
                    return (MessageUrgency.Normal, $"Channel message with urgent keyword: \"{kw}\"");
                }
            }

            // Direct chat + mention = urgent
            if (message.IsDirectChat && message.IsMention)
                return (MessageUrgency.Urgent, "Direct message with @mention");

            // Check for normal keywords in DM
            if (message.IsDirectChat)
            {
                foreach (var kw in NormalKeywords)
                {
                    if (lower.Contains(kw))
                        return (MessageUrgency.Normal, $"Direct message with request: \"{kw}\"");
                }
                return (MessageUrgency.Normal, "Direct message");
            }

            // Channel mention = normal
            if (message.IsMention)
                return (MessageUrgency.Normal, "Mentioned in channel");

            // Everything else = low
            return (MessageUrgency.Low, "Channel message");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _httpClient.Dispose();
        }
    }
}
