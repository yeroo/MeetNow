using MeetNow.Recording.Contracts;
using MeetNow.Recording.Core.Config;
using MeetNow.Recording.Core.Recording;
using Xunit;

namespace MeetNow.Recording.Core.Tests.Vad;

public class RecordingStateMachineTests
{
    private readonly RecorderConfig _config = new()
    {
        HysteresisRequired = 3,
        HysteresisWindow = 5,
        SilenceTimeoutMs = 3000,
        VadFrameSizeMs = 30,
        MinChunkDurationMs = 2000,
        MaxChunkDurationMs = 300_000,
        MaxChunkGraceMs = 30_000,
        MicKeepaliveMs = 10_000
    };

    private RecordingStateMachine CreateMachine() => new(_config);

    [Fact]
    public void InitialState_IsIdle()
    {
        var sm = CreateMachine();
        Assert.Equal(RecorderState.Idle, sm.State);
    }

    [Fact]
    public void SingleSpeechFrame_DoesNotTransitionToRecording()
    {
        var sm = CreateMachine();
        sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        Assert.Equal(RecorderState.Idle, sm.State);
    }

    [Fact]
    public void HysteresisMetOnLoopback_TransitionsToRecording()
    {
        var sm = CreateMachine();
        sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        Assert.Equal(RecorderState.Recording, sm.State);
    }

    [Fact]
    public void HysteresisWithGaps_StillTransitions()
    {
        var sm = CreateMachine();
        sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: false, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: false, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        Assert.Equal(RecorderState.Recording, sm.State);
    }

    [Fact]
    public void HysteresisNotMet_StaysIdle()
    {
        var sm = CreateMachine();
        sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: false, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: false, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: false, micSpeech: false);
        Assert.Equal(RecorderState.Idle, sm.State);
    }

    [Fact]
    public void MicSpeechAlone_DoesNotOpenChunk()
    {
        var sm = CreateMachine();
        for (int i = 0; i < 10; i++)
            sm.ProcessFrame(loopbackSpeech: false, micSpeech: true);
        Assert.Equal(RecorderState.Idle, sm.State);
    }

    [Fact]
    public void Recording_LoopbackSilence_TransitionsToMicKeepalive_WhenMicActive()
    {
        var sm = CreateMachine();
        for (int i = 0; i < 3; i++)
            sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        Assert.Equal(RecorderState.Recording, sm.State);

        sm.ProcessFrame(loopbackSpeech: false, micSpeech: true);
        Assert.Equal(RecorderState.MicKeepalive, sm.State);
    }

    [Fact]
    public void Recording_LoopbackSilence_TransitionsToDraining_WhenMicSilent()
    {
        var sm = CreateMachine();
        for (int i = 0; i < 3; i++)
            sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);

        sm.ProcessFrame(loopbackSpeech: false, micSpeech: false);
        Assert.Equal(RecorderState.Draining, sm.State);
    }

    [Fact]
    public void MicKeepalive_LoopbackResumes_BackToRecording()
    {
        var sm = CreateMachine();
        for (int i = 0; i < 3; i++)
            sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: false, micSpeech: true);
        Assert.Equal(RecorderState.MicKeepalive, sm.State);

        sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        Assert.Equal(RecorderState.Recording, sm.State);
    }

    [Fact]
    public void MicKeepalive_MicGoesSilent_AdvancesToDraining()
    {
        var sm = CreateMachine();
        for (int i = 0; i < 3; i++)
            sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: false, micSpeech: true);
        Assert.Equal(RecorderState.MicKeepalive, sm.State);

        sm.ProcessFrame(loopbackSpeech: false, micSpeech: false);
        Assert.Equal(RecorderState.Draining, sm.State);
    }

    [Fact]
    public void MicKeepalive_Expires_AdvancesToDraining()
    {
        var sm = CreateMachine();
        for (int i = 0; i < 3; i++)
            sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);

        int keepaliveFrames = _config.MicKeepaliveFrames;
        for (int i = 0; i < keepaliveFrames; i++)
            sm.ProcessFrame(loopbackSpeech: false, micSpeech: true);

        // Next frame should be in Draining (keepalive expired)
        sm.ProcessFrame(loopbackSpeech: false, micSpeech: true);
        Assert.Equal(RecorderState.Draining, sm.State);
    }

    [Fact]
    public void Draining_LoopbackResumes_BackToRecording()
    {
        var sm = CreateMachine();
        for (int i = 0; i < 3; i++)
            sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: false, micSpeech: false);
        Assert.Equal(RecorderState.Draining, sm.State);

        sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        Assert.Equal(RecorderState.Recording, sm.State);
    }

    [Fact]
    public void Draining_MicSpeech_DoesNotResumeChunk()
    {
        var sm = CreateMachine();
        for (int i = 0; i < 3; i++)
            sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: false, micSpeech: false);
        Assert.Equal(RecorderState.Draining, sm.State);

        sm.ProcessFrame(loopbackSpeech: false, micSpeech: true);
        Assert.Equal(RecorderState.Draining, sm.State);
    }

    [Fact]
    public void Draining_SilenceTimeout_TransitionsToFlushing()
    {
        var sm = CreateMachine();
        for (int i = 0; i < 3; i++)
            sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);

        int silenceFrames = _config.SilenceTimeoutFrames;
        SplitReason? reason = null;
        sm.OnFlush += r => reason = r;

        for (int i = 0; i < silenceFrames + 1; i++)
            sm.ProcessFrame(loopbackSpeech: false, micSpeech: false);

        Assert.Equal(RecorderState.Idle, sm.State);
        Assert.Equal(SplitReason.SilenceTimeout, reason);
    }

    [Fact]
    public void MaxChunkDuration_TriggersFlush()
    {
        var config = new RecorderConfig
        {
            HysteresisRequired = 1,
            HysteresisWindow = 1,
            MaxChunkDurationMs = 90,
            MaxChunkGraceMs = 60,
            VadFrameSizeMs = 30,
            SilenceTimeoutMs = 3000,
            MinChunkDurationMs = 0,
            MicKeepaliveMs = 10_000
        };
        var sm = new RecordingStateMachine(config);
        SplitReason? reason = null;
        sm.OnFlush += r => reason = r;

        sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        Assert.Equal(RecorderState.Recording, sm.State);

        // MaxChunkDurationFrames = 90/30 = 3, MaxChunkGraceFrames = 60/30 = 2
        // Frame 1 entered Recording (RC=1). Frames 2-5: RC goes 2,3,4,5.
        // At RC=5: graceFrames = 5 - 3 = 2 >= 2 => flush.
        for (int i = 0; i < 4; i++)
            sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);

        Assert.Equal(RecorderState.Idle, sm.State);
        Assert.Equal(SplitReason.MaxDuration, reason);
    }

    [Fact]
    public void MinChunkDuration_DiscardsTooShortChunks()
    {
        var config = new RecorderConfig
        {
            HysteresisRequired = 1,
            HysteresisWindow = 1,
            MinChunkDurationMs = 2000,
            SilenceTimeoutMs = 30,
            VadFrameSizeMs = 30,
            MaxChunkDurationMs = 300_000,
            MaxChunkGraceMs = 30_000,
            MicKeepaliveMs = 10_000
        };
        var sm = new RecordingStateMachine(config);
        bool flushed = false;
        bool discarded = false;
        sm.OnFlush += _ => flushed = true;
        sm.OnDiscard += () => discarded = true;

        sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        Assert.Equal(RecorderState.Recording, sm.State);

        sm.ProcessFrame(loopbackSpeech: false, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: false, micSpeech: false);

        Assert.Equal(RecorderState.Idle, sm.State);
        Assert.False(flushed);
        Assert.True(discarded);
    }

    [Fact]
    public void FrameCount_IncrementsWhileRecording()
    {
        var sm = CreateMachine();
        for (int i = 0; i < 3; i++)
            sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);

        Assert.Equal(3, sm.RecordingFrameCount);

        sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        Assert.Equal(4, sm.RecordingFrameCount);
    }
}
