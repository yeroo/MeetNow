using MeetNow.Recording.Core.Audio;

namespace MeetNow.Recording.Core.Recording;

/// <summary>
/// Synchronized clock for a chunk across both loopback and mic channels.
/// Tracks start/end timestamps, frame counts, and speech statistics.
/// </summary>
public class ChunkTimeline
{
    public int ChunkIndex { get; private set; }
    public DateTime StartTimeUtc { get; private set; }
    public DateTime EndTimeUtc { get; private set; }
    public bool IsActive { get; private set; }
    public int TotalFrames { get; private set; }
    public int SpeechFrames { get; private set; }

    public double DurationSeconds => TotalFrames * AudioFormat.VadFrameSamples / (double)AudioFormat.SampleRate;

    public void Start(int chunkIndex)
    {
        ChunkIndex = chunkIndex;
        StartTimeUtc = DateTime.UtcNow;
        IsActive = true;
        TotalFrames = 0;
        SpeechFrames = 0;
    }

    public void Stop()
    {
        EndTimeUtc = DateTime.UtcNow;
        IsActive = false;
    }

    public void AddFrames(int count)
    {
        TotalFrames += count;
    }

    public void AddSpeechFrame()
    {
        SpeechFrames++;
    }

    public void Reset()
    {
        ChunkIndex = 0;
        StartTimeUtc = default;
        EndTimeUtc = default;
        IsActive = false;
        TotalFrames = 0;
        SpeechFrames = 0;
    }
}
