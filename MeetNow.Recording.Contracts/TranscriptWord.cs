namespace MeetNow.Recording.Contracts;

public class TranscriptWord
{
    public string Word { get; set; } = "";
    public double Start { get; set; }
    public double End { get; set; }
    public double Probability { get; set; }
}
