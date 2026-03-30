using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MeetNow.Recorder;

/// <summary>
/// Generates a merged transcript.txt from individual chunk transcript JSON files.
/// Interleaves loopback ("Other") and mic ("Me") segments by absolute timestamp.
/// </summary>
public static class TranscriptGenerator
{
    public static void Generate(string sessionDir)
    {
        var sessionJsonPath = Path.Combine(sessionDir, "session.json");
        if (!File.Exists(sessionJsonPath))
            throw new InvalidOperationException("session.json not found");

        var chunksDir = Path.Combine(sessionDir, "chunks");
        var transcriptsDir = Path.Combine(sessionDir, "transcripts");
        if (!Directory.Exists(chunksDir) || !Directory.Exists(transcriptsDir))
            throw new InvalidOperationException("chunks or transcripts directory not found");

        // Read session metadata for header
        DateTime sessionStartUtc;
        using (var doc = JsonDocument.Parse(File.ReadAllText(sessionJsonPath)))
        {
            var root = doc.RootElement;
            sessionStartUtc = root.TryGetProperty("startTimeUtc", out var sp) && sp.TryGetDateTime(out var dt)
                ? dt
                : DateTime.UtcNow;
        }

        // Collect all segments with absolute timestamps
        var allSegments = new List<TranscriptSegment>();

        // Enumerate chunk metadata files (chunk_001.json, chunk_002.json, etc.)
        var chunkFiles = Directory.GetFiles(chunksDir, "chunk_*.json")
            .Where(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                return !name.Contains("_loopback") && !name.Contains("_mic");
            })
            .OrderBy(f => f)
            .ToList();

        foreach (var chunkFile in chunkFiles)
        {
            DateTime chunkStartUtc;
            int chunkIndex;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(chunkFile));
                var root = doc.RootElement;

                chunkIndex = root.GetProperty("chunkIndex").GetInt32();
                chunkStartUtc = root.TryGetProperty("startTimeUtc", out var sp) && sp.TryGetDateTime(out var dt)
                    ? dt
                    : sessionStartUtc;
            }
            catch
            {
                continue; // Skip malformed chunk metadata
            }

            var indexStr = chunkIndex.ToString("D3");

            // Load loopback transcript segments (speaker = "Other")
            LoadTranscriptSegments(
                Path.Combine(transcriptsDir, $"chunk_{indexStr}_loopback.json"),
                chunkStartUtc, "Other", allSegments);

            // Load mic transcript segments (speaker = "Me")
            LoadTranscriptSegments(
                Path.Combine(transcriptsDir, $"chunk_{indexStr}_mic.json"),
                chunkStartUtc, "Me", allSegments);
        }

        if (allSegments.Count == 0)
        {
            // Write an empty transcript for sessions with no speech detected
            var emptyPath = Path.Combine(sessionDir, "transcript.txt");
            File.WriteAllText(emptyPath,
                $"Meeting Transcript \u2014 {Path.GetFileName(sessionDir)}\n(No speech detected)\n",
                Encoding.UTF8);
            return;
        }

        // Sort by absolute time
        allSegments.Sort((a, b) => a.AbsoluteTimeUtc.CompareTo(b.AbsoluteTimeUtc));

        // Compute duration
        var firstTime = allSegments[0].AbsoluteTimeUtc;
        var lastSeg = allSegments[^1];
        var totalDuration = lastSeg.AbsoluteTimeUtc.AddSeconds(lastSeg.DurationSeconds) - firstTime;

        // Build transcript text
        var sb = new StringBuilder();
        var sessionId = Path.GetFileName(sessionDir);
        sb.AppendLine($"Meeting Transcript \u2014 {sessionId}");
        sb.AppendLine($"Duration: {FormatDuration(totalDuration)}");
        sb.AppendLine(new string('=', 60));
        sb.AppendLine();

        string? lastSpeaker = null;
        foreach (var seg in allSegments)
        {
            if (string.IsNullOrWhiteSpace(seg.Text))
                continue;

            // Blank line between speaker changes
            if (lastSpeaker != null && seg.Speaker != lastSpeaker)
                sb.AppendLine();

            var localTime = seg.AbsoluteTimeUtc.ToLocalTime();
            var timeStr = localTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            sb.AppendLine($"[{timeStr}] {seg.Speaker}: {seg.Text.Trim()}");

            lastSpeaker = seg.Speaker;
        }

        // Write transcript.txt
        var transcriptPath = Path.Combine(sessionDir, "transcript.txt");
        File.WriteAllText(transcriptPath, sb.ToString(), Encoding.UTF8);
    }

    private static void LoadTranscriptSegments(
        string transcriptJsonPath, DateTime chunkStartUtc, string speaker,
        List<TranscriptSegment> segments)
    {
        if (!File.Exists(transcriptJsonPath))
            return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(transcriptJsonPath));
            var root = doc.RootElement;

            if (!root.TryGetProperty("segments", out var segmentsArr))
                return;

            foreach (var seg in segmentsArr.EnumerateArray())
            {
                var text = seg.TryGetProperty("text", out var textProp)
                    ? textProp.GetString()?.Trim() ?? ""
                    : "";

                if (string.IsNullOrWhiteSpace(text))
                    continue;

                // Filter Whisper hallucinations: skip segments with mostly non-ASCII text
                if (text.Length > 3)
                {
                    int asciiCount = text.Count(c => c < 128);
                    if ((double)asciiCount / text.Length < 0.5)
                        continue;
                }

                var startSec = seg.TryGetProperty("start", out var startProp)
                    ? startProp.GetDouble() : 0;
                var endSec = seg.TryGetProperty("end", out var endProp)
                    ? endProp.GetDouble() : startSec;

                segments.Add(new TranscriptSegment
                {
                    AbsoluteTimeUtc = chunkStartUtc.AddSeconds(startSec),
                    DurationSeconds = endSec - startSec,
                    Speaker = speaker,
                    Text = text
                });
            }
        }
        catch
        {
            // Skip unreadable transcript JSON
        }
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return ts.ToString(@"h\:mm\:ss");
        return ts.ToString(@"m\:ss");
    }

    private sealed class TranscriptSegment
    {
        public DateTime AbsoluteTimeUtc { get; init; }
        public double DurationSeconds { get; init; }
        public string Speaker { get; init; } = "";
        public string Text { get; init; } = "";
    }
}
