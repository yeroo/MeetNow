using MeetNow.Recording.Core.Recording;
using Xunit;

namespace MeetNow.Recording.Core.Tests.Recording;

public class ChunkTimelineTests
{
    [Fact]
    public void Start_SetsStartTime()
    {
        var timeline = new ChunkTimeline();
        var before = DateTime.UtcNow;
        timeline.Start(chunkIndex: 1);
        var after = DateTime.UtcNow;

        Assert.True(timeline.StartTimeUtc >= before);
        Assert.True(timeline.StartTimeUtc <= after);
        Assert.Equal(1, timeline.ChunkIndex);
        Assert.True(timeline.IsActive);
    }

    [Fact]
    public void Stop_SetsEndTimeAndDuration()
    {
        var timeline = new ChunkTimeline();
        timeline.Start(chunkIndex: 1);
        timeline.AddFrames(100);
        timeline.Stop();

        Assert.False(timeline.IsActive);
        Assert.True(timeline.EndTimeUtc >= timeline.StartTimeUtc);
        Assert.True(timeline.DurationSeconds > 0);
    }

    [Fact]
    public void AddFrames_IncrementsTotalFrames()
    {
        var timeline = new ChunkTimeline();
        timeline.Start(chunkIndex: 1);
        timeline.AddFrames(10);
        timeline.AddFrames(20);

        Assert.Equal(30, timeline.TotalFrames);
    }

    [Fact]
    public void AddSpeechFrame_IncrementsSpeechFrames()
    {
        var timeline = new ChunkTimeline();
        timeline.Start(chunkIndex: 1);
        timeline.AddSpeechFrame();
        timeline.AddSpeechFrame();

        Assert.Equal(2, timeline.SpeechFrames);
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        var timeline = new ChunkTimeline();
        timeline.Start(chunkIndex: 1);
        timeline.AddFrames(50);
        timeline.AddSpeechFrame();
        timeline.Stop();
        timeline.Reset();

        Assert.False(timeline.IsActive);
        Assert.Equal(0, timeline.TotalFrames);
        Assert.Equal(0, timeline.SpeechFrames);
        Assert.Equal(0, timeline.ChunkIndex);
    }

    [Fact]
    public void DurationSeconds_BasedOnFrameCountAndSampleRate()
    {
        var timeline = new ChunkTimeline();
        timeline.Start(chunkIndex: 1);
        // 480 samples per frame at 16kHz = 30ms per frame
        // 100 frames = 3 seconds
        timeline.AddFrames(100);
        timeline.Stop();

        Assert.InRange(timeline.DurationSeconds, 2.9, 3.1);
    }
}
