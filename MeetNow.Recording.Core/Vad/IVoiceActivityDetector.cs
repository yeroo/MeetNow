namespace MeetNow.Recording.Core.Vad;

/// <summary>
/// Classifies audio frames as speech or non-speech.
/// Implementations must accept 30ms frames of 16kHz mono 16-bit PCM (480 samples).
/// </summary>
public interface IVoiceActivityDetector : IDisposable
{
    bool IsSpeech(ReadOnlySpan<short> frame);
}
