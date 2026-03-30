namespace MeetNow.Recording.Contracts;

public class ChunkMetadata
{
    public int ChunkIndex { get; set; }
    public string SessionId { get; set; } = "";
    public DateTime StartTimeUtc { get; set; }
    public DateTime EndTimeUtc { get; set; }
    public double DurationSeconds { get; set; }
    public string LoopbackFile { get; set; } = "";
    public string MicFile { get; set; } = "";
    public VadStats VadStats { get; set; } = new();
    public SplitReason SplitReason { get; set; }
    public ChunkStatus Status { get; set; }
    public DateTime? ClaimedAtUtc { get; set; }
    public string? Error { get; set; }
}
