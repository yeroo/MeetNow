using MeetNow.Recording.Core.Config;

namespace MeetNow.Recording.Core.Audio;

/// <summary>
/// Orchestrates loopback + mic capture streams.
/// Feeds samples into ring buffers and delivers VAD-sized frames via callback.
/// </summary>
public class DualChannelCapture : IDisposable
{
    private readonly LoopbackCaptureStream _loopback = new();
    private readonly MicrophoneCaptureStream _mic = new();
    private readonly RingBuffer _loopbackPreBuffer;
    private readonly RingBuffer _micPreBuffer;

    private readonly List<short> _loopbackAccumulator = [];
    private readonly List<short> _micAccumulator = [];
    private readonly object _lock = new();

    /// <summary>
    /// Fired for each VAD frame (480 samples / 30ms).
    /// loopbackFrame and micFrame are the frame samples.
    /// </summary>
    public event Action<short[], short[]>? OnFrameAvailable;

    public string LoopbackDeviceName => _loopback.DeviceName;
    public string MicDeviceName => _mic.DeviceName;

    public DualChannelCapture(RecorderConfig config)
    {
        int preBufferSamples = AudioFormat.PreBufferSamples(config.PreBufferSeconds);
        _loopbackPreBuffer = new RingBuffer(preBufferSamples);
        _micPreBuffer = new RingBuffer(preBufferSamples);

        _loopback.OnSamplesAvailable += OnLoopbackSamples;
        _mic.OnSamplesAvailable += OnMicSamples;
    }

    /// <summary>
    /// Drains the pre-buffers and returns the accumulated samples up to this point.
    /// Called when transitioning from IDLE to RECORDING.
    /// </summary>
    public (short[] loopback, short[] mic) DrainPreBuffers()
    {
        lock (_lock)
        {
            return (_loopbackPreBuffer.Drain(), _micPreBuffer.Drain());
        }
    }

    public void Start()
    {
        _loopback.Start();
        _mic.Start();
    }

    public void Stop()
    {
        _loopback.Stop();
        _mic.Stop();
    }

    private void OnLoopbackSamples(short[] samples)
    {
        lock (_lock)
        {
            _loopbackPreBuffer.Write(samples);
            _loopbackAccumulator.AddRange(samples);
            TryEmitFrames();
        }
    }

    private void OnMicSamples(short[] samples)
    {
        lock (_lock)
        {
            _micPreBuffer.Write(samples);
            _micAccumulator.AddRange(samples);
        }
    }

    private void TryEmitFrames()
    {
        // Emit frames driven by loopback timing.
        // If mic has fewer samples, pad with silence to keep channels aligned.
        while (_loopbackAccumulator.Count >= AudioFormat.VadFrameSamples)
        {
            var loopbackFrame = _loopbackAccumulator.GetRange(0, AudioFormat.VadFrameSamples).ToArray();
            _loopbackAccumulator.RemoveRange(0, AudioFormat.VadFrameSamples);

            short[] micFrame;
            if (_micAccumulator.Count >= AudioFormat.VadFrameSamples)
            {
                micFrame = _micAccumulator.GetRange(0, AudioFormat.VadFrameSamples).ToArray();
                _micAccumulator.RemoveRange(0, AudioFormat.VadFrameSamples);
            }
            else
            {
                // Pad with silence if mic is behind
                micFrame = new short[AudioFormat.VadFrameSamples];
            }

            OnFrameAvailable?.Invoke(loopbackFrame, micFrame);
        }
    }

    public void Dispose()
    {
        _loopback.Dispose();
        _mic.Dispose();
    }
}
