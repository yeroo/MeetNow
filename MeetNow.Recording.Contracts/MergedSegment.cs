namespace MeetNow.Recording.Contracts;

public class MergedSegment
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string Speaker { get; set; } = "";
    public string Text { get; set; } = "";
}
