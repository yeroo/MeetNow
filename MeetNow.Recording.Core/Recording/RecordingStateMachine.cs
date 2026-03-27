using MeetNow.Recording.Contracts;
using MeetNow.Recording.Core.Config;

namespace MeetNow.Recording.Core.Recording;

public class RecordingStateMachine
{
    private readonly RecorderConfig _config;
    private readonly Queue<bool> _hysteresisWindow = new();

    public RecorderState State { get; private set; } = RecorderState.Idle;
    public int RecordingFrameCount { get; private set; }
    public int SpeechFrameCount { get; private set; }

    private int _silenceFrameCount;
    private int _micKeepaliveFrameCount;
    private bool _maxDurationReached;

    public event Action<SplitReason>? OnFlush;
    public event Action? OnDiscard;
    public event Action? OnRecordingStarted;

    public RecordingStateMachine(RecorderConfig config)
    {
        _config = config;
    }

    public void ProcessFrame(bool loopbackSpeech, bool micSpeech)
    {
        switch (State)
        {
            case RecorderState.Idle:
                ProcessIdle(loopbackSpeech);
                break;
            case RecorderState.Recording:
                ProcessRecording(loopbackSpeech, micSpeech);
                break;
            case RecorderState.MicKeepalive:
                ProcessMicKeepalive(loopbackSpeech, micSpeech);
                break;
            case RecorderState.Draining:
                ProcessDraining(loopbackSpeech);
                break;
        }
    }

    public void ForceFlush(SplitReason reason)
    {
        if (State == RecorderState.Idle)
            return;

        if (RecordingFrameCount >= _config.MinChunkDurationFrames)
        {
            Flush(reason);
        }
        else
        {
            Discard();
        }
    }

    private void ProcessIdle(bool loopbackSpeech)
    {
        _hysteresisWindow.Enqueue(loopbackSpeech);
        if (_hysteresisWindow.Count > _config.HysteresisWindow)
            _hysteresisWindow.Dequeue();

        int speechCount = _hysteresisWindow.Count(x => x);
        if (speechCount >= _config.HysteresisRequired)
        {
            State = RecorderState.Recording;
            RecordingFrameCount = _hysteresisWindow.Count;
            SpeechFrameCount = speechCount;
            _silenceFrameCount = 0;
            _micKeepaliveFrameCount = 0;
            _maxDurationReached = false;
            _hysteresisWindow.Clear();
            OnRecordingStarted?.Invoke();
        }
    }

    private void ProcessRecording(bool loopbackSpeech, bool micSpeech)
    {
        RecordingFrameCount++;
        if (loopbackSpeech) SpeechFrameCount++;

        if (CheckMaxDuration())
            return;

        if (!loopbackSpeech)
        {
            if (micSpeech)
            {
                State = RecorderState.MicKeepalive;
                _micKeepaliveFrameCount = 1;
            }
            else
            {
                State = RecorderState.Draining;
                _silenceFrameCount = 1;
            }
        }
    }

    private void ProcessMicKeepalive(bool loopbackSpeech, bool micSpeech)
    {
        RecordingFrameCount++;
        if (loopbackSpeech) SpeechFrameCount++;

        if (CheckMaxDuration())
            return;

        if (loopbackSpeech)
        {
            State = RecorderState.Recording;
            _micKeepaliveFrameCount = 0;
            return;
        }

        _micKeepaliveFrameCount++;

        if (!micSpeech)
        {
            State = RecorderState.Draining;
            _silenceFrameCount = 1;
            _micKeepaliveFrameCount = 0;
            return;
        }

        if (_micKeepaliveFrameCount > _config.MicKeepaliveFrames)
        {
            State = RecorderState.Draining;
            _silenceFrameCount = 1;
            _micKeepaliveFrameCount = 0;
        }
    }

    private void ProcessDraining(bool loopbackSpeech)
    {
        RecordingFrameCount++;
        if (loopbackSpeech) SpeechFrameCount++;

        if (loopbackSpeech)
        {
            State = RecorderState.Recording;
            _silenceFrameCount = 0;
            return;
        }

        _silenceFrameCount++;

        if (_silenceFrameCount > _config.SilenceTimeoutFrames)
        {
            if (RecordingFrameCount >= _config.MinChunkDurationFrames)
            {
                Flush(SplitReason.SilenceTimeout);
            }
            else
            {
                Discard();
            }
        }
    }

    private bool CheckMaxDuration()
    {
        if (RecordingFrameCount < _config.MaxChunkDurationFrames)
        {
            _maxDurationReached = false;
            return false;
        }

        if (!_maxDurationReached)
            _maxDurationReached = true;

        int graceFrames = RecordingFrameCount - _config.MaxChunkDurationFrames;
        if (graceFrames >= _config.MaxChunkGraceFrames)
        {
            Flush(SplitReason.MaxDuration);
            return true;
        }

        return false;
    }

    private void Flush(SplitReason reason)
    {
        State = RecorderState.Idle;
        RecordingFrameCount = 0;
        SpeechFrameCount = 0;
        _silenceFrameCount = 0;
        _micKeepaliveFrameCount = 0;
        _maxDurationReached = false;
        OnFlush?.Invoke(reason);
    }

    private void Discard()
    {
        State = RecorderState.Idle;
        RecordingFrameCount = 0;
        SpeechFrameCount = 0;
        _silenceFrameCount = 0;
        _micKeepaliveFrameCount = 0;
        _maxDurationReached = false;
        OnDiscard?.Invoke();
    }
}
