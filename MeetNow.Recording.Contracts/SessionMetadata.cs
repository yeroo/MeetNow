namespace MeetNow.Recording.Contracts;

public class CaptureDeviceInfo
{
    public string Loopback { get; set; } = "";
    public string Microphone { get; set; } = "";
}

public class SessionConfig
{
    public int VadMode { get; set; }
    public int SilenceTimeoutMs { get; set; }
    public int MaxChunkDurationMs { get; set; }
}

public class SessionMetadata
{
    public string SessionId { get; set; } = "";
    public DateTime StartTimeUtc { get; set; }
    public DateTime? EndTimeUtc { get; set; }
    public int ChunkCount { get; set; }
    public SessionStatus Status { get; set; }
    public CaptureDeviceInfo CaptureDevices { get; set; } = new();
    public SessionConfig Config { get; set; } = new();
}
