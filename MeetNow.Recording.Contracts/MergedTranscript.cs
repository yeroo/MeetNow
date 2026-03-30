namespace MeetNow.Recording.Contracts;

public class ChannelTranscripts
{
    public List<MergedSegment> Loopback { get; set; } = [];
    public List<MergedSegment> Mic { get; set; } = [];
}

public class MergedTranscript
{
    public string SessionId { get; set; } = "";
    public string Duration { get; set; } = "";
    public ChannelTranscripts Channels { get; set; } = new();
    public List<MergedSegment> Merged { get; set; } = [];
}
