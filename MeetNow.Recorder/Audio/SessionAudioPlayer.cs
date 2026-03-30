using NAudio.Wave;
using System.IO;
using System.Text.Json;

namespace MeetNow.Recorder.Audio;

public enum AudioChannel { Loopback, Mic, Both }

public class SessionAudioPlayer : IDisposable
{
    private readonly string _sessionDir;
    private readonly List<ChunkInfo> _chunks = [];
    private AudioChannel _channel = AudioChannel.Loopback;
    private WaveOutEvent? _waveOut;
    private ConcatenatingWaveStream? _reader;
    private bool _disposed;

    public TimeSpan TotalDuration { get; private set; }

    public TimeSpan Position
    {
        get => _reader != null
            ? TimeSpan.FromSeconds((double)_reader.Position / _reader.WaveFormat.AverageBytesPerSecond)
            : TimeSpan.Zero;
    }

    public bool IsPlaying => _waveOut?.PlaybackState == PlaybackState.Playing;

    public SessionAudioPlayer(string sessionDir)
    {
        _sessionDir = sessionDir;
        LoadChunkInfo();
    }

    private void LoadChunkInfo()
    {
        var chunksDir = Path.Combine(_sessionDir, "chunks");
        if (!Directory.Exists(chunksDir))
            return;

        var jsonFiles = Directory.GetFiles(chunksDir, "chunk_*.json")
            .Where(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                return !name.Contains("_loopback") && !name.Contains("_mic");
            })
            .OrderBy(f => f)
            .ToList();

        double cumulativeOffset = 0;

        foreach (var jsonFile in jsonFiles)
        {
            try
            {
                var json = File.ReadAllText(jsonFile);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var chunkIndex = root.GetProperty("chunkIndex").GetInt32();
                var duration = root.GetProperty("durationSeconds").GetDouble();
                var loopbackFile = root.GetProperty("loopbackFile").GetString() ?? "";
                var micFile = root.GetProperty("micFile").GetString() ?? "";

                _chunks.Add(new ChunkInfo
                {
                    Index = chunkIndex,
                    LoopbackPath = Path.Combine(chunksDir, loopbackFile),
                    MicPath = Path.Combine(chunksDir, micFile),
                    StartOffset = cumulativeOffset,
                    Duration = duration
                });

                cumulativeOffset += duration;
            }
            catch
            {
                // Skip malformed chunk metadata
            }
        }

        TotalDuration = TimeSpan.FromSeconds(cumulativeOffset);
    }

    public void Play()
    {
        if (_disposed) return;

        if (_reader == null)
            BuildPipeline();

        if (_reader == null) return;

        _waveOut ??= CreateWaveOut();
        if (_waveOut.PlaybackState == PlaybackState.Stopped)
        {
            _waveOut.Init(_reader);
        }

        _waveOut.Play();
    }

    public void Pause()
    {
        _waveOut?.Pause();
    }

    public void Seek(TimeSpan position)
    {
        if (_reader == null) return;
        long bytePos = (long)(position.TotalSeconds * _reader.WaveFormat.AverageBytesPerSecond);
        // Align to block boundary
        bytePos -= bytePos % _reader.WaveFormat.BlockAlign;
        bytePos = Math.Clamp(bytePos, 0, _reader.Length);
        _reader.Position = bytePos;
    }

    public void SetChannel(AudioChannel channel)
    {
        if (_channel == channel) return;

        var currentPos = Position;
        bool wasPlaying = IsPlaying;

        DisposePipeline();
        _channel = channel;
        BuildPipeline();

        if (_reader != null)
        {
            Seek(currentPos);
            if (wasPlaying)
                Play();
        }
    }

    private void BuildPipeline()
    {
        var streams = new List<WaveStream>();

        foreach (var chunk in _chunks)
        {
            try
            {
                WaveStream? stream = _channel switch
                {
                    AudioChannel.Loopback when File.Exists(chunk.LoopbackPath)
                        => new WaveFileReader(chunk.LoopbackPath),
                    AudioChannel.Mic when File.Exists(chunk.MicPath)
                        => new WaveFileReader(chunk.MicPath),
                    AudioChannel.Both => CreateMixedStream(chunk),
                    _ => null
                };

                if (stream != null)
                    streams.Add(stream);
            }
            catch
            {
                // Skip unreadable chunks
            }
        }

        if (streams.Count == 0) return;

        _reader = new ConcatenatingWaveStream(streams);
        TotalDuration = TimeSpan.FromSeconds((double)_reader.Length / _reader.WaveFormat.AverageBytesPerSecond);
    }

    private static WaveStream? CreateMixedStream(ChunkInfo chunk)
    {
        if (!File.Exists(chunk.LoopbackPath) || !File.Exists(chunk.MicPath))
        {
            // Fall back to whichever exists
            if (File.Exists(chunk.LoopbackPath)) return new WaveFileReader(chunk.LoopbackPath);
            if (File.Exists(chunk.MicPath)) return new WaveFileReader(chunk.MicPath);
            return null;
        }

        return new MixingWaveStream(
            new WaveFileReader(chunk.LoopbackPath),
            new WaveFileReader(chunk.MicPath));
    }

    private WaveOutEvent CreateWaveOut()
    {
        var waveOut = new WaveOutEvent
        {
            DesiredLatency = 200
        };
        return waveOut;
    }

    private void DisposePipeline()
    {
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;
        _reader?.Dispose();
        _reader = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposePipeline();
    }

    private record ChunkInfo
    {
        public int Index { get; init; }
        public string LoopbackPath { get; init; } = "";
        public string MicPath { get; init; } = "";
        public double StartOffset { get; init; }
        public double Duration { get; init; }
    }
}

/// <summary>
/// Concatenates multiple WaveStream instances into a single seekable stream.
/// </summary>
internal class ConcatenatingWaveStream : WaveStream
{
    private readonly List<WaveStream> _streams;
    private readonly long _totalLength;
    private int _currentIndex;
    private long _position;

    public ConcatenatingWaveStream(List<WaveStream> streams)
    {
        if (streams.Count == 0)
            throw new ArgumentException("At least one stream is required.", nameof(streams));

        _streams = streams;
        _totalLength = streams.Sum(s => s.Length);
    }

    public override WaveFormat WaveFormat => _streams[0].WaveFormat;

    public override long Length => _totalLength;

    public override long Position
    {
        get => _position;
        set
        {
            _position = Math.Clamp(value, 0, _totalLength);

            // Find which stream this position falls in
            long offset = 0;
            for (int i = 0; i < _streams.Count; i++)
            {
                if (offset + _streams[i].Length > _position || i == _streams.Count - 1)
                {
                    _currentIndex = i;
                    _streams[i].Position = _position - offset;
                    break;
                }
                offset += _streams[i].Length;
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int totalRead = 0;

        while (totalRead < count && _currentIndex < _streams.Count)
        {
            var stream = _streams[_currentIndex];
            int read = stream.Read(buffer, offset + totalRead, count - totalRead);

            if (read == 0)
            {
                _currentIndex++;
                if (_currentIndex < _streams.Count)
                    _streams[_currentIndex].Position = 0;
                continue;
            }

            totalRead += read;
            _position += read;
        }

        return totalRead;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var stream in _streams)
                stream.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Mixes two mono 16-bit PCM streams into one by averaging samples.
/// </summary>
internal class MixingWaveStream : WaveStream
{
    private readonly WaveStream _stream1;
    private readonly WaveStream _stream2;
    private readonly long _length;

    public MixingWaveStream(WaveStream stream1, WaveStream stream2)
    {
        _stream1 = stream1;
        _stream2 = stream2;
        _length = Math.Max(stream1.Length, stream2.Length);
    }

    public override WaveFormat WaveFormat => _stream1.WaveFormat;

    public override long Length => _length;

    public override long Position
    {
        get => _stream1.Position;
        set
        {
            _stream1.Position = Math.Min(value, _stream1.Length);
            _stream2.Position = Math.Min(value, _stream2.Length);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var buf1 = new byte[count];
        var buf2 = new byte[count];

        int read1 = _stream1.Read(buf1, 0, count);
        int read2 = _stream2.Read(buf2, 0, count);
        int maxRead = Math.Max(read1, read2);

        // Mix as 16-bit samples
        for (int i = 0; i < maxRead - 1; i += 2)
        {
            short s1 = i < read1 ? BitConverter.ToInt16(buf1, i) : (short)0;
            short s2 = i < read2 ? BitConverter.ToInt16(buf2, i) : (short)0;
            short mixed = (short)Math.Clamp((s1 + s2) / 2, short.MinValue, short.MaxValue);
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + i), mixed);
        }

        return maxRead;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stream1.Dispose();
            _stream2.Dispose();
        }
        base.Dispose(disposing);
    }
}
