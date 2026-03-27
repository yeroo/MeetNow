using NAudio.Wave;

namespace MeetNow.Recording.Core.Audio;

public static class AudioFormat
{
    public const int SampleRate = 16000;
    public const int Channels = 1;
    public const int BitsPerSample = 16;
    public const int BytesPerSample = BitsPerSample / 8;
    public const int SamplesPerSecond = SampleRate * Channels;
    public const int BytesPerSecond = SamplesPerSecond * BytesPerSample;

    /// <summary>16kHz mono 16-bit PCM — ASR-friendly normalized format.</summary>
    public static readonly WaveFormat WaveFormat = new(SampleRate, BitsPerSample, Channels);

    /// <summary>Number of samples in one VAD frame (30ms at 16kHz).</summary>
    public const int VadFrameSamples = SampleRate * 30 / 1000; // 480

    /// <summary>Pre-buffer capacity in samples for a given duration in seconds.</summary>
    public static int PreBufferSamples(int seconds) => SampleRate * seconds;
}
