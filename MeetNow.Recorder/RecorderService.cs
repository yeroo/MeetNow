using System.IO;
using MeetNow.Recording.Contracts;
using MeetNow.Recording.Core;
using MeetNow.Recording.Core.Audio;
using MeetNow.Recording.Core.Config;
using MeetNow.Recording.Core.Recording;
using MeetNow.Recording.Core.Vad;
using NAudio.CoreAudioApi;
using Serilog;

namespace MeetNow.Recorder;

public class RecorderService
{
    private readonly RecorderConfig _config;
    private DualChannelCapture? _capture;
    private IVoiceActivityDetector? _loopbackVad;
    private IVoiceActivityDetector? _micVad;
    private RecordingStateMachine? _stateMachine;
    private SessionManager? _sessionManager;
    private ActiveSession? _activeSession;
    private ChunkWriter? _chunkWriter;
    private ChunkTimeline? _timeline;
    private DeviceMonitor? _deviceMonitor;
    private TranscriberProcessManager? _transcriber;

    private readonly List<short> _chunkLoopbackSamples = [];
    private readonly List<short> _chunkMicSamples = [];
    private DateTime? _lastChunkTimeUtc;

    /// <summary>Fired when the recorder state changes.</summary>
    public event Action<RecorderState>? StateChanged;
    public event Action<float, float>? AudioLevelChanged;
    public event Action<int, string>? ChunkFlushed;  // chunkIndex, sessionId
    public event Action<string>? SessionStarted;      // sessionId
    public event Action<string>? SessionCompleted;     // sessionId

    public bool IsRecording => _stateMachine?.State is RecorderState.Recording
        or RecorderState.MicKeepalive or RecorderState.Draining;

    public string? ActiveSessionId => _activeSession?.SessionId;

    public RecorderConfig Config => _config;

    public RecorderService(RecorderConfig config)
    {
        _config = config;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // Ensure output directory exists
        Directory.CreateDirectory(_config.OutputDir);

        // Check disk space
        CheckDiskSpace();

        // Initialize components
        _sessionManager = new SessionManager(_config);
        _timeline = new ChunkTimeline();

        _loopbackVad = new WebRtcVoiceActivityDetector(_config.VadAggressiveness);
        _micVad = new WebRtcVoiceActivityDetector(_config.VadAggressiveness);

        _stateMachine = new RecordingStateMachine(_config);
        _stateMachine.OnRecordingStarted += OnRecordingStarted;
        _stateMachine.OnFlush += OnFlush;
        _stateMachine.OnDiscard += OnDiscard;

        _capture = new DualChannelCapture(_config);
        _capture.OnFrameAvailable += OnFrameAvailable;

        _deviceMonitor = new DeviceMonitor();
        _deviceMonitor.OnDefaultDeviceChanged += OnDeviceChanged;

        // Start transcriber (if enabled)
        if (_config.TranscriberEnabled)
        {
            _transcriber = new TranscriberProcessManager(_config);
            _ = _transcriber.StartAsync(ct);
        }
        else
        {
            Log.Information("Transcriber disabled");
        }

        // Start capture
        _capture.Start();
        Log.Information("Recorder started. Loopback: {Lb}, Mic: {Mic}",
            _capture.LoopbackDeviceName, _capture.MicDeviceName);

        // Periodic disk space check
        using var diskTimer = new PeriodicTimer(TimeSpan.FromMinutes(10));

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await diskTimer.WaitForNextTickAsync(ct);
                CheckDiskSpace();
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown
        }

        Log.Information("Shutting down...");

        // Flush current chunk if recording
        _stateMachine.ForceFlush(SplitReason.ManualStop);

        // Complete active session
        _activeSession?.Complete();
        if (_activeSession != null)
            SessionCompleted?.Invoke(_activeSession.SessionId);

        // Stop capture
        _capture.Stop();

        // Stop transcriber
        _transcriber?.Dispose();

        // Cleanup
        _capture.Dispose();
        _deviceMonitor.Dispose();
        _loopbackVad.Dispose();
        _micVad.Dispose();

        Log.Information("Recorder stopped.");
    }

    private void OnFrameAvailable(short[] loopbackFrame, short[] micFrame)
    {
        bool loopbackSpeech = _loopbackVad!.IsSpeech(loopbackFrame);
        bool micSpeech = _micVad!.IsSpeech(micFrame);

        // Compute RMS audio levels for UI meters
        AudioLevelChanged?.Invoke(ComputeRms(loopbackFrame), ComputeRms(micFrame));

        // Always accumulate into chunk buffers if recording
        if (_stateMachine!.State != RecorderState.Idle)
        {
            _chunkLoopbackSamples.AddRange(loopbackFrame);
            _chunkMicSamples.AddRange(micFrame);
            _timeline!.AddFrames(1);
            if (loopbackSpeech) _timeline.AddSpeechFrame();
        }

        var prevState = _stateMachine.State;
        _stateMachine.ProcessFrame(loopbackSpeech, micSpeech);
        if (_stateMachine.State != prevState)
            StateChanged?.Invoke(_stateMachine.State);
    }

    private static float ComputeRms(short[] samples)
    {
        if (samples.Length == 0) return 0f;
        double sum = 0;
        for (int i = 0; i < samples.Length; i++)
            sum += (double)samples[i] * samples[i];
        return (float)Math.Sqrt(sum / samples.Length) / short.MaxValue;
    }

    private void OnRecordingStarted()
    {
        // Start new session if needed
        if (_activeSession == null || _sessionManager!.ShouldStartNewSession(_lastChunkTimeUtc))
        {
            _activeSession?.Complete();
            _activeSession = _sessionManager!.StartNewSession(
                _capture!.LoopbackDeviceName, _capture.MicDeviceName);
            Log.Information("New session: {Id}", _activeSession.SessionId);
            SessionStarted?.Invoke(_activeSession.SessionId);
        }

        var chunkIndex = _activeSession.NextChunkIndex();
        _chunkWriter = new ChunkWriter(Path.Combine(_activeSession.SessionDir, "chunks"));
        _timeline!.Start(chunkIndex);

        // Drain pre-buffers and prepend to chunk
        var (preLoopback, preMic) = _capture!.DrainPreBuffers();
        _chunkLoopbackSamples.Clear();
        _chunkMicSamples.Clear();
        _chunkLoopbackSamples.AddRange(preLoopback);
        _chunkMicSamples.AddRange(preMic);

        Log.Information("Recording started: chunk {Index}", chunkIndex);
    }

    private void OnFlush(SplitReason reason)
    {
        _timeline!.Stop();

        _chunkWriter!.WriteChunk(
            chunkIndex: _timeline.ChunkIndex,
            sessionId: _activeSession!.SessionId,
            loopbackSamples: [.. _chunkLoopbackSamples],
            micSamples: [.. _chunkMicSamples],
            startTimeUtc: _timeline.StartTimeUtc,
            endTimeUtc: _timeline.EndTimeUtc,
            speechFrames: _timeline.SpeechFrames,
            totalFrames: _timeline.TotalFrames,
            splitReason: reason);

        _lastChunkTimeUtc = DateTime.UtcNow;

        Log.Information("Chunk {Index} flushed ({Duration:F1}s, reason: {Reason})",
            _timeline.ChunkIndex, _timeline.DurationSeconds, reason);

        ChunkFlushed?.Invoke(_timeline.ChunkIndex, _activeSession!.SessionId);

        _chunkLoopbackSamples.Clear();
        _chunkMicSamples.Clear();
        _timeline.Reset();
    }

    private void OnDiscard()
    {
        Log.Debug("Chunk discarded (too short)");
        _chunkLoopbackSamples.Clear();
        _chunkMicSamples.Clear();
        _timeline!.Reset();
    }

    private void OnDeviceChanged(DataFlow flow, string deviceId)
    {
        Log.Warning("Audio device changed: {Flow} → {DeviceId}", flow, deviceId);

        if (_stateMachine!.State != RecorderState.Idle)
        {
            _stateMachine.ForceFlush(SplitReason.DeviceChange);
        }

        // Reinitialize capture
        _capture!.Stop();
        _capture.Dispose();

        _capture = new DualChannelCapture(_config);
        _capture.OnFrameAvailable += OnFrameAvailable;
        _capture.Start();

        Log.Information("Capture reinitialized. Loopback: {Lb}, Mic: {Mic}",
            _capture.LoopbackDeviceName, _capture.MicDeviceName);
    }

    private void CheckDiskSpace()
    {
        var drive = new DriveInfo(Path.GetPathRoot(_config.OutputDir) ?? "C:");
        var freeMb = drive.AvailableFreeSpace / (1024 * 1024);

        if (freeMb < _config.CriticalFreeDiskMb)
        {
            Log.Error("CRITICAL: Only {FreeMb}MB free. Stopping recording.", freeMb);
        }
        else if (freeMb < _config.MinFreeDiskMb)
        {
            Log.Warning("Low disk space: {FreeMb}MB free", freeMb);
        }
    }
}
