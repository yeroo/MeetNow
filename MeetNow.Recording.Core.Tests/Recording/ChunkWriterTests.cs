using System.Text.Json;
using MeetNow.Recording.Contracts;
using MeetNow.Recording.Core.Audio;
using MeetNow.Recording.Core.Recording;
using Xunit;

namespace MeetNow.Recording.Core.Tests.Recording;

public class ChunkWriterTests : IDisposable
{
    private readonly string _chunksDir;

    public ChunkWriterTests()
    {
        _chunksDir = Path.Combine(Path.GetTempPath(), "MeetNowTest_" + Guid.NewGuid().ToString("N")[..8], "chunks");
        Directory.CreateDirectory(_chunksDir);
    }

    public void Dispose()
    {
        var parent = Directory.GetParent(_chunksDir)!.FullName;
        if (Directory.Exists(parent))
            Directory.Delete(parent, recursive: true);
    }

    [Fact]
    public void WriteChunk_CreatesLoopbackAndMicWavFiles()
    {
        var writer = new ChunkWriter(_chunksDir);
        var loopback = GenerateSilence(16000); // 1 second
        var mic = GenerateSilence(16000);

        writer.WriteChunk(
            chunkIndex: 1,
            sessionId: "test-session",
            loopbackSamples: loopback,
            micSamples: mic,
            startTimeUtc: DateTime.UtcNow.AddSeconds(-1),
            endTimeUtc: DateTime.UtcNow,
            speechFrames: 30,
            totalFrames: 33,
            splitReason: SplitReason.SilenceTimeout);

        Assert.True(File.Exists(Path.Combine(_chunksDir, "chunk_001_loopback.wav")));
        Assert.True(File.Exists(Path.Combine(_chunksDir, "chunk_001_mic.wav")));
        Assert.True(File.Exists(Path.Combine(_chunksDir, "chunk_001.json")));
    }

    [Fact]
    public void WriteChunk_JsonHasCorrectMetadata()
    {
        var writer = new ChunkWriter(_chunksDir);
        var start = new DateTime(2026, 3, 27, 14, 32, 5, DateTimeKind.Utc);
        var end = new DateTime(2026, 3, 27, 14, 32, 47, DateTimeKind.Utc);

        writer.WriteChunk(
            chunkIndex: 3,
            sessionId: "2026-03-27_14-32-05",
            loopbackSamples: GenerateSilence(16000),
            micSamples: GenerateSilence(16000),
            startTimeUtc: start,
            endTimeUtc: end,
            speechFrames: 1287,
            totalFrames: 1426,
            splitReason: SplitReason.SilenceTimeout);

        var json = File.ReadAllText(Path.Combine(_chunksDir, "chunk_003.json"));
        var meta = JsonSerializer.Deserialize<ChunkMetadata>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Equal(3, meta.ChunkIndex);
        Assert.Equal("2026-03-27_14-32-05", meta.SessionId);
        Assert.Equal(ChunkStatus.PendingTranscription, meta.Status);
        Assert.Equal(SplitReason.SilenceTimeout, meta.SplitReason);
        Assert.Equal(1287, meta.VadStats.SpeechFrames);
        Assert.Equal(1426, meta.VadStats.TotalFrames);
        Assert.Equal("chunk_003_loopback.wav", meta.LoopbackFile);
        Assert.Equal("chunk_003_mic.wav", meta.MicFile);
    }

    [Fact]
    public void WriteChunk_WavFilesAreValidPcm()
    {
        var writer = new ChunkWriter(_chunksDir);
        short[] samples = [100, -200, 300, -400, 500];
        var padded = new short[16000]; // 1 second
        samples.CopyTo(padded, 0);

        writer.WriteChunk(1, "test", padded, padded,
            DateTime.UtcNow, DateTime.UtcNow, 1, 1, SplitReason.SilenceTimeout);

        // Read WAV header to verify format
        using var reader = new NAudio.Wave.WaveFileReader(Path.Combine(_chunksDir, "chunk_001_loopback.wav"));
        Assert.Equal(16000, reader.WaveFormat.SampleRate);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        Assert.Equal(1, reader.WaveFormat.Channels);
    }

    private static short[] GenerateSilence(int sampleCount)
    {
        return new short[sampleCount];
    }
}
