namespace MeetNow.Recording.Contracts;

public class TranscriptResult
{
    public int ChunkIndex { get; set; }
    public string Channel { get; set; } = "";
    public string Language { get; set; } = "";
    public double LanguageProbability { get; set; }
    public List<TranscriptSegment> Segments { get; set; } = [];
    public double TranscriptionTimeSeconds { get; set; }
    public string ModelName { get; set; } = "";
    public DateTime TimestampUtcBase { get; set; }
}
