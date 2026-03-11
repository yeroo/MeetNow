using MeetNow.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace MeetNow
{
    /// <summary>
    /// Local urgency classifier using weighted scoring across multiple signal categories:
    /// 1. Lexical signals — weighted keyword/phrase matching
    /// 2. Linguistic signals — caps ratio, punctuation intensity, imperative mood
    /// 3. Contextual signals — DM vs channel, @mention, time of day, message length
    ///
    /// Scores are combined with Bayesian-inspired log-odds and mapped to urgency levels.
    /// No external API required.
    /// </summary>
    public static class LocalUrgencyClassifier
    {
        // --- Lexical signal: weighted word/phrase scores ---
        // Positive = pushes toward urgent, negative = pushes toward low
        private static readonly (string phrase, double weight)[] LexicalScores =
        {
            // Strong urgent signals
            ("urgent", 3.0), ("asap", 3.0), ("emergency", 3.5), ("critical", 3.0),
            ("immediately", 2.8), ("right now", 2.5), ("right away", 2.5),
            ("production issue", 3.5), ("prod issue", 3.5), ("production down", 4.0),
            ("outage", 3.5), ("sev1", 3.5), ("sev 1", 3.5), ("p1", 2.5), ("p0", 3.5),
            ("blocker", 2.5), ("blocked", 2.0), ("blocking", 2.0),
            ("escalat", 2.5), ("incident", 2.5),
            ("need your help", 2.5), ("need you", 2.0), ("waiting for you", 2.5),
            ("waiting on you", 2.5), ("please respond", 2.0),
            ("are you available", 2.0), ("are you free", 1.5), ("are you there", 1.8),
            ("can you join", 2.0), ("join the call", 2.0),
            ("deadline today", 2.5), ("due today", 2.5), ("eod", 2.0), ("end of day", 2.0),
            ("before end of day", 2.5), ("by today", 2.0),
            ("help me", 1.8), ("help us", 1.8), ("sos", 3.0),
            ("broken", 2.0), ("not working", 1.8), ("down", 1.5), ("failing", 2.0),
            ("failed", 1.5), ("error", 1.2), ("crash", 2.0), ("bug", 1.0),

            // Moderate urgency signals
            ("can you", 0.8), ("could you", 0.7), ("would you", 0.6),
            ("please", 0.5), ("review", 0.8), ("take a look", 0.8),
            ("check this", 0.7), ("thoughts on", 0.5), ("feedback", 0.5),
            ("question", 0.6), ("quick question", 1.0), ("any update", 1.2),
            ("update on", 0.8), ("status", 0.6), ("follow up", 0.8), ("follow-up", 0.8),
            ("reminder", 0.8), ("don't forget", 1.0), ("do not forget", 1.0),
            ("meeting", 0.5), ("call", 0.5), ("sync", 0.5), ("catch up", 0.5),
            ("assigned to you", 1.5), ("action item", 1.2), ("task", 0.5),
            ("tomorrow", 0.5), ("this week", 0.3), ("next week", 0.0),

            // Low urgency / anti-signals
            ("fyi", -1.0), ("for your information", -1.0), ("no rush", -1.5),
            ("no hurry", -1.5), ("when you have time", -1.2), ("whenever", -1.0),
            ("low priority", -2.0), ("not urgent", -2.0), ("no action needed", -1.5),
            ("just sharing", -1.0), ("just fyi", -1.5),
            ("thank you", -0.8), ("thanks", -0.5), ("congrats", -1.0),
            ("happy birthday", -1.5), ("welcome", -0.8),
            ("lol", -0.8), ("haha", -0.8), ("nice", -0.5),
            ("good morning", -0.5), ("good afternoon", -0.5), ("good evening", -0.5),
            ("have a good", -0.8), ("enjoy", -0.8),
        };

        // Pre-compiled regex for word boundary matching of short keywords
        private static readonly Regex WordBoundary = new(@"\b", RegexOptions.Compiled);

        // --- Linguistic signal extractors ---

        private static readonly Regex ExclamationRun = new(@"!{2,}", RegexOptions.Compiled);
        private static readonly Regex QuestionRun = new(@"\?{2,}", RegexOptions.Compiled);
        private static readonly Regex AllCapsWord = new(@"\b[A-Z]{3,}\b", RegexOptions.Compiled);
        private static readonly Regex ImperativeStart = new(
            @"^(please\s+)?(do|fix|check|look|send|update|review|help|stop|call|join|respond|reply|answer|handle|resolve|address|escalate|deploy|rollback|revert)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex QuestionPattern = new(@"\?\s*$", RegexOptions.Compiled);
        private static readonly Regex EmojiUrgent = new(@"[🚨🔥⚠️❗❕🆘⛔]", RegexOptions.Compiled);

        public static (MessageUrgency urgency, string reason) Classify(TeamsMessage message)
        {
            var content = message.Content ?? "";
            var lower = content.ToLowerInvariant();
            var reasons = new List<string>();

            double score = 0.0;

            // === 1. Lexical scoring ===
            double lexicalScore = 0;
            string topKeyword = "";
            double topWeight = 0;

            foreach (var (phrase, weight) in LexicalScores)
            {
                if (lower.Contains(phrase))
                {
                    lexicalScore += weight;
                    if (Math.Abs(weight) > Math.Abs(topWeight))
                    {
                        topWeight = weight;
                        topKeyword = phrase;
                    }
                }
            }
            score += lexicalScore;
            if (!string.IsNullOrEmpty(topKeyword) && topWeight > 0)
                reasons.Add($"keyword \"{topKeyword}\"");

            // === 2. Linguistic signals ===
            double linguisticScore = 0;

            // Exclamation intensity: "!!!" = strong signal
            int exclamationCount = content.Count(c => c == '!');
            if (exclamationCount >= 3) { linguisticScore += 1.5; reasons.Add("emphatic punctuation"); }
            else if (exclamationCount >= 1) linguisticScore += 0.3;

            // Multiple question marks
            if (QuestionRun.IsMatch(content))
                linguisticScore += 0.5;

            // ALL CAPS words ratio
            var capsMatches = AllCapsWord.Matches(content);
            int capsWords = capsMatches.Count;
            if (capsWords >= 3) { linguisticScore += 1.5; reasons.Add("emphasis (caps)"); }
            else if (capsWords >= 1) linguisticScore += 0.5;

            // Message starts with imperative verb
            if (ImperativeStart.IsMatch(content.TrimStart()))
                linguisticScore += 0.5;

            // Pure question (ends with ?) — slightly more urgent in DM
            if (QuestionPattern.IsMatch(content))
                linguisticScore += 0.3;

            // Urgent emojis
            if (EmojiUrgent.IsMatch(content))
            {
                linguisticScore += 1.5;
                reasons.Add("urgent emoji");
            }

            // Short messages in DM tend to be more actionable
            if (content.Length < 40 && content.Length > 3)
                linguisticScore += 0.3;

            score += linguisticScore;

            // === 3. Contextual signals ===
            double contextScore = 0;

            // DM boosts urgency significantly
            if (message.IsDirectChat)
            {
                contextScore += 1.5;
                if (reasons.Count == 0) reasons.Add("direct message");
            }

            // @mention boosts urgency
            if (message.IsMention)
            {
                contextScore += 1.5;
                reasons.Add("@mention");
            }

            // Favorite contact boost — people you've pinned in Teams
            if (FavoriteContactsProvider.IsFavoriteContact(message.Sender))
            {
                contextScore += 3.0;
                reasons.Add("favorite contact");
            }

            // Time of day: messages outside business hours may be more urgent
            var hour = message.Timestamp.Hour;
            if (hour < 8 || hour > 19)
            {
                contextScore += 0.5;
                if (score > 2) reasons.Add("after hours");
            }

            score += contextScore;

            // === Map score to urgency level ===
            MessageUrgency urgency;
            if (score >= 4.0)
                urgency = MessageUrgency.Urgent;
            else if (score >= 1.5)
                urgency = MessageUrgency.Normal;
            else
                urgency = MessageUrgency.Low;

            string reason = reasons.Count > 0
                ? string.Join(", ", reasons.Distinct()) + $" (score: {score:F1})"
                : $"No strong signals (score: {score:F1})";

            return (urgency, reason);
        }
    }
}
