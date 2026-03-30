using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetNow.Recording.Core.Audio;

/// <summary>
/// Captures system audio output via WASAPI loopback,
/// resamples to 16kHz mono 16-bit PCM, and delivers frames via callback.
/// </summary>
public class LoopbackCaptureStream : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private WaveFormat? _sourceFormat;

    public event Action<short[]>? OnSamplesAvailable;
    public string DeviceName { get; private set; } = "";

    public void Start()
    {
        _capture = new WasapiLoopbackCapture();
        _sourceFormat = _capture.WaveFormat;
        try
        {
            var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            DeviceName = device.FriendlyName;
        }
        catch
        {
            DeviceName = "Unknown";
        }

        _capture.DataAvailable += OnDataAvailable;
        _capture.StartRecording();
    }

    public void Stop()
    {
        _capture?.StopRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || _sourceFormat == null)
            return;

        var samples = ConvertToMono16kHz(e.Buffer, e.BytesRecorded, _sourceFormat);
        if (samples.Length > 0)
            OnSamplesAvailable?.Invoke(samples);
    }

    internal static short[] ConvertToMono16kHz(byte[] buffer, int bytesRecorded, WaveFormat sourceFormat)
    {
        // Source is typically 48kHz/32-bit float/stereo from WASAPI
        int sourceSamples = bytesRecorded / (sourceFormat.BitsPerSample / 8);
        int sourceChannels = sourceFormat.Channels;
        int sourceFrames = sourceSamples / sourceChannels;

        // Downsample ratio
        double ratio = (double)AudioFormat.SampleRate / sourceFormat.SampleRate;
        int outputFrames = (int)(sourceFrames * ratio);

        var output = new short[outputFrames];

        for (int i = 0; i < outputFrames; i++)
        {
            int srcIndex = (int)(i / ratio);
            if (srcIndex >= sourceFrames) srcIndex = sourceFrames - 1;

            float sample = 0;
            // Average all channels to mono
            for (int ch = 0; ch < sourceChannels; ch++)
            {
                int sampleIndex = srcIndex * sourceChannels + ch;
                if (sourceFormat.BitsPerSample == 32 && sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat)
                {
                    int byteOffset = sampleIndex * 4;
                    if (byteOffset + 4 <= bytesRecorded)
                        sample += BitConverter.ToSingle(buffer, byteOffset);
                }
                else if (sourceFormat.BitsPerSample == 16)
                {
                    int byteOffset = sampleIndex * 2;
                    if (byteOffset + 2 <= bytesRecorded)
                        sample += BitConverter.ToInt16(buffer, byteOffset) / 32768f;
                }
            }
            sample /= sourceChannels;

            // Clamp and convert to 16-bit
            sample = Math.Clamp(sample, -1f, 1f);
            output[i] = (short)(sample * 32767);
        }

        return output;
    }

    public void Dispose()
    {
        _capture?.StopRecording();
        _capture?.Dispose();
    }
}
