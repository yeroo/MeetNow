namespace MeetNow.Recording.Contracts;

public class VadStats
{
    public int SpeechFrames { get; set; }
    public int TotalFrames { get; set; }
    public double SpeechRatio => TotalFrames > 0 ? (double)SpeechFrames / TotalFrames : 0;
}
