namespace MeetNow.Recording.Contracts;

public class TranscriptSegment
{
    public double Start { get; set; }
    public double End { get; set; }
    public string Text { get; set; } = "";
    public List<TranscriptWord> Words { get; set; } = [];
}
