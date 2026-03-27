using System.Text.Json;
using System.Text.Json.Serialization;
using MeetNow.Recording.Contracts;
using MeetNow.Recording.Core.Config;

namespace MeetNow.Recording.Core.Recording;

public class ActiveSession
{
    public string SessionId { get; }
    public string SessionDir { get; }

    private int _chunkIndex;
    private readonly string _sessionJsonPath;
    private readonly SessionMetadata _metadata;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ActiveSession(string sessionDir, SessionMetadata metadata)
    {
        SessionDir = sessionDir;
        SessionId = metadata.SessionId;
        _metadata = metadata;
        _sessionJsonPath = Path.Combine(sessionDir, "session.json");
    }

    public int NextChunkIndex()
    {
        _chunkIndex++;
        _metadata.ChunkCount = _chunkIndex;
        SaveMetadata();
        return _chunkIndex;
    }

    public void Complete()
    {
        _metadata.Status = SessionStatus.Completed;
        _metadata.EndTimeUtc = DateTime.UtcNow;
        _metadata.ChunkCount = _chunkIndex;
        SaveMetadata();
    }

    private void SaveMetadata()
    {
        var json = JsonSerializer.Serialize(_metadata, JsonOptions);
        File.WriteAllText(_sessionJsonPath, json);
    }
}

public class SessionManager
{
    private readonly RecorderConfig _config;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SessionManager(RecorderConfig config)
    {
        _config = config;
    }

    public bool ShouldStartNewSession(DateTime? lastChunkTimeUtc)
    {
        if (lastChunkTimeUtc == null)
            return true;

        var gap = DateTime.UtcNow - lastChunkTimeUtc.Value;
        return gap.TotalMinutes >= _config.SessionGapMinutes;
    }

    public ActiveSession StartNewSession(string loopbackDevice, string micDevice)
    {
        var sessionId = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
        var sessionDir = Path.Combine(_config.OutputDir, sessionId);

        Directory.CreateDirectory(sessionDir);
        Directory.CreateDirectory(Path.Combine(sessionDir, "chunks"));
        Directory.CreateDirectory(Path.Combine(sessionDir, "transcripts"));

        var metadata = new SessionMetadata
        {
            SessionId = sessionId,
            StartTimeUtc = DateTime.UtcNow,
            Status = SessionStatus.Recording,
            CaptureDevices = new CaptureDeviceInfo
            {
                Loopback = loopbackDevice,
                Microphone = micDevice
            },
            Config = new SessionConfig
            {
                VadMode = _config.VadAggressiveness,
                SilenceTimeoutMs = _config.SilenceTimeoutMs,
                MaxChunkDurationMs = _config.MaxChunkDurationMs
            }
        };

        var json = JsonSerializer.Serialize(metadata, JsonOptions);
        File.WriteAllText(Path.Combine(sessionDir, "session.json"), json);

        return new ActiveSession(sessionDir, metadata);
    }
}
