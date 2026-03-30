using System.IO;
using System.Text.Json;

namespace MeetNow.Recorder.ViewModels;

public class SessionViewModel : BaseViewModel
{
    public string SessionId { get; }
    public string SessionDir { get; }
    public DateTime StartTimeUtc { get; }

    private string _status = "";
    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    private TimeSpan _duration;
    public TimeSpan Duration
    {
        get => _duration;
        private set => SetField(ref _duration, value);
    }

    private int _totalChunks;
    public int TotalChunks
    {
        get => _totalChunks;
        private set => SetField(ref _totalChunks, value);
    }

    private int _transcribedChunks;
    public int TranscribedChunks
    {
        get => _transcribedChunks;
        private set => SetField(ref _transcribedChunks, value);
    }

    private int _failedChunks;
    public int FailedChunks
    {
        get => _failedChunks;
        private set => SetField(ref _failedChunks, value);
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetField(ref _errorMessage, value);
    }

    public bool HasTranscript => File.Exists(TranscriptPath);

    public string DisplayDate
    {
        get
        {
            var local = StartTimeUtc.ToLocalTime();
            var durationStr = Duration.TotalMinutes >= 1
                ? Duration.ToString(@"h\:mm\:ss")
                : $"{Duration.TotalSeconds:F0}s";
            return $"{local:MMM d, h:mm tt}  ({durationStr})";
        }
    }

    public string ProgressText
    {
        get
        {
            if (TotalChunks == 0) return Status;
            if (FailedChunks > 0) return $"{TranscribedChunks}/{TotalChunks} transcribed, {FailedChunks} failed";
            if (TranscribedChunks == TotalChunks) return $"{TotalChunks} chunks transcribed";
            return $"{TranscribedChunks}/{TotalChunks} transcribed";
        }
    }

    public string TranscriptPath => Path.Combine(SessionDir, "transcript.txt");

    /// <summary>
    /// Returns true if status is "recording" but no chunk files have been modified
    /// in the last 2 minutes, indicating the recorder was killed without graceful shutdown.
    /// </summary>
    public bool IsStaleRecording()
    {
        if (Status != "recording")
            return false;

        var chunksDir = Path.Combine(SessionDir, "chunks");
        if (!Directory.Exists(chunksDir))
            return true; // "recording" with no chunks dir = definitely stale

        var newestWrite = Directory.EnumerateFiles(chunksDir)
            .Select(f => File.GetLastWriteTimeUtc(f))
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();

        return (DateTime.UtcNow - newestWrite).TotalMinutes > 2;
    }

    /// <summary>
    /// Force-completes a stale recording session: sets status to "completed",
    /// sets endTimeUtc from the last chunk, and refreshes.
    /// </summary>
    public void ForceComplete()
    {
        var sessionJsonPath = Path.Combine(SessionDir, "session.json");
        if (!File.Exists(sessionJsonPath))
            return;

        // Find the endTimeUtc of the last chunk
        DateTime? lastEndTime = null;
        var chunksDir = Path.Combine(SessionDir, "chunks");
        if (Directory.Exists(chunksDir))
        {
            foreach (var file in Directory.EnumerateFiles(chunksDir, "chunk_*.json"))
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (fileName.Contains("_loopback") || fileName.Contains("_mic"))
                    continue;

                try
                {
                    var chunkJson = File.ReadAllText(file);
                    using var chunkDoc = JsonDocument.Parse(chunkJson);
                    if (chunkDoc.RootElement.TryGetProperty("endTimeUtc", out var endProp) &&
                        endProp.TryGetDateTime(out var endTime))
                    {
                        if (lastEndTime == null || endTime > lastEndTime.Value)
                            lastEndTime = endTime;
                    }
                }
                catch
                {
                    // Skip malformed chunk files
                }
            }
        }

        // Read and rewrite session.json with updated status and endTimeUtc
        try
        {
            var json = File.ReadAllText(sessionJsonPath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
            dict["status"] = JsonSerializer.SerializeToElement("completed");

            if (lastEndTime.HasValue)
            {
                dict["endTimeUtc"] = JsonSerializer.SerializeToElement(lastEndTime.Value);
            }
            else
            {
                // Fallback: use the session.json file's last write time
                dict["endTimeUtc"] = JsonSerializer.SerializeToElement(
                    File.GetLastWriteTimeUtc(sessionJsonPath));
            }

            var updated = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(sessionJsonPath, updated);
        }
        catch
        {
            // Failed to update session.json
        }

        Refresh();
    }

    public SessionViewModel(string sessionDir)
    {
        SessionDir = sessionDir;
        SessionId = Path.GetFileName(sessionDir) ?? "";

        var sessionJsonPath = Path.Combine(sessionDir, "session.json");
        if (File.Exists(sessionJsonPath))
        {
            try
            {
                var json = File.ReadAllText(sessionJsonPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("startTimeUtc", out var startProp) &&
                    startProp.TryGetDateTime(out var startTime))
                {
                    StartTimeUtc = startTime;
                }

                if (root.TryGetProperty("status", out var statusProp))
                {
                    _status = statusProp.GetString() ?? "";
                }

                if (root.TryGetProperty("endTimeUtc", out var endProp) &&
                    endProp.ValueKind != JsonValueKind.Null &&
                    endProp.TryGetDateTime(out var endTime))
                {
                    _duration = endTime - StartTimeUtc;
                }
            }
            catch
            {
                // Malformed session.json — leave defaults
            }
        }

        RefreshTranscriptionProgress();
    }

    public void Refresh()
    {
        var sessionJsonPath = Path.Combine(SessionDir, "session.json");
        if (File.Exists(sessionJsonPath))
        {
            try
            {
                var json = File.ReadAllText(sessionJsonPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("status", out var statusProp))
                {
                    Status = statusProp.GetString() ?? "";
                }

                if (root.TryGetProperty("endTimeUtc", out var endProp) &&
                    endProp.ValueKind != JsonValueKind.Null &&
                    endProp.TryGetDateTime(out var endTime))
                {
                    Duration = endTime - StartTimeUtc;
                }
            }
            catch
            {
                // Malformed session.json — ignore
            }
        }

        RefreshTranscriptionProgress();
        OnPropertyChanged(nameof(HasTranscript));
        OnPropertyChanged(nameof(DisplayDate));
        OnPropertyChanged(nameof(ProgressText));
    }

    private void RefreshTranscriptionProgress()
    {
        var chunksDir = Path.Combine(SessionDir, "chunks");
        if (!Directory.Exists(chunksDir))
        {
            TotalChunks = 0;
            TranscribedChunks = 0;
            FailedChunks = 0;
            return;
        }

        int total = 0;
        int transcribed = 0;
        int failed = 0;

        foreach (var file in Directory.EnumerateFiles(chunksDir, "chunk_*.json"))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);

            // Skip audio-specific metadata files (e.g., chunk_001_loopback, chunk_001_mic)
            if (fileName.Contains("_loopback") || fileName.Contains("_mic"))
                continue;

            total++;

            try
            {
                var json = File.ReadAllText(file);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("status", out var statusProp))
                {
                    var status = statusProp.GetString();
                    if (status == "transcribed")
                        transcribed++;
                    else if (status == "failed")
                        failed++;
                }
            }
            catch
            {
                // Malformed chunk JSON — skip
            }
        }

        TotalChunks = total;
        TranscribedChunks = transcribed;
        FailedChunks = failed;
    }
}
