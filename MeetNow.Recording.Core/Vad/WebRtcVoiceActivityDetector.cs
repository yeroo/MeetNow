using WebRtcVadSharp;

namespace MeetNow.Recording.Core.Vad;

public class WebRtcVoiceActivityDetector : IVoiceActivityDetector
{
    private readonly WebRtcVad _vad;

    public WebRtcVoiceActivityDetector(int aggressiveness = 3)
    {
        _vad = new WebRtcVad
        {
            OperatingMode = aggressiveness switch
            {
                0 => OperatingMode.HighQuality,
                1 => OperatingMode.LowBitrate,
                2 => OperatingMode.Aggressive,
                3 => OperatingMode.VeryAggressive,
                _ => OperatingMode.VeryAggressive
            },
            FrameLength = FrameLength.Is30ms,
            SampleRate = SampleRate.Is16kHz
        };
    }

    public bool IsSpeech(ReadOnlySpan<short> frame)
    {
        // WebRtcVadSharp supports short[] directly — no byte conversion needed
        return _vad.HasSpeech(frame.ToArray());
    }

    public void Dispose()
    {
        _vad.Dispose();
    }
}
