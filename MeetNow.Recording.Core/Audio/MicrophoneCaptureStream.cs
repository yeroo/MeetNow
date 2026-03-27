using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetNow.Recording.Core.Audio;

/// <summary>
/// Captures microphone audio via WASAPI,
/// resamples to 16kHz mono 16-bit PCM, and delivers frames via callback.
/// </summary>
public class MicrophoneCaptureStream : IDisposable
{
    private WasapiCapture? _capture;
    private WaveFormat? _sourceFormat;

    public event Action<short[]>? OnSamplesAvailable;
    public string DeviceName { get; private set; } = "";

    public void Start()
    {
        var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        DeviceName = device.FriendlyName;

        _capture = new WasapiCapture(device);
        _sourceFormat = _capture.WaveFormat;

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

        // Reuse the same conversion logic as loopback
        var samples = LoopbackCaptureStream.ConvertToMono16kHz(e.Buffer, e.BytesRecorded, _sourceFormat);
        if (samples.Length > 0)
            OnSamplesAvailable?.Invoke(samples);
    }

    public void Dispose()
    {
        _capture?.StopRecording();
        _capture?.Dispose();
    }
}
