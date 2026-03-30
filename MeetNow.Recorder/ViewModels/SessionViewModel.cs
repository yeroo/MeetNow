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

    public string TranscriptPath => Path.Combine(SessionDir, "transcript.txt");

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
